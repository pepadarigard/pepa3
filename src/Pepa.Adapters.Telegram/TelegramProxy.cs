using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pepa.Adapters.Telegram;

public enum TelegramProxyKind
{
    None,
    Socks5,
    MTProxy,
}

/// <summary>
/// Прокси для связи с Telegram — когда Telegram недоступен в сети напрямую. SOCKS5 (с логином и паролем или без) или
/// MTProxy (прокси Telegram с секретом). Пароль и секрет хранятся зашифрованными и наружу не отдаются.
/// </summary>
public sealed record TelegramProxy(
    TelegramProxyKind Kind,
    string Host = "",
    int Port = 0,
    string? Username = null,
    string? Password = null,
    string? Secret = null)
{
    public static TelegramProxy None { get; } = new(TelegramProxyKind.None);

    /// <summary>«хост:порт» для сообщений.</summary>
    public string Address => Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>Описание для интерфейса и журнала — без пароля и секрета.</summary>
    public string Describe() => Kind switch
    {
        TelegramProxyKind.Socks5 => $"SOCKS5 {Address}" + (Username is { Length: > 0 } user ? $" (логин {user})" : ""),
        TelegramProxyKind.MTProxy => $"MTProxy {Address}",
        _ => "без прокси",
    };

    /// <summary>Адрес для библиотеки: она принимает MTProxy ссылкой.</summary>
    internal string MTProxyUrl =>
        $"https://t.me/proxy?server={Uri.EscapeDataString(Host)}&port={Port.ToString(CultureInfo.InvariantCulture)}&secret={Uri.EscapeDataString(Secret ?? "")}";

    /// <summary>
    /// Ссылка на прокси из Telegram или от поставщика: <c>tg://socks?server=…&amp;port=…&amp;user=…&amp;pass=…</c>,
    /// <c>https://t.me/socks?…</c>, <c>tg://proxy?server=…&amp;port=…&amp;secret=…</c>, <c>https://t.me/proxy?…</c>,
    /// а также <c>socks5://логин:пароль@хост:порт</c>.
    /// </summary>
    public static TelegramProxy ParseLink(string link)
    {
        var text = link.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new TelegramProxyException("Это не ссылка на прокси. Пример: tg://proxy?server=…&port=…&secret=… или socks5://хост:порт.");
        }

        if (uri.Scheme is "socks5" or "socks5h" or "socks")
        {
            var credentials = uri.UserInfo.Split(':', 2);
            return new TelegramProxy(TelegramProxyKind.Socks5, uri.Host.Trim('[', ']'), uri.IsDefaultPort ? 1080 : uri.Port,
                credentials[0].Length > 0 ? Uri.UnescapeDataString(credentials[0]) : null,
                credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null).Validated();
        }

        var kind = (uri.Scheme, uri.Host.ToLowerInvariant(), uri.AbsolutePath.Trim('/').ToLowerInvariant()) switch
        {
            ("tg", "socks", _) => TelegramProxyKind.Socks5,
            ("tg", "proxy", _) => TelegramProxyKind.MTProxy,
            ("https" or "http", "t.me" or "telegram.me", "socks") => TelegramProxyKind.Socks5,
            ("https" or "http", "t.me" or "telegram.me", "proxy") => TelegramProxyKind.MTProxy,
            _ => throw new TelegramProxyException("Ссылка не похожа на прокси Telegram: нужна tg://proxy, tg://socks, t.me/proxy, t.me/socks или socks5://."),
        };

        var query = Query(uri.Query);
        var server = query.GetValueOrDefault("server") ?? "";
        if (!int.TryParse(query.GetValueOrDefault("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            throw new TelegramProxyException("В ссылке на прокси нет порта (port=…).");
        }

        return (kind == TelegramProxyKind.Socks5
            ? new TelegramProxy(kind, server, port, query.GetValueOrDefault("user"), query.GetValueOrDefault("pass"))
            : new TelegramProxy(kind, server, port, Secret: query.GetValueOrDefault("secret"))).Validated();
    }

    /// <summary>Проверенная и очищенная копия; неверные данные — <see cref="TelegramProxyException"/> с понятной причиной.</summary>
    public TelegramProxy Validated()
    {
        if (Kind == TelegramProxyKind.None)
        {
            return None;
        }

        var host = Host.Trim().Trim('[', ']');
        if (host.Length == 0 || host.Length > 253 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '?' or '#' or '@'))
        {
            throw new TelegramProxyException("Укажите адрес прокси: имя или IP-адрес, без «http://» и порта.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new TelegramProxyException("Порт прокси — число от 1 до 65535.");
        }

        if (Kind == TelegramProxyKind.Socks5)
        {
            var user = string.IsNullOrEmpty(Username) ? null : Username;
            var password = string.IsNullOrEmpty(Password) ? null : Password;
            if (user is null && password is not null)
            {
                throw new TelegramProxyException("Указан пароль прокси без логина.");
            }

            if (Encoding.UTF8.GetByteCount(user ?? "") > 255 || Encoding.UTF8.GetByteCount(password ?? "") > 255)
            {
                throw new TelegramProxyException("Логин и пароль SOCKS5 — не длиннее 255 байт.");
            }

            return new TelegramProxy(Kind, host, Port, user, password);
        }

        var secret = (Secret ?? "").Trim();
        if (!IsSecret(secret))
        {
            throw new TelegramProxyException("Секрет MTProxy — строка из ссылки на прокси (secret=…): шестнадцатеричная (от 32 символов) или base64.");
        }

        return new TelegramProxy(Kind, host, Port, Secret: secret);
    }

    private static bool IsSecret(string secret)
    {
        if (secret.Length >= 32 && secret.Length % 2 == 0 && secret.All(Uri.IsHexDigit))
        {
            return true;
        }

        // Base64 (обычный и для адресов): 16 байт ключа и больше.
        return secret.Length >= 22 && secret.TrimEnd('=').All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_');
    }

    private static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            values[Uri.UnescapeDataString(pair[0])] = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "";
        }

        return values;
    }
}

