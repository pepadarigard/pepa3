using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pepa.Adapters.Email.Tests;

/// <summary>Как поведёт себя сервер отправки на очередном письме.</summary>
public enum SmtpBehavior
{
    /// <summary>Принять и ответить «250».</summary>
    Accept,

    /// <summary>Отклонить письмо после передачи («554»): письмо не принято.</summary>
    RejectMessage,

    /// <summary>Временная ошибка на MAIL FROM («451»): письмо не принято, повтор возможен.</summary>
    TemporaryFailure,

    /// <summary>Оборвать связь после RCPT TO — до передачи письма.</summary>
    DropBeforeData,

    /// <summary>Принять письмо целиком и оборвать связь, не ответив: письмо ушло, но клиент этого не знает.</summary>
    DropAfterData,
}

/// <summary>
/// Сервер отправки (SMTP) для проверок: без шифрования, на 127.0.0.1, вход AUTH PLAIN. Запоминает принятые письма;
/// <see cref="Accepted"/> — например, положить копию в «Отправленные», как делает Gmail.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly ConcurrentQueue<SmtpBehavior> _plan = new();

    public FakeSmtpServer(string login = "anna@example.com", string password = "app-pass-1")
    {
        Login = login;
        Password = password;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public string Login { get; }
    public string Password { get; set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Адреса, которые сервер не принимает как получателей («550»).</summary>
    public ConcurrentDictionary<string, bool> RejectedRecipients { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Принятые письма (как пришли, с заголовками) и их получатели.</summary>
    public ConcurrentQueue<(string Raw, IReadOnlyList<string> Recipients)> Messages { get; } = new();

    /// <summary>Вызывается с каждым принятым письмом (в том числе при обрыве после передачи).</summary>
    public Action<string>? Accepted { get; set; }

    public int Logins;

    /// <summary>Поведение для очередных писем; после — <see cref="SmtpBehavior.Accept"/>.</summary>
    public void Plan(params SmtpBehavior[] behaviors)
    {
        foreach (var behavior in behaviors)
        {
            _plan.Enqueue(behavior);
        }
    }

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
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        async Task Write(string line) => await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), _stop.Token);
        try
        {
            await Write("220 fake.smtp ESMTP ready");
            var recipients = new List<string>();
            var behavior = SmtpBehavior.Accept;
            var authenticated = false;
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var command = line.Split(' ', 2)[0].ToUpperInvariant();
                var argument = line.Length > command.Length ? line[(command.Length + 1)..] : "";
                switch (command)
                {
                    case "EHLO":
                        await Write("250-fake.smtp\r\n250-AUTH PLAIN\r\n250-8BITMIME\r\n250 SIZE 52428800");
                        break;
                    case "HELO":
                        await Write("250 fake.smtp");
                        break;
                    case "AUTH":
                        var parts = argument.Split(' ', 2);
                        var initial = parts.Length > 1 ? parts[1] : null;
                        if (initial is null)
                        {
                            await Write("334 ");
                            initial = await reader.ReadLineAsync(_stop.Token);
                        }

                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(initial ?? "")).Split('\0');
                        if (decoded.Length == 3 && decoded[1] == Login && decoded[2] == Password)
                        {
                            authenticated = true;
                            Interlocked.Increment(ref Logins);
                            await Write("235 2.7.0 Authentication successful");
                        }
                        else
                        {
                            await Write("535 5.7.8 Authentication credentials invalid");
                        }

                        break;
                    case "MAIL":
                        recipients.Clear();
                        behavior = _plan.TryDequeue(out var planned) ? planned : SmtpBehavior.Accept;
                        if (!authenticated)
                        {
                            await Write("530 5.7.0 Authentication required");
                        }
                        else if (behavior == SmtpBehavior.TemporaryFailure)
                        {
                            await Write("451 4.3.0 Try again later");
                        }
                        else
                        {
                            await Write("250 2.1.0 Sender OK");
                        }

                        break;
                    case "RCPT":
                        var address = argument[(argument.IndexOf('<') + 1)..argument.IndexOf('>')];
                        if (RejectedRecipients.ContainsKey(address))
                        {
                            await Write($"550 5.1.1 <{address}>: Recipient address rejected");
                            break;
                        }

                        recipients.Add(address);
                        await Write("250 2.1.5 Recipient OK");
                        if (behavior == SmtpBehavior.DropBeforeData)
                        {
                            return;
                        }

                        break;
                    case "DATA":
                        await Write("354 End data with <CR><LF>.<CR><LF>");
                        var raw = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } data && data != ".")
                        {
                            raw.Append(data.StartsWith("..", StringComparison.Ordinal) ? data[1..] : data).Append("\r\n");
                        }

                        if (behavior == SmtpBehavior.RejectMessage)
                        {
                            await Write("554 5.7.1 Message rejected as spam");
                            break;
                        }

                        Messages.Enqueue((raw.ToString(), [.. recipients]));
                        Accepted?.Invoke(raw.ToString());
                        if (behavior == SmtpBehavior.DropAfterData)
                        {
                            return;
                        }

                        await Write("250 2.0.0 Ok: queued");
                        break;
                    case "RSET" or "NOOP":
                        await Write("250 OK");
                        break;
                    case "QUIT":
                        await Write("221 Bye");
                        return;
                    default:
                        await Write("502 5.5.2 Command not recognized");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or FormatException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
