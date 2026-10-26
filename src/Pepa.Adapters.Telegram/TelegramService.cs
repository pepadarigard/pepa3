using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pepa.Core;
using Pepa.Core.Security;
using TL;
using MBoxState = WTelegram.UpdateManager.MBoxState;

namespace Pepa.Adapters.Telegram;

/// <summary>Данные для входа: api_id и api_hash выдаёт my.telegram.org, номер — телефон аккаунта.</summary>
public sealed record TelegramCredentials(int ApiId, string ApiHash, string Phone)
{
    /// <summary>Вход завершён и сессия действительна — при запуске Pepa подключаться автоматически.</summary>
    public bool Authorized { get; init; }

    /// <summary>Ключ шифрования файла сессии (случайный, свой для каждого входа). null — ключом служит api_hash.</summary>
    public string? SessionKey { get; init; }
}

/// <summary>
/// Пользовательский аккаунт Telegram (MTProto, библиотека WTelegramClient). Получает входящие сообщения и передаёт их
/// как события Pepa; отправляет сообщения для блоков. Сессия и состояние обновлений хранятся в папке данных:
/// после перезапуска сообщения, пришедшие, пока Pepa была закрыта, догружаются (и попадают только к ожидающим
/// процессам — запуск по ним сам не выполняется).
/// </summary>
/// <remarks>
/// Состояние обновлений сохраняется только когда все полученные сообщения приняты движком: библиотека продвигает своё
/// состояние до вызова обработчика, поэтому после неудачи сохранять его нельзя — иначе сообщение пропало бы навсегда.
/// Сохранение идёт отдельно от обработчика: обработчик вызывается под внутренней блокировкой библиотеки, которую
/// ждёт и чтение состояния.
/// </remarks>
public sealed class TelegramService : ITelegramGateway, IAsyncDisposable
{
    private const string NoConnectionHint =
        "Telegram не ответил за отведённое время. Проверьте интернет; если Telegram недоступен в вашей сети, нужен VPN или прокси.";

    private const string NotConnectedHint = "Telegram не подключён. Подключите аккаунт: «Интеграции» → Telegram.";
    private const string SessionExpiredHint = "Сессия Telegram больше не действует (вход отменён на другом устройстве или истёк) — войдите снова.";

    private static readonly TimeSpan NameCacheLifetime = TimeSpan.FromHours(6);

    private readonly string _folder;
    private readonly Func<EventInput, Task> _publish;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _reconnectSignal = new(0);
    private readonly ConcurrentDictionary<string, InputPeer> _inputPeers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TelegramPeer> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (TelegramPeer Peer, DateTime At)> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _reconnectLoop;
    private Connection? _connection;
    private TelegramCredentials? _credentials;
    private TelegramStatus _status;
    private string? _stateWarning;
    private string? _clockWarning;
    private volatile bool _caughtUpReported;
    private int _reconnectFailures;
    private volatile bool _disposed;
    private int _shuttingDown;
    private int _disposeStarted;

    public TelegramService(string dataDirectory, Func<EventInput, Task> publish)
    {
        _folder = Path.Combine(dataDirectory, "telegram");
        _publish = publish;
        // Журнал библиотеки — только предупреждения и ошибки (по умолчанию она пишет всё в консоль).
        WTelegram.Helpers.Log = (level, message) =>
        {
            // При закрытии библиотека сообщает об оборванном соединении — это не ошибка.
            if (level >= 4 && !_disposed)
            {
                Logger.LogWarning("Telegram: {Message}", message);
            }
        };
        _credentials = LoadCredentials();
        _status = _credentials is null
            ? new TelegramStatus(TelegramState.NotConfigured, null, null)
            : new TelegramStatus(HasSession ? TelegramState.Disconnected : TelegramState.LoginRequired, null, null);
        _reconnectLoop = Task.Run(ReconnectLoopAsync);
    }

    public TelegramStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// Первая догрузка после запуска закончена (сообщения, пришедшие, пока Pepa была закрыта, получены). Вызывается один раз.
    /// </summary>
    public event Action? CaughtUp;

    /// <summary>Есть сохранённый вход: после запуска Pepa подключится и догрузит пропущенные сообщения.</summary>
    public bool WillCatchUp => HasSession;

    /// <summary>Журнал (задаётся после сборки хоста: сервис создаётся раньше системы журналов).</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Сколько ждать ответа Telegram при подключении и входе. Без ответа — понятная ошибка, а не вечное ожидание.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Адрес сервера Telegram «ip:порт»; null — основной сервер Telegram (проверки подставляют недоступный адрес).</summary>
    public string? ServerAddress { get; init; }

    /// <summary>Сколько раз пробовать передать движку входящее сообщение, прежде чем отложить его до перезапуска.</summary>
    public int PublishAttempts { get; init; } = 6;

    /// <summary>Сохранённые api_id и номер (api_hash наружу не отдаётся) — чтобы при повторном входе не вводить их снова.</summary>
    public (int ApiId, string Phone)? SavedLogin => _credentials is { } c ? (c.ApiId, c.Phone) : null;

    /// <summary>Изменилось состояние подключения (для интерфейса).</summary>
    public event Action? StatusChanged;

    private string SessionPath => Path.Combine(_folder, "session.dat");
    private bool HasSession => _credentials is { Authorized: true } && File.Exists(SessionPath);
    private string StatePath => Path.Combine(_folder, "updates.json");
    private string CredentialsPath => Path.Combine(_folder, "account.json");

    // ───────────── Подключение ─────────────

    /// <summary>При запуске Pepa: подключиться к уже авторизованному аккаунту. Новый вход сам не начинается.</summary>
    public async Task StartAsync()
    {
        if (!HasSession || _disposed)
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            await ResumeAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <returns>Подключено.</returns>
    private async Task<bool> ResumeAsync()
    {
        SetStatus(TelegramState.Connecting, null, "Подключение…");
        await DisposeConnectionAsync();
        try
        {
            var connection = _connection = await CreateConnectionAsync(_credentials!, interactive: false);
            var user = await Limited(connection.Client.LoginUserIfNeeded(null, reloginOnFailedResume: false));
            AfterLogin(connection, user);
            return true;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Pepa закрывается.
            return false;
        }
        catch (LoginRequiredException)
        {
            await SessionExpiredAsync();
            return false;
        }
        catch (RpcException ex) when (IsSessionRevoked(ex))
        {
            await SessionExpiredAsync();
            return false;
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "Не удалось подключиться к Telegram.");
            await DisposeConnectionAsync();
            var reason = ex is TimeoutException ? NoConnectionHint : $"Нет связи с Telegram: {ex.Message}.";
            SetStatus(TelegramState.Disconnected, null, $"{reason} Повторю попытку.");
            RequestReconnect();
            return false;
        }
    }