/// <summary>Сохранённый прокси для интерфейса: без пароля и секрета.</summary>
/// <param name="Problem">Настройки прокси не прочитались — подключение не идёт, пока прокси не задан заново.</param>
public sealed record TelegramProxyInfo(TelegramProxyKind Kind, string Host, int Port, string? Username, bool HasPassword, bool HasSecret, string? Problem);

/// <summary>Неверные данные прокси или прокси не пропустил соединение — с понятной причиной.</summary>
/// <remarks>Не <see cref="IOException"/>: библиотека Telegram на сбоях сокета пробует другие адреса, а эту причину должна отдать как есть.</remarks>
public sealed class TelegramProxyException(string message) : Exception(message);

/// <summary>
/// Соединение через SOCKS5 (RFC 1928, вход по логину и паролю — RFC 1929). Адрес назначения передаётся прокси как есть
/// (имя разрешает прокси), поэтому ни соединение, ни запрос имени не идут в обход прокси.
/// </summary>
public static class Socks5Connector
{
    public static async Task<TcpClient> ConnectAsync(TelegramProxy proxy, string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var token = limit.Token;
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            try
            {
                await tcp.ConnectAsync(proxy.Host, proxy.Port, token);
            }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new TelegramProxyException(ex is SocketException socket
                    ? $"Прокси {proxy.Address} недоступен ({socket.SocketErrorCode}). Проверьте адрес и порт."
                    : $"Прокси {proxy.Address} не ответил за {timeout.TotalSeconds:0} с.");
            }

            var stream = tcp.GetStream();
            try
            {
                await HandshakeAsync(stream, proxy, host, port, token);
            }
            catch (Exception ex) when (ex is IOException or SocketException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new TelegramProxyException(ex is OperationCanceledException
                    ? $"Прокси {proxy.Address} не ответил за {timeout.TotalSeconds:0} с."
                    : $"Прокси {proxy.Address} оборвал соединение — возможно, это не SOCKS5-прокси или он требует другой способ входа.");
            }

            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task HandshakeAsync(NetworkStream stream, TelegramProxy proxy, string host, int port, CancellationToken token)
    {
        var withLogin = proxy.Username is { Length: > 0 };
        await stream.WriteAsync(withLogin ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, token);
        var choice = await ReadAsync(stream, 2, token);
        if (choice[0] != 5)
        {
            throw new TelegramProxyException($"По адресу {proxy.Address} не SOCKS5-прокси.");
        }

        switch (choice[1])
        {
            case 0:
                break;
            case 2 when withLogin:
            {
                var user = Encoding.UTF8.GetBytes(proxy.Username!);
                var password = Encoding.UTF8.GetBytes(proxy.Password ?? "");
                var login = new byte[3 + user.Length + password.Length];
                login[0] = 1;
                login[1] = (byte)user.Length;
                user.CopyTo(login, 2);
                login[2 + user.Length] = (byte)password.Length;
                password.CopyTo(login, 3 + user.Length);
                await stream.WriteAsync(login, token);
                var reply = await ReadAsync(stream, 2, token);
                if (reply[1] != 0)
                {
                    throw new TelegramProxyException($"Прокси {proxy.Address} не принял логин или пароль.");
                }

                break;
            }

            case 2 or 0xFF when !withLogin:
                throw new TelegramProxyException($"Прокси {proxy.Address} требует логин и пароль.");
            default:
                throw new TelegramProxyException($"Прокси {proxy.Address} не поддерживает вход по логину и паролю.");
        }

        await stream.WriteAsync(ConnectRequest(host, port), token);
        var head = await ReadAsync(stream, 4, token);
        if (head[1] != 0)
        {
            throw new TelegramProxyException($"Прокси {proxy.Address} не соединился с Telegram: {Reason(head[1])}.");
        }

        // Адрес, который прокси выделил для соединения, не нужен — его надо только дочитать.
        var rest = head[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadAsync(stream, 1, token))[0],
            _ => throw new TelegramProxyException($"Прокси {proxy.Address} ответил непонятно (тип адреса {head[3]})."),
        };
        await ReadAsync(stream, rest + 2, token);
    }

    private static byte[] ConnectRequest(string host, int port)
    {
        byte[] address;
        byte type;
        if (IPAddress.TryParse(host, out var ip))
        {
            address = ip.GetAddressBytes();
            type = ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1;
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255)
            {
                throw new TelegramProxyException("Слишком длинное имя сервера для SOCKS5.");
            }

            address = [(byte)name.Length, .. name];
            type = 3;
        }

        return [5, 1, 0, type, .. address, (byte)(port >> 8), (byte)port];
    }

    private static string Reason(byte code) => code switch
    {
        1 => "общий сбой прокси",
        2 => "правила прокси запрещают это соединение",
        3 => "сеть недоступна",
        4 => "сервер недоступен",
        5 => "в соединении отказано",
        6 => "истекло время",
        7 => "команда не поддерживается",
        8 => "тип адреса не поддерживается",
        _ => $"код {code}",
    };

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, token);
        return buffer;
    }
}
