using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Pepa.Core;
using Pepa.Core.Security;

namespace Pepa.Adapters.Email;

public sealed record EmailServiceOptions
{
    /// <summary>Как часто проверять папку.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждать ответа сервера на одну команду.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Сколько новых писем забирать за одну проверку (остальные — сразу следующей).</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Письмо больше — в событие попадают только заголовки (тема, отправитель), без текста.</summary>
    public long MaxMessageBytes { get; init; } = 5 * 1024 * 1024;
}

/// <summary>
/// Почтовый ящик (IMAP): новые письма в выбранной папке становятся событиями «email» — их ждёт блок «Ждать письмо» и
/// по ним могут запускаться сценарии. Pepa только читает: папка открывается «только для чтения», письма не отмечаются
/// прочитанными и не удаляются.
/// <para>
/// Без повторов и пропусков: номер последнего переданного письма хранится на диске (после каждого письма); письма,
/// пришедшие, пока Pepa была закрыта, передаются после запуска (процессы по ним сами не запускаются); то, что лежало в
/// ящике до подключения, — не события. Одно и то же письмо (Message-ID) — одно событие.
/// </para>
/// </summary>
public sealed class EmailService : IAsyncDisposable
{
    public const string SecretPurpose = "Pepa.Email";

    private readonly string _folder;
    private readonly Func<EventInput, Task> _publish;
    private readonly EmailServiceOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private StoredAccount? _stored;
    private MailState? _state;
    private ImapClient? _client;
    private IMailFolder? _openFolder;
    private EmailStatus _status;
    private bool _catchingUp;
    private Task? _loop;

    private sealed record StoredAccount(EmailAccount Account, string? Password);

    private sealed record MailState(string Key, uint UidValidity, uint LastUid);

    private sealed record AccountFile(string Server, int Port, EmailSecurity Security, string Login, string Folder, bool Enabled, string? Password);

    public EmailService(string dataDirectory, Func<EventInput, Task> publish, EmailServiceOptions? options = null)
    {
        _folder = Path.Combine(dataDirectory, "email");
        _publish = publish;
        _options = options ?? new EmailServiceOptions();
        _stored = LoadAccount(out var problem);
        _state = LoadState();
        _status = Idle(problem);
    }

    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Состояние сменилось (для «Интеграций»).</summary>
    public event Action? StatusChanged;

    /// <summary>Письма, пришедшие, пока Pepa была закрыта, переданы.</summary>
    public event Action? CaughtUp;

    public EmailAccount? Account => _stored?.Account;

    /// <summary>Почта настроена и включена: письма приходят (или придут, когда сервер ответит).</summary>
    public bool IsReady => _stored is { Account.Enabled: true, Password: not null };

    public EmailStatus Status => Volatile.Read(ref _status);

    /// <summary>Будет ли после запуска досмотр писем, пришедших без Pepa.</summary>
    public bool WillCatchUp => IsReady && _state is { } state && state.Key == _stored!.Account.Key;

    private string AccountPath => Path.Combine(_folder, "account.json");
    private string StatePath => Path.Combine(_folder, "state.json");