    private async Task SessionExpiredAsync()
    {
        if (_credentials is { } credentials)
        {
            _credentials = credentials with { Authorized = false };
            TrySaveCredentials(_credentials);
        }

        // Исключение не нужно: это не ответ на запрос входа, а состояние для интерфейса.
        _ = await FailLoginAsync(SessionExpiredHint);
    }

    /// <summary>Telegram ответил «сессия недействительна» во время работы — перейти к повторному входу (в фоне).</summary>
    private void OnSessionRevoked(Connection connection)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _lock.WaitAsync(_shutdown.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_connection == connection)
                {
                    await SessionExpiredAsync();
                }
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    private static bool IsSessionRevoked(RpcException ex) =>
        ex.Code == 401 || ex.Message.Contains("AUTH_KEY", StringComparison.Ordinal) || ex.Message.Contains("SESSION_REVOKED", StringComparison.Ordinal) ||
        ex.Message.Contains("SESSION_EXPIRED", StringComparison.Ordinal);

    /// <summary>Ожидание ответа Telegram: не дольше <see cref="ConnectTimeout"/> и прерывается при закрытии Pepa.</summary>
    private Task<T> Limited<T>(Task<T> task) => task.WaitAsync(ConnectTimeout, _shutdown.Token);

    /// <summary>Начать вход. Возвращает, что нужно дальше: код из Telegram, пароль или ничего (вход выполнен).</summary>
    /// <remarks>Пустой api_hash при том же api_id — взять сохранённый (повторный вход после истечения сессии).</remarks>
    public async Task<TelegramStatus> BeginLoginAsync(TelegramCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiHash) && _credentials is { } saved && saved.ApiId == credentials.ApiId)
        {
            credentials = credentials with { ApiHash = saved.ApiHash };
        }

        if (credentials.ApiId <= 0 || string.IsNullOrWhiteSpace(credentials.ApiHash) || NormalizePhone(credentials.Phone).Length < 6)
        {
            throw new TelegramLoginException("Укажите api_id, api_hash (их выдаёт my.telegram.org) и номер телефона аккаунта.");
        }

        if (!IsApiHash(credentials.ApiHash.Trim()))
        {
            throw new TelegramLoginException("api_hash — это 32 символа (цифры и латинские буквы a–f) со страницы my.telegram.org → API development tools.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync();
        try
        {
            await DisposeConnectionAsync();
            Directory.CreateDirectory(_folder);
            try
            {
                DeleteFiles(throwOnError: true, SessionPath, StatePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TelegramLoginException($"Не удалось удалить прежнюю сессию Telegram ({ex.Message}). Перезапустите Pepa и попробуйте снова.");
            }

            _stateWarning = null;
            _credentials = credentials with
            {
                Phone = NormalizePhone(credentials.Phone),
                ApiHash = credentials.ApiHash.Trim(),
                Authorized = false,
                SessionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            };
            SaveCredentials(_credentials);
            SetStatus(TelegramState.Connecting, null, "Подключение к Telegram…");
            try
            {
                _connection = await CreateConnectionAsync(_credentials, interactive: true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Не удалось начать вход в Telegram.");
                throw await FailLoginAsync($"Не удалось начать вход: {ex.Message}");
            }

            return await LoginStepAsync(_connection, _credentials.Phone);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Передать код подтверждения или пароль двухэтапной проверки.</summary>
    public async Task<TelegramStatus> ContinueLoginAsync(string value)
    {
        await _lock.WaitAsync();
        try
        {
            if (_connection is not { Interactive: true } connection || Status.State is not (TelegramState.WaitingCode or TelegramState.WaitingPassword))
            {
                throw new TelegramLoginException("Вход не начат. Нажмите «Подключить» ещё раз.");
            }

            // Пароль может начинаться или заканчиваться пробелом — его не обрезаем.
            var input = Status.State == TelegramState.WaitingCode ? value.Trim() : value;
            if (input.Length == 0)
            {
                throw new TelegramLoginException(Status.State == TelegramState.WaitingCode ? "Введите код из Telegram." : "Введите пароль.");
            }

            return await LoginStepAsync(connection, input);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<TelegramStatus> LoginStepAsync(Connection connection, string value)
    {
        var state = Status.State;
        string? next;
        connection.AwaitsInput = false;
        try
        {
            next = await Limited(connection.Client.Login(value));
        }
        catch (RpcException ex)
        {
            // Неверный код или пароль библиотека переспрашивает сама; исключение — когда попытки кончились.
            throw await FailLoginAsync(ex.Message switch
            {
                var m when m.Contains("PHONE_CODE_INVALID", StringComparison.Ordinal) => "Код несколько раз введён неверно. Начните вход заново.",
                var m when m.Contains("PASSWORD_HASH_INVALID", StringComparison.Ordinal) => "Пароль несколько раз введён неверно. Начните вход заново.",
                var m when m.Contains("PHONE_CODE_EXPIRED", StringComparison.Ordinal) => "Код устарел. Начните вход заново.",
                var m when m.Contains("PHONE_NUMBER_INVALID", StringComparison.Ordinal) => "Неверный номер телефона.",
                var m when m.Contains("PHONE_NUMBER_BANNED", StringComparison.Ordinal) => "Этот номер заблокирован в Telegram.",
                var m when m.Contains("API_ID_INVALID", StringComparison.Ordinal) => "Неверные api_id или api_hash.",
                var m when m.Contains("FLOOD", StringComparison.Ordinal) => $"Telegram временно ограничил попытки входа ({ex.Message}). Подождите и попробуйте позже.",
                _ => $"Telegram отказал во входе: {ex.Message}",
            });
        }
        catch (TimeoutException)
        {
            throw await FailLoginAsync(NoConnectionHint);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            throw new TelegramLoginException("Pepa закрывается — вход прерван.");
        }
        catch (Exception ex) when (ex is not (TelegramLoginException or OperationCanceledException))
        {
            Logger.LogWarning(ex, "Вход в Telegram не удался.");
            throw await FailLoginAsync($"Не удалось связаться с Telegram: {ex.Message}");
        }

        if (next is not null)
        {
            // Библиотека держит поток в ожидании ответа: если вход бросят, его нужно отпустить (см. DisposeConnectionAsync).
            connection.AwaitsInput = true;
        }

        switch (next)
        {
            case null:
                break;
            case "verification_code":
                return SetStatus(TelegramState.WaitingCode, null, state == TelegramState.WaitingCode
                    ? "Код не подошёл. Проверьте его и введите ещё раз."
                    : "Telegram отправил код в приложение Telegram (или по SMS). Введите его.");
            case "password":
                return SetStatus(TelegramState.WaitingPassword, null, state == TelegramState.WaitingPassword
                    ? "Пароль не подошёл. Введите пароль двухэтапной проверки ещё раз."
                    : "На аккаунте включена двухэтапная проверка. Введите пароль.");
            case "name":
                throw await FailLoginAsync("На этот номер нет аккаунта Telegram. Зарегистрируйтесь в приложении Telegram.");
            default:
                throw await FailLoginAsync($"Telegram запросил «{next}» — такой способ входа пока не поддерживается.");
        }

        // Вход выполнен: сессия действительна, при следующих запусках Pepa подключится сама.
        _credentials = _credentials! with { Authorized = true };
        SaveCredentials(_credentials);
        AfterLogin(connection, connection.Client.User);
        return Status;
    }

    /// <summary>Вход не удался: закрыть подключение и вернуться к форме входа с причиной.</summary>
    private async Task<TelegramLoginException> FailLoginAsync(string message)
    {
        await DisposeConnectionAsync();
        // Неавторизованная сессия не нужна: без неё при следующем запуске Pepa не будет пытаться подключиться.
        DeleteFiles(throwOnError: false, SessionPath, StatePath);
        SetStatus(TelegramState.LoginRequired, null, message);
        return new TelegramLoginException(message);
    }

    private void AfterLogin(Connection connection, User? user)
    {
        connection.LoggedIn = true;
        _reconnectFailures = 0;
        var name = user is null ? "аккаунт" : $"{user}{(user.MainUsername is { Length: > 0 } un ? $" (@{un})" : "")}";
        SetStatus(TelegramState.Connected, name, Warning);
        Logger.LogInformation("Telegram подключён: {Account}.", name);

        // Чаты из списка диалогов — чтобы находить их по ключу user:/chat:/channel: без поиска по имени. Загрузка в фоне:
        // у большого аккаунта она долгая (и Telegram может попросить подождать), а подключение от неё не зависит.
        connection.Dialogs = Task.Run(async () =>
        {
            try
            {
                var dialogs = await connection.Client.Messages_GetAllDialogs().WaitAsync(connection.Life.Token);
                foreach (var (_, u) in dialogs.users)
                {
                    Remember(u);
                }

                foreach (var (_, chat) in dialogs.chats)
                {
                    Remember(chat);
                }

                // Каналы и супергруппы, из которых ещё не было обновлений, получают позицию: после перезапуска библиотека
                // догрузит и их сообщения, пришедшие, пока Pepa была закрыта.
                await connection.Manager.LoadDialogs(dialogs).WaitAsync(connection.Life.Token);
            }
            catch (Exception) when (connection.Life.IsCancellationRequested)
            {
                // Подключение закрывается.
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Список чатов Telegram не загрузился: чаты по ключу будут находиться только после сообщений из них.");
            }
        });
    }

    public async Task LogoutAsync()
    {
        await _lock.WaitAsync();
        try
        {
            string? note = null;
            if (_connection is { LoggedIn: true } connection && Status.State == TelegramState.Connected)
            {
                try
                {
                    await Limited(connection.Client.Auth_LogOut());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogWarning(ex, "Выход из Telegram на сервере не подтвердился; локальная сессия удалена.");
                    note = "Выход на сервере Telegram не подтвердился — завершите сеанс Pepa в Telegram: Настройки → Устройства.";
                }
            }
            else if (_credentials is { Authorized: true })
            {
                note = "Связи с Telegram нет, поэтому сеанс на сервере не завершён — завершите его в Telegram: Настройки → Устройства.";
            }

            await DisposeConnectionAsync();
            if (!DeleteFiles(throwOnError: false, SessionPath, StatePath, CredentialsPath))
            {
                note = (note is null ? "" : note + " ") + "Часть файлов Telegram в папке данных удалить не удалось — удалите папку telegram вручную.";
            }

            _credentials = null;
            _stateWarning = null;
            _inputPeers.Clear();
            _peers.Clear();
            _byName.Clear();
            SetStatus(TelegramState.NotConfigured, null, note);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Для проверок: часы «сервера Telegram» вместо запроса к нему.</summary>
    internal Func<Task<DateTime>>? ServerClockForTests { get; init; }

    /// <summary>
    /// Для проверок: подключение «как после входа» без сети — чтобы подавать обновления в настоящий менеджер обновлений
    /// библиотеки и проверять приём сообщений и сохранение состояния.
    /// </summary>
    internal async Task<WTelegram.UpdateManager> ConnectForTestsAsync(TelegramCredentials credentials)
    {
        await _lock.WaitAsync();
        try
        {
            _credentials = credentials with { Authorized = true };
            SaveCredentials(_credentials);
            var connection = _connection = await CreateConnectionAsync(_credentials, interactive: false);
            connection.LoggedIn = true;
            SetStatus(TelegramState.Connected, "тест", Warning);
            return connection.Manager;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ───────────── Подключение к библиотеке ─────────────

    /// <param name="interactive">
    /// Вход через <c>Client.Login</c>: номер, код и пароль библиотека запрашивает у <see cref="LoginStepAsync"/>, поэтому на эти
    /// вопросы здесь ответа нет (null). При восстановлении сессии такой вопрос означает, что сессия недействительна.
    /// </param>
    private async Task<Connection> CreateConnectionAsync(TelegramCredentials credentials, bool interactive)
    {
        Directory.CreateDirectory(_folder);
        var connection = new Connection(interactive);
        WTelegram.Client? client = null;
        try
        {
            client = new WTelegram.Client(what => what switch
            {
                "api_id" => credentials.ApiId.ToString(CultureInfo.InvariantCulture),
                "api_hash" => credentials.ApiHash,
                "session_key" => credentials.SessionKey,
                "session_pathname" => SessionPath,
                "device_model" => "Pepa",
                "app_version" => "Pepa",
                // Сохранённая сессия принадлежит этому аккаунту (номер не сверяем: в настройках он может быть записан иначе).
                "user_id" => "-1",
                "server_address" => ServerAddress,
                // Вход через Firebase доступен только официальным приложениям — пусть Telegram пришлёт код другим способом.
                "firebase" => "",
                // Никогда не спрашивать данные входа в консоли: вход идёт только через интерфейс Pepa. Брошенный вход
                // отвечает отказом — так библиотека завершает его, а не ждёт ответа вечно.
                "phone_number" or "verification_code" or "password" or "first_name" or "last_name" or "email"
                    or "email_verification_code" or "bot_token" => connection.Interactive && !connection.Abandoned ? null : throw new LoginRequiredException(),
                _ => null,
            });
            client.MaxAutoReconnects = 50;
            // Короткие «подождите N секунд» библиотека выжидает сама; длинные — понятная ошибка, а не зависание.
            client.FloodRetryThreshold = 10;
            connection.Client = client;
            connection.Manager = client.WithUpdateManager(update => OnUpdateAsync(connection, update), LoadState());
            connection.Manager.Log = (level, message) => OnManagerLog(connection, level, message);
            client.OnOther += item => OnOtherAsync(connection, item);
            connection.Saver = Task.Run(() => SaveLoopAsync(connection));
            return connection;
        }
        catch
        {
            connection.Life.Cancel();
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            throw;
        }
    }

    private Task OnOtherAsync(Connection connection, IObject item)
    {
        if (item is not ReactorError error || _disposed || connection.Life.IsCancellationRequested || _connection != connection)
        {
            return Task.CompletedTask;
        }

        Logger.LogWarning(error.Exception, "Связь с Telegram потеряна.");
        if (!connection.LoggedIn)
        {
            // Вход ещё не завершён: восстанавливать нечего — честно сообщить и начать вход заново.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _lock.WaitAsync(_shutdown.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    if (_connection == connection)
                    {
                        _ = await FailLoginAsync("Связь с Telegram прервалась во время входа. Начните вход заново.");
                    }
                }
                finally
                {
                    _lock.Release();
                }
            });
            return Task.CompletedTask;
        }

        SetStatus(TelegramState.Disconnected, Status.Account, "Связь с Telegram потеряна. Повторю подключение.");
        RequestReconnect();
        return Task.CompletedTask;
    }

    private void RequestReconnect()
    {
        if (_disposed || _credentials is not { Authorized: true })
        {
            return;
        }

        try
        {
            _reconnectSignal.Release();
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Один цикл переподключения на всё время работы: пауза растёт с каждой неудачей (10 с … 5 мин).</summary>
    private async Task ReconnectLoopAsync()
    {
        var token = _shutdown.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _reconnectSignal.WaitAsync(token);
                while (_reconnectSignal.CurrentCount > 0)
                {
                    await _reconnectSignal.WaitAsync(token);
                }

                var delay = TimeSpan.FromSeconds(Math.Min(10 * Math.Pow(2, Math.Min(_reconnectFailures, 6)), 300));
                await Task.Delay(delay, token);
                await _lock.WaitAsync(token);
                try
                {
                    if (Status.State == TelegramState.Disconnected && HasSession && !await ResumeAsync())
                    {
                        _reconnectFailures++;
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Pepa закрывается.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Цикл переподключения к Telegram остановился.");
        }
    }

    // ───────────── Входящие сообщения ─────────────

    private async Task OnUpdateAsync(Connection connection, Update update)
    {
        if (update is not UpdateNewMessage { message: Message message } || message.flags.HasFlag(Message.Flags.out_))
        {
            return;
        }

        var chat = PeerOf(connection, message.peer_id);
        var from = message.from_id is null ? chat : PeerOf(connection, message.from_id);
        var incoming = new TelegramIncomingMessage
        {
            ClockOffset = await ClockOffsetAsync(connection),
            AccountId = connection.Client.UserId,
            Chat = chat,
            From = from,
            MessageId = message.id,
            Text = message.message ?? "",
            Date = new DateTimeOffset(DateTime.SpecifyKind(message.date, DateTimeKind.Utc)),
            ReplyToMessageId = message.reply_to is MessageReplyHeader { reply_to_msg_id: var replyTo } && replyTo != 0 ? replyTo : null,
        };

        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            if (connection.Life.IsCancellationRequested)
            {
                // Подключение закрывается: сообщение не принято — состояние не сохраняем, после перезапуска оно придёт снова.
                connection.Tainted = true;
                return;
            }

            try
            {
                await _publish(TelegramEvents.ToEvent(incoming));
                connection.MarkDirty();
                if (attempt > 1 && _connection == connection && Status.State == TelegramState.Connected)
                {
                    SetStatus(TelegramState.Connected, Status.Account, Warning);
                }

                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Не удалось принять сообщение Telegram {Chat}:{Message} (попытка {Attempt}).", chat.Key, message.id, attempt);
                if (attempt >= PublishAttempts)
                {
                    // Состояние больше не сохраняется: после перезапуска Pepa сообщение придёт снова (повтор отсеется по ключу).
                    connection.Tainted = true;
                    _stateWarning = $"Сообщение Telegram не удалось принять ({ex.Message}). Оно и следующие придут снова после перезапуска Pepa.";
                    if (_connection == connection)
                    {
                        SetStatus(Status.State, Status.Account, Warning);
                    }

                    return;
                }

                if (attempt == 1 && _connection == connection)
                {
                    SetStatus(Status.State, Status.Account, $"Не удалось принять сообщение Telegram ({ex.Message}). Повторяю.");
                }
            }

            try
            {
                await Task.Delay(delay, connection.Life.Token);
            }
            catch (OperationCanceledException)
            {
                connection.Tainted = true;
                return;
            }

            delay *= 2;
        }
    }

    /// <summary>Сохраняет состояние обновлений вне обработчика: вскоре после подключения, после принятых сообщений и раз в 5 минут.</summary>
    private async Task SaveLoopAsync(Connection connection)
    {
        var token = connection.Life.Token;
        var lastSave = DateTime.MinValue;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            while (!token.IsCancellationRequested)
            {
                if (connection.LoggedIn && !_caughtUpReported)
                {
                    await ReportCaughtUpAsync(connection, token);
                }

                if (connection.LoggedIn && (connection.TakeDirty() || DateTime.UtcNow - lastSave > TimeSpan.FromMinutes(5)))
                {
                    await SaveStateAsync(connection, TimeSpan.FromMinutes(2), token);
                    lastSave = DateTime.UtcNow;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Сохранение состояния обновлений Telegram остановилось.");
        }
    }

    /// <summary>
    /// Догрузка после подключения закончена: библиотека получает пропущенные сообщения под своей блокировкой, и чтение
    /// состояния ждёт её конца. Тогда движок может засчитывать тайм-ауты ожиданий, истёкшие, пока Pepa была закрыта.
    /// </summary>
    private async Task ReportCaughtUpAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            // Список чатов догружает и каналы — дождаться его (без ошибки, если он не загрузился).
            await connection.Dialogs.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
            await Task.Run(() => connection.Manager.State, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            return;
        }

        _caughtUpReported = true;
        try
        {
            CaughtUp?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Обработчик окончания догрузки Telegram завершился с ошибкой.");
        }
    }

    /// <summary>
    /// Разница часов компьютера и Telegram: время сообщения сравнивается с часами компьютера (пришло ли сообщение, пока
    /// Pepa была закрыта, успел ли ответ до тайм-аута). Если часы компьютера спешат или отстают, без поправки свежий ответ
    /// можно принять за старый — и наоборот.
    /// </summary>
    private Task<TimeSpan> ClockOffsetAsync(Connection connection)
    {
        lock (connection)
        {
            return connection.ClockOffset ??= MeasureClockOffsetAsync(connection);
        }
    }

    private async Task<TimeSpan> MeasureClockOffsetAsync(Connection connection)
    {
        try
        {
            var before = DateTimeOffset.UtcNow;
            var serverTime = ServerClockForTests is { } clock
                ? await clock()
                : (await connection.Client.Updates_GetState().WaitAsync(TimeSpan.FromSeconds(15), connection.Life.Token)).date;
            var after = DateTimeOffset.UtcNow;
            var server = new DateTimeOffset(DateTime.SpecifyKind(serverTime, DateTimeKind.Utc));
            var offset = before + (after - before) / 2 - server;
            if (after - before > TimeSpan.FromSeconds(5) || offset.Duration() < TimeSpan.FromSeconds(3))
            {
                // Ответ шёл долго (поправка неточна) или часы почти совпадают (Telegram даёт время с точностью до секунды).
                return TimeSpan.Zero;
            }

            Logger.LogWarning("Часы компьютера отличаются от часов Telegram на {Seconds:0} с — время сообщений поправлено.", offset.TotalSeconds);
            if (offset.Duration() > TimeSpan.FromMinutes(2))
            {
                _clockWarning = $"Часы компьютера {(offset > TimeSpan.Zero ? "спешат" : "отстают")} на {FormatOffset(offset)}. " +
                                "Pepa учитывает это для сообщений Telegram, но лучше включить синхронизацию времени в Windows.";
                SetStatus(Status.State, Status.Account, Warning);
            }

            return offset;
        }
        catch (Exception ex)
        {
            // Без поправки сообщение всё равно принимается: лучше неточное время, чем потерянное сообщение.
            Logger.LogDebug(ex, "Не удалось сверить часы с Telegram.");
            return TimeSpan.Zero;
        }
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var d = offset.Duration();
        return d >= TimeSpan.FromHours(1) ? $"{d.TotalHours:0.#} ч" : $"{d.TotalMinutes:0} мин";
    }

    /// <summary>
    /// Журнал библиотеки обновлений. Некоторые её записи значат, что часть обновлений пропущена: запрос пропущенного
    /// упал, Telegram ответил «пропущено слишком много» или разрыв не удалось заполнить. Pepa не скрывает этого: сообщает
    /// в состоянии подключения и не сохраняет сдвинутое состояние — после перезапуска библиотека попробует догрузить снова.
    /// </summary>
    private void OnManagerLog(Connection connection, int level, string message)
    {
        if (level >= 3 && !_disposed)
        {
            Logger.LogWarning("Telegram (обновления): {Message}", message);
        }

        var transient = (level >= 4 && message.StartsWith("GetDifference(", StringComparison.Ordinal) && message.Contains(" raised ", StringComparison.Ordinal)) ||
                        message.Contains("forcibly removed", StringComparison.Ordinal) ||
                        message.Contains("No access_hash to recover", StringComparison.Ordinal);
        var permanent = message.StartsWith("DifferenceTooLong", StringComparison.Ordinal) || message.StartsWith("ChannelDifferenceTooLong", StringComparison.Ordinal);
        if (!transient && !permanent || connection.Life.IsCancellationRequested)
        {
            return;
        }

        if (transient)
        {
            connection.Tainted = true;
        }

        _stateWarning = transient
            ? "Telegram не отдал часть пропущенных обновлений: некоторые сообщения могли не дойти до Pepa. После перезапуска Pepa попробует " +
              "догрузить их снова; если предупреждение повторится — проверьте нужные чаты вручную."
            : "Пока Pepa была отключена, сообщений накопилось слишком много, и Telegram отдал не все: некоторые могли не дойти до Pepa. " +
              "Проверьте нужные чаты вручную.";
        if (_connection == connection)
        {
            SetStatus(Status.State, Status.Account, Warning);
        }
    }

    /// <summary>Предупреждения подключения: о пропущенных сообщениях и о часах компьютера.</summary>
    private string? Warning => _stateWarning is null ? _clockWarning : _clockWarning is null ? _stateWarning : $"{_stateWarning} {_clockWarning}";

    private async Task SaveStateAsync(Connection connection, TimeSpan wait, CancellationToken cancellationToken)
    {
        if (connection.Tainted)
        {
            return;
        }

        IReadOnlyDictionary<long, MBoxState> state;
        try
        {
            // Чтение состояния ждёт, пока библиотека закончит текущий вызов обработчика.
            state = await Task.Run(() => (IReadOnlyDictionary<long, MBoxState>)connection.Manager.State, CancellationToken.None)
                .WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("Состояние обновлений Telegram не прочиталось за {Seconds} с — сохраню позже.", wait.TotalSeconds);
            connection.MarkDirty();
            return;
        }

        // Неудача обработчика случается под той же блокировкой, поэтому после чтения состояния она уже видна.
        if (connection.Tainted || state.Count < 3)
        {
            return;
        }

        try
        {
            WriteAtomic(StatePath, JsonSerializer.Serialize(state.ToDictionary(p => p.Key, p => p.Value)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Не удалось сохранить состояние обновлений Telegram.");
            connection.MarkDirty();
        }
    }

    private Dictionary<long, MBoxState>? LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<long, MBoxState>>(File.ReadAllText(StatePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Состояние обновлений Telegram повреждено — начинаю с текущего.");
            _stateWarning = "Файл состояния Telegram был повреждён: сообщения, пришедшие, пока Pepa была закрыта, могли не загрузиться.";
            DeleteFiles(throwOnError: false, StatePath);
            return null;
        }
    }

    private TelegramPeer PeerOf(Connection connection, Peer peer)
    {
        var info = connection.Manager.UserOrChat(peer);
        if (info is not null)
        {
            return Remember(info);
        }

        var kind = peer switch
        {
            PeerChat => TelegramPeerKind.Chat,
            PeerChannel => TelegramPeerKind.Channel,
            _ => TelegramPeerKind.User,
        };
        var fallback = new TelegramPeer(kind, peer.ID, peer.ID.ToString(CultureInfo.InvariantCulture), null);
        return _peers.GetValueOrDefault(fallback.Key) ?? fallback;
    }

    private TelegramPeer Remember(IPeerInfo info)
    {
        var kind = info switch
        {
            User => TelegramPeerKind.User,
            Channel => TelegramPeerKind.Channel,
            _ => TelegramPeerKind.Chat,
        };
        var title = info switch
        {
            User user => user.ToString() ?? user.ID.ToString(CultureInfo.InvariantCulture),
            ChatBase chat => chat.Title,
            _ => info.ID.ToString(CultureInfo.InvariantCulture),
        };
        var peer = new TelegramPeer(kind, info.ID, title, string.IsNullOrEmpty(info.MainUsername) ? null : info.MainUsername);
        // «Неполные» сведения (min) из больших групп не годятся для отправки — не заменять ими полные.
        var partial = info is User { flags: var userFlags } && userFlags.HasFlag(User.Flags.min) ||
                      info is Channel { flags: var channelFlags } && channelFlags.HasFlag(Channel.Flags.min);
        if (partial && _peers.ContainsKey(peer.Key))
        {
            return _peers[peer.Key];
        }

        _peers[peer.Key] = peer;
        if (info.ToInputPeer() is { } input)
        {
            _inputPeers[peer.Key] = input;
        }

        return peer;
    }

    // ───────────── Для блоков ─────────────

    private Connection Connected()
    {
        var connection = _connection;
        return Status.State == TelegramState.Connected && connection is { LoggedIn: true }
            ? connection
            : throw new TelegramUnavailableException(Status.State switch
            {
                TelegramState.NotConfigured or TelegramState.LoginRequired or TelegramState.WaitingCode or TelegramState.WaitingPassword => NotConnectedHint,
                _ => "Сейчас нет связи с Telegram. Pepa подключится сама; попробуйте позже.",
            });
    }

    public async Task<TelegramPeer> ResolveAsync(string chat, CancellationToken cancellationToken)
    {
        var connection = Connected();
        var client = connection.Client;
        var text = chat.Trim();
        if (text.Length == 0)
        {
            throw new TelegramChatNotFoundException("Не указан чат.");
        }

        if (text is "me" or "self" or "Избранное" or "избранное")
        {
            var self = new TelegramPeer(TelegramPeerKind.User, client.UserId, "Избранное", client.User?.MainUsername);
            _inputPeers[self.Key] = InputPeer.Self;
            _peers[self.Key] = self;
            return self;
        }

        if (TelegramEvents.TryParseKey(text, out var kind, out var id))
        {
            var key = new TelegramPeer(kind, id, "", null).Key;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_peers.TryGetValue(key, out var known) && _inputPeers.ContainsKey(key))
                {
                    return known;
                }

                // Список диалогов ещё загружается (сразу после подключения) — дождаться и посмотреть снова.
                await connection.Dialogs.WaitAsync(cancellationToken);
            }

            throw new TelegramChatNotFoundException($"Чат {key} не найден среди ваших диалогов.");
        }

        var phone = text.StartsWith('+') || text.All(c => char.IsDigit(c) || c is ' ' or '-' or '(' or ')');
        var name = phone ? NormalizePhone(text) : text.TrimStart('@');
        // Поиск по имени Telegram строго ограничивает по частоте — найденное запоминается.
        if (_byName.TryGetValue(name, out var cached) && DateTime.UtcNow - cached.At < NameCacheLifetime && _inputPeers.ContainsKey(cached.Peer.Key))
        {
            return cached.Peer;
        }

        try
        {
            var resolved = phone
                ? await client.Contacts_ResolvePhone(name).WaitAsync(cancellationToken)
                : await client.Contacts_ResolveUsername(name).WaitAsync(cancellationToken);
            var peer = Remember(resolved.UserOrChat);
            _byName[name] = (peer, DateTime.UtcNow);
            return peer;
        }
        catch (RpcException ex) when (ex.Message.Contains("NOT_OCCUPIED", StringComparison.Ordinal) ||
                                      ex.Message.Contains("INVALID", StringComparison.Ordinal) ||
                                      ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
        {
            throw new TelegramChatNotFoundException($"Чат «{text}» не найден в Telegram ({ex.Message}).");
        }
        catch (RpcException ex) when (IsSessionRevoked(ex))
        {
            OnSessionRevoked(connection);
            throw new TelegramUnavailableException(SessionExpiredHint);
        }
        catch (RpcException ex)
        {
            throw new TelegramUnavailableException($"Telegram не дал найти чат «{text}»: {ex.Message}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Поиск чата ничего не меняет: при любой ошибке связи действие просто не выполнялось.
            throw new TelegramUnavailableException($"Не удалось найти чат «{text}»: нет связи с Telegram ({ex.Message}).");
        }
    }

    public async Task<int> LastMessageIdAsync(TelegramPeer chat, CancellationToken cancellationToken)
    {
        var (connection, input) = Target(chat);
        try
        {
            var history = await connection.Client.Messages_GetHistory(input, limit: 1).WaitAsync(cancellationToken);
            return history.Messages.Length == 0 ? 0 : history.Messages.Max(m => m.ID);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TelegramUnavailableException))
        {
            throw Unavailable(connection, ex, "прочитать историю чата");
        }
    }

    public async Task<TelegramSentMessage> SendAsync(TelegramPeer chat, string text, long randomId, CancellationToken cancellationToken)
    {
        var (connection, input) = Target(chat);
        UpdatesBase updates;
        try
        {
            updates = await connection.Client.Messages_SendMessage(input, text, randomId).WaitAsync(cancellationToken);
        }
        catch (RpcException ex) when (IsSessionRevoked(ex))
        {
            OnSessionRevoked(connection);
            throw new TelegramSendRejectedException(SessionExpiredHint, retryable: false);
        }
        catch (RpcException ex) when (ex.Message.Contains("RANDOM_ID_DUPLICATE", StringComparison.Ordinal))
        {
            // С этим ключом сообщение уже отправлялось (прошлой попыткой) — оно есть, второго не будет.
            throw new TelegramAlreadySentException();
        }
        catch (RpcException ex) when (ex.Code == 420 || ex.Message.Contains("FLOOD", StringComparison.Ordinal))
        {
            throw new TelegramSendRejectedException($"Telegram просит подождать перед отправкой ({ex.Message}) — сообщение не отправлено.", retryable: true);
        }
        catch (RpcException ex) when (ex.Code is >= 400 and < 500)
        {
            // Отказ Telegram — сообщение не отправлено. Внутренние ошибки сервера (500) — итог неизвестен.
            throw new TelegramSendRejectedException(RejectReason(ex), retryable: false);
        }

        var messageId = 0;
        var date = DateTime.UtcNow;
        if (updates is UpdateShortSentMessage sent)
        {
            messageId = sent.id;
            date = sent.date;
        }
        else
        {
            foreach (var update in updates.UpdateList)
            {
                if (update is UpdateMessageID assigned && assigned.random_id == randomId)
                {
                    messageId = assigned.id;
                }
            }

            foreach (var update in updates.UpdateList)
            {
                if (update is UpdateNewMessage { message: Message message } && message.flags.HasFlag(Message.Flags.out_) &&
                    (messageId == 0 || message.id == messageId))
                {
                    messageId = message.id;
                    date = message.date;
                    break;
                }
            }
        }

        return new TelegramSentMessage(chat, messageId, new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)));
    }

    public async Task<IReadOnlyList<TelegramHistoryMessage>> MessagesAfterAsync(TelegramPeer chat, int afterId, int max, CancellationToken cancellationToken)
    {
        var (connection, input) = Target(chat);
        var result = new List<TelegramHistoryMessage>();
        try
        {
            var offsetId = 0;
            while (result.Count < max)
            {
                var page = await connection.Client.Messages_GetHistory(input, offset_id: offsetId, limit: 100, min_id: afterId)
                    .WaitAsync(cancellationToken);
                if (page.Messages.Length == 0)
                {
                    break;
                }

                result.AddRange(page.Messages.OfType<Message>().Where(m => m.id > afterId)
                    .Select(m => new TelegramHistoryMessage(m.id, m.flags.HasFlag(Message.Flags.out_), m.message ?? "",
                        new DateTimeOffset(DateTime.SpecifyKind(m.date, DateTimeKind.Utc)))));
                offsetId = page.Messages.Min(m => m.ID);
                if (page.Messages.Length < 100 || offsetId <= afterId + 1)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TelegramUnavailableException))
        {
            throw Unavailable(connection, ex, "прочитать историю чата");
        }

        return result.Take(max).ToList();
    }

    private (Connection Connection, InputPeer Input) Target(TelegramPeer chat)
    {
        var connection = Connected();
        return (connection, _inputPeers.GetValueOrDefault(chat.Key) ?? throw new TelegramChatNotFoundException($"Чат {chat.Key} не найден."));
    }

    private TelegramUnavailableException Unavailable(Connection connection, Exception ex, string what)
    {
        if (ex is RpcException rpc && IsSessionRevoked(rpc))
        {
            OnSessionRevoked(connection);
            return new TelegramUnavailableException(SessionExpiredHint);
        }

        return new TelegramUnavailableException($"Не удалось {what}: {ex.Message}");
    }

    private static string RejectReason(RpcException ex) => ex.Message switch
    {
        var m when m.Contains("CHAT_WRITE_FORBIDDEN", StringComparison.Ordinal) || m.Contains("CHAT_ADMIN_REQUIRED", StringComparison.Ordinal) =>
            "В этот чат вам нельзя писать — сообщение не отправлено.",
        var m when m.Contains("USER_IS_BLOCKED", StringComparison.Ordinal) || m.Contains("YOU_BLOCKED_USER", StringComparison.Ordinal) =>
            "Переписка заблокирована — сообщение не отправлено.",
        var m when m.Contains("MESSAGE_TOO_LONG", StringComparison.Ordinal) => "Сообщение длиннее 4096 символов — не отправлено.",
        var m when m.Contains("MESSAGE_EMPTY", StringComparison.Ordinal) => "Сообщение пустое — не отправлено.",
        var m when m.Contains("PEER_ID_INVALID", StringComparison.Ordinal) || m.Contains("INPUT_USER_DEACTIVATED", StringComparison.Ordinal) =>
            "Чат недоступен (удалён или Telegram не знает его для вашего аккаунта) — сообщение не отправлено.",
        _ => $"Telegram не принял сообщение ({ex.Message}) — оно не отправлено.",
    };

    // ───────────── Вспомогательное ─────────────

    private TelegramStatus SetStatus(TelegramState state, string? account, string? message)
    {
        var status = new TelegramStatus(state, account, message);
        Volatile.Write(ref _status, status);
        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Ошибка в обработчике состояния Telegram.");
        }

        return status;
    }

    internal static bool IsApiHash(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);

    internal static string NormalizePhone(string phone) =>
        "+" + new string(phone.Where(char.IsDigit).ToArray());

    private TelegramCredentials? LoadCredentials()
    {
        try
        {
            if (!File.Exists(CredentialsPath) || JsonSerializer.Deserialize<StoredCredentials>(File.ReadAllText(CredentialsPath)) is not { } stored)
            {
                return null;
            }

            return new TelegramCredentials(stored.ApiId, Secrets.Unprotect(stored.ApiHash, SecretPurpose), stored.Phone)
            {
                Authorized = stored.Authorized,
                SessionKey = stored.SessionKey is { } key ? Secrets.Unprotect(key, SecretPurpose) : null,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            Logger.LogWarning(ex, "Данные входа Telegram не прочитались — нужен повторный вход.");
            return null;
        }
    }

    private void SaveCredentials(TelegramCredentials credentials)
    {
        Directory.CreateDirectory(_folder);
        WriteAtomic(CredentialsPath, JsonSerializer.Serialize(new StoredCredentials(credentials.ApiId, Secrets.Protect(credentials.ApiHash, SecretPurpose),
            credentials.Phone, credentials.Authorized, credentials.SessionKey is { } key ? Secrets.Protect(key, SecretPurpose) : null)));
    }

    private void TrySaveCredentials(TelegramCredentials credentials)
    {
        try
        {
            SaveCredentials(credentials);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Не удалось сохранить данные входа Telegram.");
        }
    }

    /// <summary>Записать файл целиком или не трогать: сбой посередине не оставляет обрезанный файл.</summary>
    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
            // На диск до переименования: иначе после отключения питания файл может оказаться пустым.
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <returns>Все файлы удалены (или их не было).</returns>
    private bool DeleteFiles(bool throwOnError, params string[] files)
    {
        var ok = true;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (!throwOnError && ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Не удалось удалить {File}.", file);
                ok = false;
            }
        }

        return ok;
    }

    private async Task DisposeConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
        {
            return;
        }

        // Остановить сохранение и повторы обработчика; итоговое сохранение — только если все сообщения приняты.
        connection.Life.Cancel();
        try
        {
            await connection.Saver.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("Сохранение состояния Telegram не остановилось за 5 секунд.");
        }

        if (connection.LoggedIn)
        {
            await SaveStateAsync(connection, TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        connection.Abandoned = true;
        try
        {
            await connection.Client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("Подключение к Telegram не закрылось за 5 секунд.");
        }

        if (connection.AwaitsInput)
        {
            // Брошенный вход: библиотека держит поток в ожидании ответа. Пустой ответ отпускает его, а на следующий
            // вопрос библиотека получит отказ (Abandoned) — и завершит вход. Клиент уже закрыт: в Telegram ничего не уйдёт.
            connection.AwaitsInput = false;
            var client = connection.Client;
            _ = Task.Run(() => client.Login("")).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>
    /// Pepa закрывается: прервать подключение или вход, которые сейчас ждут Telegram (иначе закрытие ждало бы их),
    /// и больше не переподключаться.
    /// </summary>
    public void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        BeginShutdown();
        await _lock.WaitAsync();
        try
        {
            await DisposeConnectionAsync();
        }
        finally
        {
            _lock.Release();
        }

        await _reconnectLoop;
        _lock.Dispose();
        _reconnectSignal.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Одно подключение к Telegram: клиент, менеджер обновлений и всё, что живёт ровно столько же.</summary>
    private sealed class Connection(bool interactive)
    {
        private int _dirty;

        public bool Interactive { get; } = interactive;
        public WTelegram.Client Client { get; set; } = null!;
        public WTelegram.UpdateManager Manager { get; set; } = null!;
        public CancellationTokenSource Life { get; } = new();
        public Task Saver { get; set; } = Task.CompletedTask;
        public Task Dialogs { get; set; } = Task.CompletedTask;
        public volatile bool LoggedIn;

        /// <summary>Вход брошен: на вопросы библиотеки отвечать отказом.</summary>
        public volatile bool Abandoned;

        /// <summary>Библиотека ждёт ответа на вопрос входа.</summary>
        public volatile bool AwaitsInput;

        /// <summary>Какое-то сообщение не принято движком: состояние обновлений больше не сохранять.</summary>
        public volatile bool Tainted;

        /// <summary>
        /// Насколько часы компьютера отличаются от часов Telegram (прибавить к времени Telegram). Измеряется один раз на
        /// подключение, до первого принятого сообщения.
        /// </summary>
        public Task<TimeSpan>? ClockOffset;

        public void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

        public bool TakeDirty() => Interlocked.Exchange(ref _dirty, 0) == 1;
    }

    private const string SecretPurpose = "Pepa.Telegram";

    private sealed record StoredCredentials(int ApiId, string ApiHash, string Phone, bool Authorized, string? SessionKey);

    /// <summary>Библиотека попросила данные входа — значит, сессии нет или она недействительна.</summary>
    private sealed class LoginRequiredException : Exception;
}

/// <summary>Ошибка входа в Telegram с понятным текстом.</summary>
public sealed class TelegramLoginException(string message) : Exception(message);
