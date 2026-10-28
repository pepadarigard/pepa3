using System.Net.Sockets;
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

    /// <summary>Письмо, которое сервер столько проверок подряд не отдаёт, пропускается — иначе за ним застряли бы все остальные.</summary>
    public int MaxMessageFailures { get; init; } = 3;

    /// <summary>
    /// Поиск отправленного письма в «Отправленных» (когда ответ сервера отправки потерялся): сколько раз и с какой паузой —
    /// копия появляется там не мгновенно.
    /// </summary>
    public int SentCheckAttempts { get; init; } = 3;

    public TimeSpan SentCheckDelay { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Почтовый ящик (IMAP): новые письма в выбранной папке становятся событиями «email» — их ждёт блок «Ждать письмо» и
/// по ним могут запускаться сценарии. Pepa только читает: папка открывается «только для чтения», письма не отмечаются
/// прочитанными и не удаляются.
/// <para>
/// Без повторов и пропусков: номер последнего переданного письма хранится на диске (после каждого письма); письма,
/// пришедшие, пока Pepa была закрыта, передаются после запуска (процессы по ним сами не запускаются); то, что лежало в
/// ящике до подключения, — не события. Одно письмо ящика (его номер) — одно событие.
/// </para>
/// </summary>
public sealed partial class EmailService : IAsyncDisposable
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
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Task? _loop;

    /// <summary>Почему настройки не прочитались (показывается, пока ящик не настроят заново).</summary>
    private string? _loadProblem;

    /// <summary>Пометка для «Интеграций»: письма могли не стать событиями (и почему).</summary>
    private string? _note;

    /// <summary>Ошибка, которая сама не пройдёт (пароль не принят, папки нет): ждать письма до исправления бессмысленно.</summary>
    private EmailUnavailableException? _lasting;

    private sealed record StoredAccount(EmailAccount Account, string? Password);

    /// <param name="CheckedAt">Последняя удачная проверка: письмо «пришло» не раньше неё.</param>
    /// <param name="FailedUid">Письмо, которое сервер не отдаёт, и сколько проверок подряд.</param>
    private sealed record MailState(string Key, uint UidValidity, uint LastUid, DateTimeOffset? CheckedAt = null, uint? FailedUid = null,
        int Failures = 0);

    private sealed record AccountFile(string? Server, int Port, EmailSecurity Security, string? Login, string? Folder, bool Enabled, string? Password,
        string? SmtpServer = null, int SmtpPort = 465, EmailSecurity SmtpSecurity = EmailSecurity.Auto, string? SentFolder = null);

    public EmailService(string dataDirectory, Func<EventInput, Task> publish, EmailServiceOptions? options = null)
    {
        _folder = Path.Combine(dataDirectory, "email");
        _publish = publish;
        _options = options ?? new EmailServiceOptions();
        _stored = LoadAccount();
        _state = LoadState();
        _status = Idle();
    }

    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Состояние сменилось (для «Интеграций»).</summary>
    public event Action? StatusChanged;

    /// <summary>Письма, пришедшие, пока Pepa была закрыта, переданы (или ждать их больше нечего).</summary>
    public event Action? CaughtUp;

    public EmailAccount? Account => _stored?.Account;

    /// <summary>Почта настроена и включена: письма приходят (или придут, когда сервер ответит).</summary>
    public bool IsReady => _stored is { Account.Enabled: true, Password: not null };

    /// <summary>Почему ждать письмо сейчас бессмысленно; null — можно ждать (сервер мог лишь ненадолго пропасть).</summary>
    public string? Problem => _stored switch
    {
        null => _loadProblem ?? "Почта не подключена: «Интеграции» → «Почта» — укажите ящик и пароль приложения.",
        { Account.Enabled: false } => "Почта выключена: «Интеграции» → «Почта».",
        { Password: null } => _loadProblem ?? "Пароль ящика нужно ввести снова: «Интеграции» → «Почта».",
        _ when Volatile.Read(ref _lasting) is { } lasting => $"Почта не работает: {lasting.Message}",
        _ => null,
    };

    public EmailStatus Status => Volatile.Read(ref _status);

    /// <summary>Будет ли после запуска досмотр писем, пришедших без Pepa.</summary>
    public bool WillCatchUp => IsReady && _state is { } state && state.Key == _stored!.Account.Key;

    private string AccountPath => Path.Combine(_folder, "account.json");
    private string StatePath => Path.Combine(_folder, "state.json");

    /// <param name="engineStartedAt">Когда запущен движок: письма, пришедшие без Pepa, получают время раньше этого.</param>
    /// <param name="runLoop">false — проверки только через <see cref="CheckAsync"/> (для проверок Pepa).</param>
    public void Start(DateTimeOffset engineStartedAt, bool runLoop = true)
    {
        _startedAt = engineStartedAt;
        _catchingUp = WillCatchUp;
        if (_note is { } note)
        {
            Logger.LogWarning("Почта: {Note}", note);
        }

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
                SetStatus(Idle());
                FinishCatchUp();
                return false;
            }

            var account = stored.Account;
            try
            {
                if (_client is { IsConnected: true, IsAuthenticated: true } client && _openFolder is { IsOpen: true })
                {
                    try
                    {
                        // Сервер сообщает о новых письмах в ответ на любую команду.
                        await client.NoOpAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // Сервер закрыл долгое соединение — обычное дело: сразу подключиться заново, а не сообщать об ошибке.
                        Disconnect();
                    }
                }

                if (_client is not { IsConnected: true, IsAuthenticated: true } || _openFolder is not { IsOpen: true })
                {
                    Disconnect();
                    if (Status.State != EmailState.Connected)
                    {
                        SetStatus(Status with { State = EmailState.Connecting, Account = account });
                    }

                    _client = await ConnectAsync(account, password, cancellationToken);
                    _openFolder = await OpenFolderAsync(_client, account, cancellationToken);
                }

                var more = await FetchNewAsync(account, _openFolder, cancellationToken);
                Volatile.Write(ref _lasting, null);
                return more;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Disconnect();
                var problem = ex as EmailUnavailableException ?? Explain(ex, account);
                Volatile.Write(ref _lasting, problem.Lasting ? problem : null);
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
        var now = DateTimeOffset.UtcNow;
        if (_state is not { } state || state.Key != account.Key || state.UidValidity != validity)
        {
            if (_state is { } old && old.Key == account.Key)
            {
                _note = "Почтовый ящик заново пронумеровал письма — письма, пришедшие с прошлой проверки, могли не стать событиями.";
                Logger.LogWarning("Почта: {Note}", _note);
            }

            // Что уже лежит в папке — не события: считаются только письма, пришедшие после подключения.
            var last = folder.UidNext is { } next && next.Id > 0
                ? next.Id - 1
                : (await folder.SearchAsync(SearchQuery.All, cancellationToken)).Select(u => u.Id).DefaultIfEmpty(0u).Max();
            _state = new MailState(account.Key, validity, last, now);
            SaveState(_state);
            Connected(account, folder);
            FinishCatchUp();
            return false;
        }

        var range = new UniqueIdRange(new UniqueId(validity, state.LastUid + 1), UniqueId.MaxValue);
        var found = (await folder.SearchAsync(SearchQuery.Uids(range), cancellationToken))
            .Where(u => u.Id > state.LastUid).OrderBy(u => u.Id).ToList();
        var batch = found.Take(_options.BatchSize).ToList();
        var stuck = false;
        if (batch.Count > 0)
        {
            var summaries = await folder.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.InternalDate,
                cancellationToken);
            foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
            {
                var uid = summary.UniqueId.Id;
                var (message, tooLarge) = await ReadAsync(folder, summary, cancellationToken);
                if (message is null)
                {
                    var failures = _state!.FailedUid == uid ? _state.Failures + 1 : 1;
                    if (failures < _options.MaxMessageFailures)
                    {
                        // Письма — по порядку: следующие подождут, пока это не получится или не будет пропущено.
                        _state = _state with { FailedUid = uid, Failures = failures };
                        SaveState(_state);
                        stuck = true;
                        break;
                    }

                    _note = $"Письмо №{uid} сервер не отдал {failures} раза подряд — оно пропущено и событием не стало.";
                    Logger.LogWarning("Почта: {Note}", _note);
                    _state = _state with { LastUid = uid, FailedUid = null, Failures = 0 };
                    SaveState(_state);
                    continue;
                }

                // Сначала событие, потом номер: сбой между ними даст повтор того же номера, а его отсечёт движок (не пропуск).
                await _publish(EmailEvents.ToEvent(message, uid, validity, Received(summary.InternalDate, state.CheckedAt, now), account, tooLarge));
                _state = _state! with { LastUid = uid, FailedUid = null, Failures = 0 };
                SaveState(_state);
            }
        }

        _state = _state! with { CheckedAt = now };
        SaveState(_state);
        Connected(account, folder);
        var more = !stuck && found.Count > batch.Count;
        if (!more)
        {
            FinishCatchUp();
        }

        return more;
    }

    /// <summary>
    /// Письмо целиком (или только заголовки — большое или недоступное целиком). null — сервер не отдаёт и заголовки.
    /// </summary>
    private async Task<(MimeMessage? Message, bool TooLarge)> ReadAsync(IMailFolder folder, IMessageSummary summary, CancellationToken cancellationToken)
    {
        if (summary.Size is not { } size || size <= _options.MaxMessageBytes)
        {
            try
            {
                return (await folder.GetMessageAsync(summary.UniqueId, cancellationToken), false);
            }
            catch (Exception ex) when (ex is ImapCommandException or ParseException or FormatException)
            {
                Logger.LogWarning("Почта: письмо №{Uid} не получено целиком: {Error}", summary.UniqueId.Id, ex.Message);
            }
        }

        try
        {
            return (new MimeMessage(await folder.GetHeadersAsync(summary.UniqueId, cancellationToken)), true);
        }
        catch (Exception ex) when (ex is ImapCommandException or ParseException or FormatException)
        {
            Logger.LogWarning("Почта: письмо №{Uid} не получено: {Error}", summary.UniqueId.Id, ex.Message);
            return (null, false);
        }
    }

    /// <summary>
    /// Когда письмо «пришло» для Pepa: время сервера, но не раньше прошлой проверки (перенесённое правилом или из другой
    /// папки письмо хранит старую дату) и не позже «сейчас» (часы сервера спешат). Пока досматривается пропущенное без
    /// Pepa, письмо без даты считается пришедшим до запуска — процессы по нему сами не запускаются.
    /// </summary>
    private DateTimeOffset Received(DateTimeOffset? server, DateTimeOffset? previousCheck, DateTimeOffset now)
    {
        var value = server ?? (_catchingUp ? _startedAt.AddMilliseconds(-1) : now);
        if (previousCheck is { } lower && value < lower)
        {
            value = lower;
        }

        return value > now ? now : value;
    }

    private void Connected(EmailAccount account, IMailFolder folder) =>
        SetStatus(new EmailStatus(EmailState.Connected, account, DateTimeOffset.UtcNow, null, folder.Count, _note));

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
    /// папка, как и включение выключенной почты, начинают с того, что в ней уже лежит (письма за время, пока почта была
    /// выключена, — не события). Смена только порта, защиты или пароля — тот же ящик: письма за время без связи придут.
    /// </summary>
    public async Task<EmailStatus> ConfigureAsync(EmailAccount account, string? password, CancellationToken cancellationToken)
    {
        account = Normalize(account);
        var secret = PasswordFor(account, password);
        int? count = null;
        if (account.Enabled)
        {
            count = await TestAsync(account, secret, cancellationToken);
            if (account.SmtpServer is not null)
            {
                await TestSmtpAsync(account, secret, cancellationToken);
            }
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            Disconnect();
            var wasEnabled = _stored is { Account.Enabled: true };
            _stored = new StoredAccount(account, secret);
            SaveAccount(_stored);
            _loadProblem = null;
            Volatile.Write(ref _lasting, null);
            if (_state is { } state && (state.Key != account.Key || (account.Enabled && !wasEnabled)))
            {
                _state = null;
                _note = null;
                Delete(StatePath);
            }

            if (!account.Enabled)
            {
                FinishCatchUp();
            }

            SetStatus(account.Enabled
                ? new EmailStatus(EmailState.Connecting, account, null, null, count, _note)
                : Idle());
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

    /// <summary>Забыть ящик и пароль. Письма, пришедшие, пока почта отключена, событиями не станут.</summary>
    public async Task<EmailStatus> ClearAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Disconnect();
            _stored = null;
            _state = null;
            _loadProblem = null;
            _note = null;
            Volatile.Write(ref _lasting, null);
            Delete(AccountPath);
            Delete(StatePath);
            SetStatus(Idle());
            FinishCatchUp();
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
        var server = (account.Server ?? "").Trim();
        var login = (account.Login ?? "").Trim();
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

        if (!Enum.IsDefined(account.Security))
        {
            throw new EmailUnavailableException("Выберите способ защиты соединения.");
        }

        var smtp = string.IsNullOrWhiteSpace(account.SmtpServer) ? null : account.SmtpServer.Trim();
        if (smtp is not null)
        {
            if (smtp.Any(char.IsWhiteSpace) || smtp.Contains('/'))
            {
                throw new EmailUnavailableException("Сервер отправки (SMTP) — только имя, например smtp.yandex.ru.");
            }

            if (account.SmtpPort is < 1 or > 65535)
            {
                throw new EmailUnavailableException("Порт сервера отправки — число от 1 до 65535 (обычно 465 или 587).");
            }

            if (!Enum.IsDefined(account.SmtpSecurity))
            {
                throw new EmailUnavailableException("Выберите способ защиты соединения с сервером отправки.");
            }
        }

        return account with
        {
            Server = server, Login = login, Folder = folder, SmtpServer = smtp,
            SentFolder = string.IsNullOrWhiteSpace(account.SentFolder) ? null : account.SentFolder.Trim(),
        };
    }

    private async Task<ImapClient> ConnectAsync(EmailAccount account, string password, CancellationToken cancellationToken)
    {
        if (account.Security == EmailSecurity.None && !IsLocal(account.Server))
        {
            throw new EmailUnavailableException(
                "Без шифрования Pepa подключается только к серверу на этом компьютере — иначе пароль ушёл бы по сети открытым. Выберите SSL или STARTTLS.",
                lasting: true);
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
            throw new EmailUnavailableException($"В ящике нет папки «{account.Folder}». Входящие — INBOX.", lasting: true);
        }
    }

    private static EmailUnavailableException Explain(Exception ex, EmailAccount account) => ex switch
    {
        EmailUnavailableException known => known,
        AuthenticationException => new EmailUnavailableException(
            "Сервер не принял логин или пароль. Для Яндекса, Mail.ru и Gmail нужен пароль приложения (создаётся в настройках " +
            "безопасности почты), а в настройках почты должен быть включён доступ по IMAP.", ex, lasting: true),
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

    private EmailStatus Idle() => _stored switch
    {
        null => new EmailStatus(EmailState.NotConfigured, null, null, _loadProblem, null, null),
        { Account.Enabled: false } stored => new EmailStatus(EmailState.Off, stored.Account, null, _loadProblem, null, null),
        { Password: null } stored => new EmailStatus(EmailState.Error, stored.Account, null, _loadProblem ?? "Введите пароль ящика снова.", null, null),
        var stored => new EmailStatus(EmailState.Connecting, stored.Account, null, _loadProblem, null, _note),
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

    /// <summary>Настройки ящика; повреждённый файл — не сбой запуска, а понятная причина в «Интеграциях».</summary>
    private StoredAccount? LoadAccount()
    {
        if (!File.Exists(AccountPath))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<AccountFile>(File.ReadAllText(AccountPath), Json);
            if (file is not { Server.Length: > 0, Login.Length: > 0 } || !Enum.IsDefined(file.Security) || !Enum.IsDefined(file.SmtpSecurity))
            {
                _loadProblem = "Настройки почты повреждены — подключите ящик заново.";
                return null;
            }

            string? password = null;
            if (file.Password is { Length: > 0 } stored)
            {
                try
                {
                    password = Secrets.Unprotect(stored, SecretPurpose);
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
                {
                    _loadProblem = "Пароль ящика сохранён под другой учётной записью Windows или повреждён — введите его снова.";
                }
            }

            return new StoredAccount(new EmailAccount
            {
                Server = file.Server, Port = file.Port, Security = file.Security, Login = file.Login,
                Folder = string.IsNullOrWhiteSpace(file.Folder) ? "INBOX" : file.Folder, Enabled = file.Enabled,
                SmtpServer = string.IsNullOrWhiteSpace(file.SmtpServer) ? null : file.SmtpServer,
                SmtpPort = file.SmtpPort is >= 1 and <= 65535 ? file.SmtpPort : 465, SmtpSecurity = file.SmtpSecurity,
                SentFolder = string.IsNullOrWhiteSpace(file.SentFolder) ? null : file.SentFolder,
            }, password);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _loadProblem = $"Настройки почты не читаются ({ex.Message}) — подключите ящик заново.";
            return null;
        }
    }

    private void SaveAccount(StoredAccount stored)
    {
        var account = stored.Account;
        WriteAtomic(AccountPath, JsonSerializer.Serialize(new AccountFile(account.Server, account.Port, account.Security, account.Login,
            account.Folder, account.Enabled, stored.Password is { } password ? Secrets.Protect(password, SecretPurpose) : null,
            account.SmtpServer, account.SmtpPort, account.SmtpSecurity, account.SentFolder), Json));
    }

    private MailState? LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            if (JsonSerializer.Deserialize<MailState>(File.ReadAllText(StatePath), Json) is { Key.Length: > 0 } state)
            {
                return state;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        _note = "Сохранённое состояние почты не прочиталось — письма, пришедшие, пока Pepa была закрыта, могли не стать событиями.";
        return null;
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