    /// <param name="runLoop">false — проверки только через <see cref="CheckAsync"/> (для проверок Pepa).</param>
    public void Start(bool runLoop = true)
    {
        _catchingUp = WillCatchUp;
        if (runLoop)
        {
            _loop = Task.Run(LoopAsync);
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var more = false;
            try
            {
                more = await CheckAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Ошибка видна в «Интеграциях»; следующая проверка — по расписанию, с новым подключением.
                Logger.LogWarning("Почта: {Error}", ex.Message);
            }

            try
            {
                if (!more)
                {
                    await _wake.WaitAsync(_options.Interval, _stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Одна проверка папки: новые письма — события. true — новых писем больше, чем забирается за раз (проверить снова
    /// сразу). Ошибка соединения — исключение <see cref="EmailUnavailableException"/> и состояние «ошибка».
    /// </summary>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_stored is not { Account.Enabled: true, Password: { } password } stored)
            {
                Disconnect();
                SetStatus(Idle(null));
                return false;
            }

            var account = stored.Account;
            try
            {
                if (_client is not { IsConnected: true, IsAuthenticated: true } || _openFolder is not { IsOpen: true })
                {
                    Disconnect();
                    SetStatus(Status with { State = EmailState.Connecting, Account = account });
                    _client = await ConnectAsync(account, password, cancellationToken);
                    _openFolder = await OpenFolderAsync(_client, account, cancellationToken);
                }
                else
                {
                    // Сервер сообщает о новых письмах в ответ на любую команду.
                    await _client.NoOpAsync(cancellationToken);
                }

                return await FetchNewAsync(account, _openFolder, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Disconnect();
                var problem = ex as EmailUnavailableException ?? Explain(ex, account);
                SetStatus(Status with { State = EmailState.Error, Account = account, Error = problem.Message, LastCheckAt = DateTimeOffset.UtcNow });
                throw problem;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> FetchNewAsync(EmailAccount account, IMailFolder folder, CancellationToken cancellationToken)
    {
        var validity = folder.UidValidity;
        string? note = null;
        if (_state is not { } state || state.Key != account.Key || state.UidValidity != validity)
        {
            if (_state is { } old && old.Key == account.Key)
            {
                note = "Почтовый ящик заново пронумеровал письма — письма, пришедшие с прошлой проверки, могли не стать событиями.";
                Logger.LogWarning("Почта: {Note}", note);
            }

            // Что уже лежит в папке — не события: считаются только письма, пришедшие после подключения.
            var last = folder.UidNext is { } next && next.Id > 0
                ? next.Id - 1
                : (await folder.SearchAsync(SearchQuery.All, cancellationToken)).Select(u => u.Id).DefaultIfEmpty(0u).Max();
            _state = new MailState(account.Key, validity, last);
            SaveState(_state);
            Connected(account, folder, note);
            FinishCatchUp();
            return false;
        }

        var range = new UniqueIdRange(new UniqueId(validity, state.LastUid + 1), UniqueId.MaxValue);
        var found = (await folder.SearchAsync(SearchQuery.Uids(range), cancellationToken))
            .Where(u => u.Id > state.LastUid).OrderBy(u => u.Id).ToList();
        var batch = found.Take(_options.BatchSize).ToList();
        if (batch.Count > 0)
        {
            var summaries = await folder.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.InternalDate,
                cancellationToken);
            foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
            {
                var tooLarge = summary.Size > _options.MaxMessageBytes;
                var message = tooLarge
                    ? new MimeMessage(await folder.GetHeadersAsync(summary.UniqueId, cancellationToken))
                    : await folder.GetMessageAsync(summary.UniqueId, cancellationToken);
                // Сначала событие, потом номер: сбой между ними даст повтор, а его отсечёт Message-ID (не пропуск).
                await _publish(EmailEvents.ToEvent(message, summary.UniqueId.Id, validity, summary.InternalDate ?? DateTimeOffset.UtcNow, account, tooLarge));
                _state = _state! with { LastUid = summary.UniqueId.Id };
                SaveState(_state);
            }
        }

        Connected(account, folder, Status.Note);
        var more = found.Count > batch.Count;
        if (!more)
        {
            FinishCatchUp();
        }

        return more;
    }

    private void Connected(EmailAccount account, IMailFolder folder, string? note) =>
        SetStatus(new EmailStatus(EmailState.Connected, account, DateTimeOffset.UtcNow, null, folder.Count, note));

    private void FinishCatchUp()
    {
        if (_catchingUp)
        {
            _catchingUp = false;
            CaughtUp?.Invoke();
        }
    }

    /// <summary>
    /// Проверить ящик, не сохраняя: подключиться, войти и открыть папку. Возвращает число писем в папке. Пароль null —
    /// сохранённый (если сервер и логин те же).
    /// </summary>
    public async Task<int> TestAsync(EmailAccount account, string? password, CancellationToken cancellationToken)
    {
        account = Normalize(account);
        var secret = PasswordFor(account, password);
        using var client = await ConnectAsync(account, secret, cancellationToken);
        try
        {
            var folder = await OpenFolderAsync(client, account, cancellationToken);
            return folder.Count;
        }
        finally
        {
            try
            {
                await client.DisconnectAsync(true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Проверка уже прошла; закрыть соединение вежливо не обязательно.
            }
        }
    }

    /// <summary>
    /// Сохранить ящик. Включённый сначала проверяется (вход и папка) — неверные данные не сохраняются. Другой ящик или
    /// папка начинают с того, что в них уже лежит.
    /// </summary>
    public async Task<EmailStatus> ConfigureAsync(EmailAccount account, string? password, CancellationToken cancellationToken)
    {
        account = Normalize(account);
        var secret = PasswordFor(account, password);
        int? count = null;
        if (account.Enabled)
        {
            count = await TestAsync(account, secret, cancellationToken);
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            Disconnect();
            _stored = new StoredAccount(account, secret);
            SaveAccount(_stored);
            if (_state is { } state && state.Key != account.Key)
            {
                _state = null;
                Delete(StatePath);
            }

            SetStatus(account.Enabled
                ? new EmailStatus(EmailState.Connecting, account, null, null, count, null)
                : Idle(null));
        }
        finally
        {
            _lock.Release();
        }

        Logger.LogInformation("Почта: ящик {Login} на {Server}, папка {Folder}{Off}.", account.Login, account.Server, account.Folder,
            account.Enabled ? "" : " (выключено)");
        Wake();
        return Status;
    }

    /// <summary>Забыть ящик и пароль.</summary>
    public async Task<EmailStatus> ClearAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Disconnect();
            _stored = null;
            _state = null;
            Delete(AccountPath);
            Delete(StatePath);
            SetStatus(Idle(null));
        }
        finally
        {
            _lock.Release();
        }

        return Status;
    }

    private string PasswordFor(EmailAccount account, string? password)
    {
        if (password is { Length: > 0 })
        {
            return password;
        }

        if (_stored is { Password: { } stored } current &&
            string.Equals(current.Account.Server, account.Server, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.Account.Login, account.Login, StringComparison.OrdinalIgnoreCase))
        {
            return stored;
        }

        throw new EmailUnavailableException("Введите пароль ящика.");
    }

    private static EmailAccount Normalize(EmailAccount account)
    {
        var server = account.Server.Trim();
        var login = account.Login.Trim();
        var folder = string.IsNullOrWhiteSpace(account.Folder) ? "INBOX" : account.Folder.Trim();
        if (server.Length == 0 || server.Any(char.IsWhiteSpace) || server.Contains('/'))
        {
            throw new EmailUnavailableException("Укажите сервер IMAP, например imap.yandex.ru.");
        }

        if (account.Port is < 1 or > 65535)
        {
            throw new EmailUnavailableException("Порт — число от 1 до 65535 (обычно 993).");
        }

        if (login.Length == 0)
        {
            throw new EmailUnavailableException("Укажите логин — обычно это адрес почты.");
        }

        return account with { Server = server, Login = login, Folder = folder };
    }

    private async Task<ImapClient> ConnectAsync(EmailAccount account, string password, CancellationToken cancellationToken)
    {
        if (account.Security == EmailSecurity.None && !IsLocal(account.Server))
        {
            throw new EmailUnavailableException(
                "Без шифрования Pepa подключается только к серверу на этом компьютере — иначе пароль ушёл бы по сети открытым. Выберите SSL или STARTTLS.");
        }

        // Не «STARTTLS, если сервер умеет»: сервер без него получил бы пароль открытым.
        var options = account.Security switch
        {
            EmailSecurity.Ssl => SecureSocketOptions.SslOnConnect,
            EmailSecurity.StartTls => SecureSocketOptions.StartTls,
            EmailSecurity.None => SecureSocketOptions.None,
            _ => account.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
        };
        var client = new ImapClient { Timeout = (int)_options.OperationTimeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(account.Server, account.Port, options, cancellationToken);
            await client.AuthenticateAsync(account.Login, password, cancellationToken);
            return client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw Explain(ex, account);
        }
    }

    private static async Task<IMailFolder> OpenFolderAsync(ImapClient client, EmailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            var folder = account.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(account.Folder, cancellationToken);
            // Только чтение: письма не отмечаются прочитанными.
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            return folder;
        }
        catch (FolderNotFoundException)
        {
            throw new EmailUnavailableException($"В ящике нет папки «{account.Folder}». Входящие — INBOX.");
        }
    }

    private static EmailUnavailableException Explain(Exception ex, EmailAccount account) => ex switch
    {
        EmailUnavailableException known => known,
        AuthenticationException => new EmailUnavailableException(
            "Сервер не принял логин или пароль. Для Яндекса, Mail.ru и Gmail нужен пароль приложения (создаётся в настройках " +
            "безопасности почты), а в настройках почты должен быть включён доступ по IMAP.", ex),
        SslHandshakeException => new EmailUnavailableException(
            $"Не удалось установить защищённое соединение с {account.Server}:{account.Port} — проверьте порт и способ защиты.", ex),
        SocketException => new EmailUnavailableException($"Сервер {account.Server}:{account.Port} не отвечает — проверьте адрес, порт и интернет.", ex),
        TimeoutException or OperationCanceledException => new EmailUnavailableException($"Сервер {account.Server} не ответил вовремя.", ex),
        _ => new EmailUnavailableException($"Ошибка почты: {ex.Message}", ex),
    };

    private static bool IsLocal(string server) =>
        server is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    private void Disconnect()
    {
        _openFolder = null;
        if (_client is { } client)
        {
            _client = null;
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Соединение уже разорвано.
            }

            client.Dispose();
        }
    }

    private EmailStatus Idle(string? problem) => _stored switch
    {
        null => new EmailStatus(EmailState.NotConfigured, null, null, problem, null, null),
        { Account.Enabled: false } stored => new EmailStatus(EmailState.Off, stored.Account, null, problem, null, null),
        { Password: null } stored => new EmailStatus(EmailState.Error, stored.Account, null, problem ?? "Введите пароль ящика снова.", null, null),
        var stored => new EmailStatus(EmailState.Connecting, stored.Account, null, problem, null, null),
    };

    private void SetStatus(EmailStatus status)
    {
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous with { LastCheckAt = null } != status with { LastCheckAt = null })
        {
            StatusChanged?.Invoke();
        }
    }

    // ───────────── Хранение ─────────────

    private StoredAccount? LoadAccount(out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(AccountPath))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize<AccountFile>(File.ReadAllText(AccountPath), Json);
            if (file is null)
            {
                return null;
            }

            string? password = null;
            if (file.Password is { Length: > 0 } stored)
            {
                try
                {
                    password = Secrets.Unprotect(stored, SecretPurpose);
                }
                catch (CryptographicException)
                {
                    problem = "Пароль ящика сохранён под другой учётной записью Windows — введите его снова.";
                }
            }

            return new StoredAccount(new EmailAccount
            {
                Server = file.Server, Port = file.Port, Security = file.Security, Login = file.Login, Folder = file.Folder, Enabled = file.Enabled,
            }, password);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = $"Настройки почты не читаются: {ex.Message}";
            return null;
        }
    }

    private void SaveAccount(StoredAccount stored)
    {
        var account = stored.Account;
        WriteAtomic(AccountPath, JsonSerializer.Serialize(new AccountFile(account.Server, account.Port, account.Security, account.Login,
            account.Folder, account.Enabled, stored.Password is { } password ? Secrets.Protect(password, SecretPurpose) : null), Json));
    }

    private MailState? LoadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<MailState>(File.ReadAllText(StatePath), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveState(MailState state) => WriteAtomic(StatePath, JsonSerializer.Serialize(state, Json));

    private void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(_folder);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is { } loop)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _lock.WaitAsync();
        try
        {
            Disconnect();
        }
        finally
        {
            _lock.Release();
        }

        _stop.Dispose();
    }
}
