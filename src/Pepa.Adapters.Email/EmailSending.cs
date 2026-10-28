using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Pepa.Adapters.Email;

/// <summary>
/// Отправка писем (SMTP) с того же ящика. Отказ сервера — письмо точно не ушло. Обрыв связи, когда письмо уже передано
/// серверу целиком, — неизвестно, ушло ли (сервер мог принять его и не успеть ответить): тогда письмо ищется в
/// «Отправленных» по его Message-ID (D-074). Обрыв раньше — не ушло: незаконченное письмо сервер не отправляет.
/// </summary>
public sealed partial class EmailService
{
    private static readonly string[] SentNames =
        ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "Отправленные", "Отправленные элементы", "Исходящие"];

    /// <summary>Почему отправить письмо сейчас нельзя; null — можно.</summary>
    public string? SendProblem => _stored switch
    {
        null or { Account.Enabled: false } or { Password: null } => Problem,
        { Account.SmtpServer: null } => "Отправка писем не настроена: «Интеграции» → «Почта» → «Сервер отправки (SMTP)».",
        _ when Sender(_stored.Account) is null =>
            $"Логин ящика «{_stored.Account.Login}» — не адрес почты, а письма отправляются от адреса ящика. Укажите логин полностью: имя@почта.ru.",
        _ => null,
    };

    /// <summary>От кого письма: адрес ящика (почтовые серверы отправляют только от него).</summary>
    public string? SenderAddress => _stored is { } stored ? Sender(stored.Account) : null;

    private static string? Sender(EmailAccount account) =>
        MailboxAddress.TryParse(account.Login, out var mailbox) && mailbox.Address.Contains('@') ? mailbox.Address : null;

    /// <summary>
    /// Отправить письмо. <see cref="EmailNotSentException"/> — точно не ушло; <see cref="EmailSendUncertainException"/> —
    /// неизвестно (проверить через <see cref="FindSentAsync"/>, не отправлять снова).
    /// </summary>
    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var (account, password) = SendAccount();
        using var client = new SmtpClient { Timeout = (int)_options.OperationTimeout.TotalMilliseconds };
        try
        {
            await ConnectSmtpAsync(client, account, password, cancellationToken);
        }
        catch (EmailUnavailableException ex)
        {
            throw new EmailNotSentException($"{ex.Message} Письмо не отправлено.", retryable: !ex.Lasting, ex);
        }

        var progress = new SendProgress();
        try
        {
            await client.SendAsync(message, cancellationToken, progress);
        }
        catch (SmtpCommandException ex)
        {
            // Сервер ответил отказом (адрес, отправитель, само письмо) — письмо он не принял.
            var temporary = (int)ex.StatusCode is >= 400 and < 500;
            throw new EmailNotSentException(Refusal(ex), retryable: temporary, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!progress.Complete)
            {
                throw new EmailNotSentException($"Связь с сервером отправки прервалась до передачи письма ({ex.Message}). Письмо не отправлено.",
                    retryable: true, ex);
            }

            throw new EmailSendUncertainException(
                $"Связь с сервером отправки прервалась, когда письмо уже передавалось ({ex.Message}) — неизвестно, ушло ли оно.", ex);
        }

        try
        {
            await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Письмо уже принято сервером; закрыть соединение вежливо не обязательно.
        }

        Logger.LogInformation("Почта: отправлено письмо {MessageId}.", message.MessageId);
    }

    /// <summary>
    /// Есть ли письмо с этим Message-ID в «Отправленных». Ищется несколько раз с паузой: копия появляется там не сразу.
    /// <see cref="EmailUnavailableException"/> — проверить не удалось (нет связи, нет папки).
    /// </summary>
    public async Task<SentCheck> FindSentAsync(string messageId, CancellationToken cancellationToken)
    {
        var (account, password) = SendAccount(unavailable: true);
        for (var attempt = 1; ; attempt++)
        {
            using (var client = await ConnectAsync(account, password, cancellationToken))
            {
                try
                {
                    var folder = await SentFolderAsync(client, account, cancellationToken);
                    await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
                    var found = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), cancellationToken);
                    if (found.Count > 0)
                    {
                        return SentCheck.Found;
                    }
                }
                catch (Exception ex) when (ex is not (EmailUnavailableException or OperationCanceledException))
                {
                    throw Explain(ex, account);
                }
                finally
                {
                    try
                    {
                        await client.DisconnectAsync(true, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                    }
                }
            }

            if (attempt >= _options.SentCheckAttempts)
            {
                return SentCheck.NotFound;
            }

            await Task.Delay(_options.SentCheckDelay, cancellationToken);
        }
    }

    /// <summary>Проверка настроек отправки: подключиться и войти (письмо не отправляется).</summary>
    public async Task TestSmtpAsync(EmailAccount account, string? password, CancellationToken cancellationToken)
    {
        account = Normalize(account);
        var secret = PasswordFor(account, password);
        if (account.SmtpServer is null)
        {
            return;
        }

        if (Sender(account) is null)
        {
            throw new EmailUnavailableException(
                $"Логин «{account.Login}» — не адрес почты, а письма отправляются от адреса ящика. Укажите логин полностью: имя@почта.ru.");
        }

        using var client = new SmtpClient { Timeout = (int)_options.OperationTimeout.TotalMilliseconds };
        await ConnectSmtpAsync(client, account, secret, cancellationToken);
        try
        {
            await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private (EmailAccount Account, string Password) SendAccount(bool unavailable = false)
    {
        if (SendProblem is { } problem)
        {
            throw unavailable ? new EmailUnavailableException(problem, lasting: true) : new EmailNotSentException(problem, retryable: false);
        }

        return (_stored!.Account, _stored.Password!);
    }

    private static async Task ConnectSmtpAsync(SmtpClient client, EmailAccount account, string password, CancellationToken cancellationToken)
    {
        var server = account.SmtpServer!;
        if (account.SmtpSecurity == EmailSecurity.None && !IsLocal(server))
        {
            throw new EmailUnavailableException(
                "Без шифрования Pepa подключается к серверу отправки только на этом компьютере — иначе пароль ушёл бы по сети открытым. Выберите SSL или STARTTLS.",
                lasting: true);
        }

        // Не «STARTTLS, если сервер умеет»: сервер без него получил бы пароль открытым.
        var options = account.SmtpSecurity switch
        {
            EmailSecurity.Ssl => SecureSocketOptions.SslOnConnect,
            EmailSecurity.StartTls => SecureSocketOptions.StartTls,
            EmailSecurity.None => SecureSocketOptions.None,
            _ => account.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
        };
        try
        {
            await client.ConnectAsync(server, account.SmtpPort, options, cancellationToken);
            if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            {
                await client.AuthenticateAsync(account.Login, password, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw ExplainSmtp(ex, account);
        }
    }

    private static EmailUnavailableException ExplainSmtp(Exception ex, EmailAccount account) => ex switch
    {
        EmailUnavailableException known => known,
        AuthenticationException => new EmailUnavailableException(
            "Сервер отправки не принял логин или пароль. Для Яндекса, Mail.ru и Gmail нужен пароль приложения, а в настройках " +
            "почты — разрешение почтовым программам (для Яндекса — «Портал паролей», для Mail.ru — «Пароли для внешних приложений»).",
            ex, lasting: true),
        SslHandshakeException => new EmailUnavailableException(
            $"Не удалось установить защищённое соединение с {account.SmtpServer}:{account.SmtpPort} — проверьте порт и способ защиты.", ex),
        SocketException => new EmailUnavailableException(
            $"Сервер отправки {account.SmtpServer}:{account.SmtpPort} не отвечает — проверьте адрес, порт и интернет.", ex),
        TimeoutException or OperationCanceledException => new EmailUnavailableException($"Сервер отправки {account.SmtpServer} не ответил вовремя.", ex),
        SmtpCommandException command => new EmailUnavailableException($"Сервер отправки отказал: {command.Message}", ex),
        _ => new EmailUnavailableException($"Ошибка отправки почты: {ex.Message}", ex),
    };

    private static string Refusal(SmtpCommandException ex) => ex.ErrorCode switch
    {
        SmtpErrorCode.RecipientNotAccepted =>
            $"Сервер отправки не принял адрес получателя «{ex.Mailbox?.Address}»: {ex.Message.Trim()} Письмо не отправлено.",
        SmtpErrorCode.SenderNotAccepted =>
            $"Сервер отправки не разрешил отправлять от «{ex.Mailbox?.Address}»: {ex.Message.Trim()} Письмо не отправлено.",
        SmtpErrorCode.MessageNotAccepted => $"Сервер отправки не принял письмо: {ex.Message.Trim()} Письмо не отправлено.",
        _ => $"Сервер отправки отказал: {ex.Message.Trim()} Письмо не отправлено.",
    };

    /// <summary>
    /// Папка отправленных: указанная в настройках, иначе отмеченная сервером как «Отправленные», иначе — по обычному
    /// названию.
    /// </summary>
    private static async Task<IMailFolder> SentFolderAsync(ImapClient client, EmailAccount account, CancellationToken cancellationToken)
    {
        if (account.SentFolder is { } named)
        {
            try
            {
                return await client.GetFolderAsync(named, cancellationToken);
            }
            catch (FolderNotFoundException)
            {
                throw new EmailUnavailableException($"В ящике нет папки «{named}» (папка отправленных в настройках почты).", lasting: true);
            }
        }

        if (client.Capabilities.HasFlag(ImapCapabilities.SpecialUse) || client.Capabilities.HasFlag(ImapCapabilities.XList))
        {
            try
            {
                if (client.GetFolder(SpecialFolder.Sent) is { } special)
                {
                    return special;
                }
            }
            catch (NotSupportedException)
            {
            }
        }

        var folders = new List<IMailFolder>();
        foreach (var space in client.PersonalNamespaces)
        {
            folders.AddRange(await client.GetFoldersAsync(space, subscribedOnly: false, cancellationToken: cancellationToken));
        }

        return folders.FirstOrDefault(f => f.Attributes.HasFlag(FolderAttributes.Sent))
               ?? folders.FirstOrDefault(f => SentNames.Any(n => n.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
               ?? throw new EmailUnavailableException(
                   "В ящике не нашлась папка отправленных — укажите её в настройках почты («Папка отправленных»).", lasting: true);
    }

    /// <summary>Сколько письма передано серверу: пока не всё, сервер его не отправит (незаконченное письмо отбрасывается).</summary>
    private sealed class SendProgress : ITransferProgress
    {
        private long _written;
        private long? _total;

        /// <summary>Письмо передано целиком (размер неизвестен — передана хоть часть: осторожнее считать, что целиком).</summary>
        public bool Complete => _written > 0 && (_total is not { } total || _written >= total);

        public void Report(long bytesTransferred, long totalSize)
        {
            _written = bytesTransferred;
            _total = totalSize;
        }

        public void Report(long bytesTransferred) => _written = bytesTransferred;
    }
}
