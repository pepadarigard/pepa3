using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pepa.Adapters.Telegram.Tests;

/// <summary>Прокси для Telegram: ссылки, SOCKS5 на локальном прокси-сервере, сохранение и подключение через прокси.</summary>
public sealed class TelegramProxyTests : IAsyncLifetime
{
    private const string ApiHash = "0123456789abcdef0123456789abcdef";
    private const string Secret = "dd0123456789abcdef0123456789abcdef";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pepa-telegram-proxy-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSocks5 _proxy = new();
    private readonly TcpListener _echo = new(IPAddress.Loopback, 0);

    public ValueTask InitializeAsync()
    {
        _echo.Start();
        _ = EchoForeverAsync();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _echo.Stop();
        await _proxy.DisposeAsync();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private int EchoPort => ((IPEndPoint)_echo.LocalEndpoint).Port;

    private TelegramProxy Socks(string? user = null, string? password = null) =>
        new(TelegramProxyKind.Socks5, "127.0.0.1", _proxy.Port, user, password);

    [Theory]
    [InlineData("tg://socks?server=10.0.0.1&port=1080&user=ivan&pass=секрет", TelegramProxyKind.Socks5, "10.0.0.1", 1080, "ivan", "секрет", null)]
    [InlineData("https://t.me/socks?server=proxy.example&port=9050", TelegramProxyKind.Socks5, "proxy.example", 9050, null, null, null)]
    [InlineData("socks5://ivan:p%40ss@proxy.example:1081", TelegramProxyKind.Socks5, "proxy.example", 1081, "ivan", "p@ss", null)]
    [InlineData("socks5://proxy.example", TelegramProxyKind.Socks5, "proxy.example", 1080, null, null, null)]
    [InlineData("tg://proxy?server=1.2.3.4&port=443&secret=" + Secret, TelegramProxyKind.MTProxy, "1.2.3.4", 443, null, null, Secret)]
    [InlineData("https://t.me/proxy?server=mt.example&port=8443&secret=7gAAAAAAAAAAAAAAAAAAAABnb29nbGUuY29t", TelegramProxyKind.MTProxy, "mt.example", 8443,
        null, null, "7gAAAAAAAAAAAAAAAAAAAABnb29nbGUuY29t")]
    public void Proxy_links_from_Telegram_and_providers_are_understood(string link, TelegramProxyKind kind, string host, int port,
        string? user, string? password, string? secret)
    {
        var proxy = TelegramProxy.ParseLink(link);
        Assert.Equal(new TelegramProxy(kind, host, port, user, password, secret), proxy);
    }

    [Theory]
    [InlineData("просто текст", "не ссылка")]
    [InlineData("https://example.com/proxy?server=a&port=1", "не похожа")]
    [InlineData("tg://proxy?server=1.2.3.4&secret=" + Secret, "нет порта")]
    [InlineData("tg://proxy?server=1.2.3.4&port=443&secret=xyz", "Секрет")]
    [InlineData("tg://socks?server=&port=1080", "адрес прокси")]
    [InlineData("tg://socks?server=a.example&port=70000", "Порт")]
    public void Wrong_proxy_links_are_explained(string link, string expected)
    {
        var error = Assert.Throws<TelegramProxyException>(() => TelegramProxy.ParseLink(link));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Socks5_connects_through_the_proxy_with_login_and_passes_the_address_to_the_proxy()
    {
        _proxy.RequireLogin("ivan", "пароль");
        using var tcp = await Socks5Connector.ConnectAsync(Socks("ivan", "пароль"), "127.0.0.1", EchoPort, TimeSpan.FromSeconds(10), CancellationToken.None);
        var stream = tcp.GetStream();
        await stream.WriteAsync("привет"u8.ToArray(), TestContext.Current.CancellationToken);
        var buffer = new byte["привет"u8.Length];
        await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Equal("привет", Encoding.UTF8.GetString(buffer));
        Assert.Equal($"127.0.0.1:{EchoPort}", Assert.Single(_proxy.Targets));

        // Имя сервера разрешает прокси (запрос имени не идёт в обход).
        using var byName = await Socks5Connector.ConnectAsync(Socks("ivan", "пароль"), "localhost", EchoPort, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal($"localhost:{EchoPort}", _proxy.Targets.Last());
    }

    [Fact]
    public async Task Socks5_failures_are_explained()
    {
        _proxy.RequireLogin("ivan", "пароль");
        var wrong = await Assert.ThrowsAsync<TelegramProxyException>(() =>
            Socks5Connector.ConnectAsync(Socks("ivan", "другой"), "127.0.0.1", EchoPort, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains("не принял логин или пароль", wrong.Message);

        var none = await Assert.ThrowsAsync<TelegramProxyException>(() =>
            Socks5Connector.ConnectAsync(Socks(), "127.0.0.1", EchoPort, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains("требует логин и пароль", none.Message);

        _proxy.RequireLogin(null, null);
        _proxy.Refuse = 5;
        var refused = await Assert.ThrowsAsync<TelegramProxyException>(() =>
            Socks5Connector.ConnectAsync(Socks(), "127.0.0.1", EchoPort, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains("в соединении отказано", refused.Message);

        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var down = await Assert.ThrowsAsync<TelegramProxyException>(() =>
            Socks5Connector.ConnectAsync(new TelegramProxy(TelegramProxyKind.Socks5, "127.0.0.1", closedPort), "127.0.0.1", EchoPort,
                TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains("недоступен", down.Message);
    }

    [Fact]
    public async Task Proxy_is_saved_encrypted_kept_after_restart_and_its_password_is_not_shown()
    {
        _proxy.RequireLogin("ivan", "пароль-прокси");
        await using (var telegram = new TelegramService(_data, _ => Task.CompletedTask) { ServerAddress = $"127.0.0.1:{EchoPort}" })
        {
            await telegram.SetProxyAsync(Socks("ivan", "пароль-прокси"));
            Assert.Equal(new TelegramProxyInfo(TelegramProxyKind.Socks5, "127.0.0.1", _proxy.Port, "ivan", true, false, null), telegram.ProxyInfo);

            // Проверка до сохранения: тот же прокси без пароля в форме — берётся сохранённый.
            var (ok, message) = await telegram.CheckProxyAsync(Socks("ivan"), TestContext.Current.CancellationToken);
            Assert.True(ok, message);
            Assert.Contains("Прокси работает", message);

            var (bad, why) = await telegram.CheckProxyAsync(Socks("ivan", "не тот"), TestContext.Current.CancellationToken);
            Assert.False(bad);
            Assert.Contains("не принял логин или пароль", why);
        }

        var file = await File.ReadAllTextAsync(Path.Combine(_data, "telegram", "proxy.json"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("пароль-прокси", file);
        Assert.DoesNotContain(Uri.EscapeDataString("пароль-прокси"), file);

        await using (var restarted = new TelegramService(_data, _ => Task.CompletedTask))
        {
            Assert.Equal(TelegramProxyKind.Socks5, restarted.ProxyInfo.Kind);
            Assert.True(restarted.ProxyInfo.HasPassword);

            // Тот же прокси без пароля — пароль остаётся; пустая строка — пароль снят.
            await restarted.SetProxyAsync(Socks("ivan"));
            Assert.True(restarted.ProxyInfo.HasPassword);
            await restarted.SetProxyAsync(Socks("ivan", ""));
            Assert.False(restarted.ProxyInfo.HasPassword);

            await restarted.SetProxyAsync(TelegramProxy.None);
            Assert.Equal(TelegramProxyKind.None, restarted.ProxyInfo.Kind);
            Assert.False(File.Exists(Path.Combine(_data, "telegram", "proxy.json")));
        }
    }

    [Fact]
    public async Task Login_goes_through_the_proxy()
    {
        await using var telegram = new TelegramService(_data, _ => Task.CompletedTask)
        {
            ServerAddress = $"127.0.0.1:{EchoPort}",
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        await telegram.SetProxyAsync(Socks());
        // «Сервер» за прокси молчит (эхо не отвечает на запросы Telegram осмысленно) — вход заканчивается понятной ошибкой.
        var error = await Assert.ThrowsAsync<TelegramLoginException>(() => telegram.BeginLoginAsync(new TelegramCredentials(12345, ApiHash, "+79001234567")));
        Assert.Contains($"127.0.0.1:{EchoPort}", _proxy.Targets);
        Assert.Equal(TelegramState.LoginRequired, telegram.Status.State);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Silent_Telegram_behind_the_proxy_gives_a_hint_about_the_proxy()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    held.Add(await silent.AcceptTcpClientAsync());
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }, TestContext.Current.CancellationToken);

        await using var telegram = new TelegramService(_data, _ => Task.CompletedTask)
        {
            ServerAddress = $"127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}",
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        await telegram.SetProxyAsync(Socks());
        var error = await Assert.ThrowsAsync<TelegramLoginException>(() => telegram.BeginLoginAsync(new TelegramCredentials(12345, ApiHash, "+79001234567")));
        Assert.Contains("через прокси", error.Message);
        Assert.Equal(TelegramState.LoginRequired, telegram.Status.State);
        silent.Stop();
        held.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task Unreachable_proxy_stops_the_login_with_the_proxy_named()
    {
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        await using var telegram = new TelegramService(_data, _ => Task.CompletedTask)
        {
            ServerAddress = $"127.0.0.1:{EchoPort}",
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        await telegram.SetProxyAsync(new TelegramProxy(TelegramProxyKind.Socks5, "127.0.0.1", closedPort));
        var error = await Assert.ThrowsAsync<TelegramLoginException>(() => telegram.BeginLoginAsync(new TelegramCredentials(12345, ApiHash, "+79001234567")));
        Assert.Contains("прокси", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_proxy.Targets);
    }

    [Fact]
    public async Task Unreadable_proxy_settings_never_fall_back_to_a_direct_connection()
    {
        Directory.CreateDirectory(Path.Combine(_data, "telegram"));
        await File.WriteAllTextAsync(Path.Combine(_data, "telegram", "proxy.json"), "{испорчено", TestContext.Current.CancellationToken);
        await using var telegram = new TelegramService(_data, _ => Task.CompletedTask) { ServerAddress = $"127.0.0.1:{EchoPort}" };
        Assert.Contains("не прочитались", telegram.ProxyInfo.Problem);

        var error = await Assert.ThrowsAsync<TelegramLoginException>(() => telegram.BeginLoginAsync(new TelegramCredentials(12345, ApiHash, "+79001234567")));
        Assert.Contains("прокси", error.Message, StringComparison.OrdinalIgnoreCase);

        await telegram.SetProxyAsync(TelegramProxy.None);
        Assert.Null(telegram.ProxyInfo.Problem);
    }

    private async Task EchoForeverAsync()
    {
        try
        {
            while (true)
            {
                var client = await _echo.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await client.GetStream().CopyToAsync(client.GetStream());
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                        {
                        }
                    }
                });
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Простой SOCKS5-прокси: вход по логину и паролю (если задан), CONNECT, пересылка данных; запоминает адреса.</summary>
    private sealed class FakeSocks5 : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _targets = new();
        private volatile Tuple<string, string>? _login;

        public FakeSocks5()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyCollection<string> Targets => _targets.ToArray();

        /// <summary>Код отказа на CONNECT (0 — соединять).</summary>
        public byte Refuse { get; set; }

        public void RequireLogin(string? user, string? password) => _login = user is null ? null : Tuple.Create(user, password ?? "");

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var token = _stop.Token;
                    var stream = client.GetStream();
                    var head = await Read(stream, 2, token);
                    var methods = await Read(stream, head[1], token);
                    var login = _login;
                    if (login is { } required)
                    {
                        if (!methods.Contains((byte)2))
                        {
                            await stream.WriteAsync(new byte[] { 5, 0xFF }, token);
                            return;
                        }

                        await stream.WriteAsync(new byte[] { 5, 2 }, token);
                        var version = await Read(stream, 2, token);
                        var user = Encoding.UTF8.GetString(await Read(stream, version[1], token));
                        var passwordLength = await Read(stream, 1, token);
                        var password = Encoding.UTF8.GetString(await Read(stream, passwordLength[0], token));
                        var ok = user == required.Item1 && password == required.Item2;
                        await stream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, token);
                        if (!ok)
                        {
                            return;
                        }
                    }
                    else
                    {
                        await stream.WriteAsync(new byte[] { 5, 0 }, token);
                    }

                    var request = await Read(stream, 4, token);
                    string host;
                    switch (request[3])
                    {
                        case 1:
                            host = new IPAddress(await Read(stream, 4, token)).ToString();
                            break;
                        case 4:
                            host = new IPAddress(await Read(stream, 16, token)).ToString();
                            break;
                        default:
                            var length = await Read(stream, 1, token);
                            host = Encoding.ASCII.GetString(await Read(stream, length[0], token));
                            break;
                    }

                    var portBytes = await Read(stream, 2, token);
                    var port = (portBytes[0] << 8) | portBytes[1];
                    if (Refuse != 0)
                    {
                        await stream.WriteAsync(new byte[] { 5, Refuse, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
                        return;
                    }

                    _targets.Enqueue($"{host}:{port}");
                    using var target = new TcpClient();
                    await target.ConnectAsync(host == "localhost" ? "127.0.0.1" : host, port, token);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
                    var upstream = target.GetStream();
                    await Task.WhenAny(stream.CopyToAsync(upstream, token), upstream.CopyToAsync(stream, token));
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }
        }

        private static async Task<byte[]> Read(NetworkStream stream, int count, CancellationToken token)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, token);
            return buffer;
        }
    }
}
