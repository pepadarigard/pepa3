using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Pepa.Adapters.Email.Tests;

/// <summary>
/// Почтовый сервер IMAP для проверок (без шифрования, на 127.0.0.1): ровно те команды, которые нужны клиенту MailKit, —
/// вход, список папок, открытие папки, поиск и чтение писем по номерам (UID). Запоминает команды, чтобы проверить, что
/// Pepa ничего в ящике не меняет.
/// </summary>
public sealed partial class FakeImapServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly object _gate = new();
    private readonly Dictionary<string, Mailbox> _folders = new(StringComparer.OrdinalIgnoreCase) { ["INBOX"] = new Mailbox("INBOX") };

    public FakeImapServer(string login = "anna@example.com", string password = "app-pass-1")
    {
        Login = login;
        Password = password;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public string Login { get; }

    /// <summary>Пароль ящика (можно сменить «на сервере» — как отозванный пароль приложения).</summary>
    public string Password { get; set; }

    /// <summary>Письма, которые сервер не отдаёт (ответ NO на чтение) — как повреждённое письмо на сервере.</summary>
    public ConcurrentDictionary<uint, bool> Unreadable { get; } = new();

    /// <summary>Не сообщать время прихода писем (INTERNALDATE) — так бывает у некоторых серверов.</summary>
    public bool WithoutInternalDate { get; set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Все полученные команды (без меток и паролей).</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public int Logins;

    private readonly ConcurrentDictionary<TcpClient, bool> _connections = new();

    /// <summary>Оборвать все соединения (сервер перезапустился, пропала связь).</summary>
    public void DropConnections()
    {
        foreach (var client in _connections.Keys)
        {
            client.Close();
        }
    }

    private sealed class Mailbox(string name)
    {
        public string Name { get; } = name;
        public uint UidValidity { get; set; } = 1;
        public uint NextUid { get; set; } = 1;
        public List<(uint Uid, byte[] Raw, DateTimeOffset Received)> Messages { get; } = [];
    }

    public void AddFolder(string name)
    {
        lock (_gate)
        {
            _folders[name] = new Mailbox(name);
        }
    }

    /// <summary>Положить письмо в папку (как будто оно пришло).</summary>
    public uint Deliver(string raw, DateTimeOffset? received = null, string folder = "INBOX")
    {
        lock (_gate)
        {
            var box = _folders[folder];
            var uid = box.NextUid++;
            box.Messages.Add((uid, Encoding.UTF8.GetBytes(raw.ReplaceLineEndings("\r\n")), received ?? DateTimeOffset.UtcNow));
            return uid;
        }
    }

    /// <summary>Ящик заново пронумеровал письма (UIDVALIDITY сменился).</summary>
    public void Renumber(string folder = "INBOX")
    {
        lock (_gate)
        {
            var box = _folders[folder];
            box.UidValidity++;
            for (var i = 0; i < box.Messages.Count; i++)
            {
                box.Messages[i] = box.Messages[i] with { Uid = (uint)(i + 1) };
            }

            box.NextUid = (uint)box.Messages.Count + 1;
        }
    }

    public static string Message(string from, string subject, string body, string? to = "anna@example.com", string? id = null, bool html = false) =>
        $"From: {from}\r\nTo: {to}\r\nSubject: =?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))}?=\r\n" +
        $"Message-ID: <{id ?? Guid.NewGuid().ToString("N")}@example.com>\r\nDate: Wed, 28 Oct 2026 10:00:00 +0000\r\nMIME-Version: 1.0\r\n" +
        $"Content-Type: text/{(html ? "html" : "plain")}; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(body)) + "\r\n";

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        _connections[client] = true;
        var stream = client.GetStream();
        var reader = new LineReader(stream);
        Mailbox? open = null;
        var reportedCount = 0;
        var loggedIn = false;
        try
        {
            await Write(stream, "* OK [CAPABILITY IMAP4rev1] Fake IMAP ready\r\n");
            while (!_stop.IsCancellationRequested)
            {
                var line = await reader.ReadCommandAsync(stream, _stop.Token);
                if (line is null)
                {
                    return;
                }

                var space = line.IndexOf(' ');
                var tag = line[..space];
                var rest = line[(space + 1)..];
                var command = rest.Split(' ', 2)[0].ToUpperInvariant();
                var args = rest.Length > command.Length ? rest[(command.Length + 1)..] : "";
                Commands.Enqueue(command == "LOGIN" ? "LOGIN" : rest);
                switch (command)
                {
                    case "CAPABILITY":
                        await Write(stream, $"* CAPABILITY IMAP4rev1\r\n{tag} OK done\r\n");
                        break;
                    case "NOOP" or "CHECK":
                        if (open is not null)
                        {
                            int count;
                            lock (_gate)
                            {
                                count = open.Messages.Count;
                            }

                            if (count != reportedCount)
                            {
                                await Write(stream, $"* {count} EXISTS\r\n");
                                reportedCount = count;
                            }
                        }

                        await Write(stream, $"{tag} OK done\r\n");
                        break;
                    case "LOGIN":
                        var credentials = Tokens(args);
                        if (credentials.Count == 2 && credentials[0] == Login && credentials[1] == Password)
                        {
                            loggedIn = true;
                            Interlocked.Increment(ref Logins);
                            await Write(stream, $"{tag} OK [CAPABILITY IMAP4rev1] Logged in\r\n");
                        }
                        else
                        {
                            await Write(stream, $"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials\r\n");
                        }

                        break;
                    case "LIST" when loggedIn:
                        var pattern = Tokens(args) is [_, var p] ? DecodeName(p) : "";
                        var text = new StringBuilder();
                        if (pattern.Length == 0)
                        {
                            text.Append("* LIST (\\Noselect) \"/\" \"\"\r\n");
                        }
                        else
                        {
                            lock (_gate)
                            {
                                foreach (var name in _folders.Keys.Where(n => pattern is "*" or "%" || n.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                                {
                                    text.Append($"* LIST (\\HasNoChildren) \"/\" \"{EncodeName(name)}\"\r\n");
                                }
                            }
                        }

                        await Write(stream, text + $"{tag} OK done\r\n");
                        break;
                    case "SELECT" or "EXAMINE" when loggedIn:
                        var folderName = DecodeName(Tokens(args).FirstOrDefault() ?? "");
                        Mailbox? box;
                        lock (_gate)
                        {
                            _folders.TryGetValue(folderName, out box);
                        }

                        if (box is null)
                        {
                            await Write(stream, $"{tag} NO [NONEXISTENT] No such folder\r\n");
                            break;
                        }

                        open = box;
                        uint validity, next;
                        lock (_gate)
                        {
                            reportedCount = box.Messages.Count;
                            validity = box.UidValidity;
                            next = box.NextUid;
                        }

                        await Write(stream,
                            "* FLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)\r\n" +
                            $"* {reportedCount} EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY {validity}] UIDs valid\r\n* OK [UIDNEXT {next}] next\r\n" +
                            $"* OK [PERMANENTFLAGS ()] read-only\r\n{tag} OK [{(command == "EXAMINE" ? "READ-ONLY" : "READ-WRITE")}] {command} completed\r\n");
                        break;
                    case "UID" when open is not null:
                        await UidAsync(stream, tag, args, open);
                        break;
                    case "LOGOUT":
                        await Write(stream, $"* BYE bye\r\n{tag} OK done\r\n");
                        return;
                    default:
                        await Write(stream, $"{tag} BAD not supported\r\n");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
        }
        finally
        {
            _connections.TryRemove(client, out var _);
        }
    }

    private async Task UidAsync(NetworkStream stream, string tag, string args, Mailbox box)
    {
        var parts = args.Split(' ', 3);
        var sub = parts[0].ToUpperInvariant();
        List<(int Seq, uint Uid, byte[] Raw, DateTimeOffset Received)> messages;
        lock (_gate)
        {
            messages = box.Messages.Select((m, i) => (i + 1, m.Uid, m.Raw, m.Received)).ToList();
        }

        var max = messages.Count > 0 ? messages[^1].Uid : 0;
        if (sub == "SEARCH")
        {
            var query = args[(sub.Length + 1)..].Trim();
            var found = query.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                ? messages.Select(m => m.Uid)
                : messages.Select(m => m.Uid).Where(Set(query.Split(' ').Last(), max));
            await Write(stream, $"* SEARCH {string.Join(' ', found)}\r\n{tag} OK done\r\n");
            return;
        }

        if (sub != "FETCH")
        {
            await Write(stream, $"{tag} BAD not supported\r\n");
            return;
        }

        var inSet = Set(parts[1], max);
        var items = parts.Length > 2 ? parts[2].ToUpperInvariant() : "";
        foreach (var message in messages.Where(m => inSet(m.Uid)))
        {
            var fields = new List<string> { $"UID {message.Uid}" };
            if (items.Contains("RFC822.SIZE", StringComparison.Ordinal))
            {
                fields.Add($"RFC822.SIZE {message.Raw.Length}");
            }

            if (items.Contains("INTERNALDATE", StringComparison.Ordinal) && !WithoutInternalDate)
            {
                fields.Add($"INTERNALDATE \"{message.Received.ToUniversalTime().ToString("dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture)} +0000\"");
            }

            if (items.Contains("FLAGS", StringComparison.Ordinal))
            {
                fields.Add("FLAGS ()");
            }

            byte[]? literal = null;
            string? section = null;
            if (Unreadable.ContainsKey(message.Uid) && items.Contains("BODY", StringComparison.Ordinal))
            {
                await Write(stream, $"{tag} NO Some messages could not be FETCHed (Failure)\r\n");
                return;
            }

            if (items.Contains("BODY.PEEK[HEADER]", StringComparison.Ordinal))
            {
                var text = Encoding.UTF8.GetString(message.Raw);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                literal = Encoding.UTF8.GetBytes(end >= 0 ? text[..(end + 4)] : text);
                section = "BODY[HEADER]";
            }
            else if (items.Contains("BODY.PEEK[]", StringComparison.Ordinal) || items.Contains("BODY[]", StringComparison.Ordinal))
            {
                literal = message.Raw;
                section = "BODY[]";
            }

            var head = $"* {message.Seq} FETCH ({string.Join(' ', fields)}";
            if (literal is null)
            {
                await Write(stream, head + ")\r\n");
                continue;
            }

            await Write(stream, $"{head} {section} {{{literal.Length}}}\r\n");
            await stream.WriteAsync(literal);
            await Write(stream, ")\r\n");
        }

        await Write(stream, $"{tag} OK done\r\n");
    }

    /// <summary>Набор номеров вида «1,3:5,7:*».</summary>
    private static Func<uint, bool> Set(string set, uint max)
    {
        var ranges = set.Split(',').Select(part =>
        {
            var ends = part.Split(':');
            uint Parse(string s) => s == "*" ? max : uint.Parse(s, CultureInfo.InvariantCulture);
            var a = Parse(ends[0]);
            var b = ends.Length > 1 ? Parse(ends[1]) : a;
            return (Low: Math.Min(a, b), High: Math.Max(a, b));
        }).ToList();
        return uid => ranges.Any(r => uid >= r.Low && uid <= r.High);
    }

    private static List<string> Tokens(string text) =>
        TokenPattern().Matches(text).Select(m => m.Groups[1].Success ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : m.Value).ToList();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""|[^\s()]+")]
    private static partial Regex TokenPattern();

    /// <summary>Имена папок в IMAP — в «изменённом UTF-7» (RFC 3501): «Спам» — «&BCEEPwQwBDw-».</summary>
    private static string DecodeName(string name) => ModifiedBase64().Replace(name, m => m.Groups[1].Value.Length == 0
        ? "&"
        : Encoding.BigEndianUnicode.GetString(Convert.FromBase64String(Pad(m.Groups[1].Value.Replace(',', '/')))));

    private static string EncodeName(string name)
    {
        var result = new StringBuilder();
        var pending = new StringBuilder();
        void Flush()
        {
            if (pending.Length > 0)
            {
                result.Append('&').Append(Convert.ToBase64String(Encoding.BigEndianUnicode.GetBytes(pending.ToString())).TrimEnd('=').Replace('/', ',')).Append('-');
                pending.Clear();
            }
        }

        foreach (var c in name)
        {
            if (c is >= ' ' and <= '~')
            {
                Flush();
                result.Append(c == '&' ? "&-" : c.ToString());
            }
            else
            {
                pending.Append(c);
            }
        }

        Flush();
        return result.ToString();
    }

    private static string Pad(string base64) => base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

    [GeneratedRegex("&([A-Za-z0-9+,]*)-")]
    private static partial Regex ModifiedBase64();

    private static Task Write(NetworkStream stream, string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    /// <summary>Строки команд (с литералами «{n}» — продолжение после «+»).</summary>
    private sealed class LineReader(NetworkStream stream)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public async Task<string?> ReadCommandAsync(NetworkStream output, CancellationToken cancellationToken)
        {
            var command = new StringBuilder();
            while (true)
            {
                var line = await ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return null;
                }

                if (LiteralAtEnd().Match(line) is { Success: true } literal)
                {
                    var length = int.Parse(literal.Groups[1].Value, CultureInfo.InvariantCulture);
                    command.Append(line[..literal.Index]);
                    if (!literal.Groups[0].Value.Contains('+'))
                    {
                        await output.WriteAsync("+ go ahead\r\n"u8.ToArray(), cancellationToken);
                    }

                    var bytes = await ReadBytesAsync(length, cancellationToken);
                    command.Append('"').Append(Encoding.UTF8.GetString(bytes).Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                    continue;
                }

                return command.Append(line).ToString();
            }
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                for (var i = _start; i + 1 < _end; i++)
                {
                    if (_buffer[i] == '\r' && _buffer[i + 1] == '\n')
                    {
                        var line = Encoding.UTF8.GetString(_buffer, _start, i - _start);
                        _start = i + 2;
                        return line;
                    }
                }

                if (!await FillAsync(cancellationToken))
                {
                    return null;
                }
            }
        }

        private async Task<byte[]> ReadBytesAsync(int length, CancellationToken cancellationToken)
        {
            while (_end - _start < length)
            {
                if (!await FillAsync(cancellationToken))
                {
                    throw new IOException("Соединение закрыто.");
                }
            }

            var bytes = _buffer[_start..(_start + length)];
            _start += length;
            return bytes;
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            if (_start > 0)
            {
                Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
            _end += read;
            return read > 0;
        }
    }

    [GeneratedRegex(@"\{(\d+)\+?\}$")]
    private static partial Regex LiteralAtEnd();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }
}
