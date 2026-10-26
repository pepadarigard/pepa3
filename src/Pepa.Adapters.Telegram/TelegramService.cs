using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pepa.Core;
using TL;

namespace Pepa.Adapters.Telegram;

/// <summary>Данные для входа: api_id и api_hash выдаёт my.telegram.org, номер — телефон аккаунта.</summary>
public sealed record TelegramCredentials(int ApiId, string ApiHash, string Phone)
{
    /// <summary>Вход завершён и сессия действительна — при запуске Pepa подключаться автоматически.</summary>
    public bool Authorized { get; init; }
}

/// <summary>
/// Пользовательский аккаунт Telegram (MTProto, библиотека WTelegramClient). Получает входящие сообщения и передаёт их
/// как события Pepa; отправляет сообщения для блоков. Сессия и состояние обновлений хранятся в папке данных:
/// после перезапуска сообщения, пришедшие, пока Pepa была закрыта, догружаются (и попадают только к ожидающим
/// процессам — запуск по ним сам не выполняется).
/// </summary>
public sealed class TelegramService : ITelegramGateway, IAsyncDisposable
{
    private const string NoConnectionHint =
        "Telegram не ответил за отведённое время. Проверьте интернет; если Telegram недоступен в вашей сети, нужен VPN или прокси.";

    private readonly string _folder;
    private readonly Func<EventInput, Task> _publish;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, InputPeer> _inputPeers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TelegramPeer> _peers = new(StringComparer.Ordinal);
    private WTelegram.Client? _client;
    private WTelegram.UpdateManager? _manager;
    private TelegramCredentials? _credentials;
    private TelegramStatus _status;
    private CancellationTokenSource? _reconnect;
    private volatile bool _disposed;
    private bool _clientAwaitsInput;
    private Task _dialogs = Task.CompletedTask;
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
    }

    public TelegramStatus Status => Volatile.Read(ref _status);

    /// <summary>Журнал (задаётся после сборки хоста: сервис создаётся раньше системы журналов).</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Сколько ждать ответа Telegram при подключении и входе. Без ответа — понятная ошибка, а не вечное ожидание.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Адрес сервера Telegram «ip:порт»; null — основной сервер Telegram (проверки подставляют недоступный адрес).</summary>
    public string? ServerAddress { get; init; }

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
        if (!HasSession)
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

    private async Task ResumeAsync()
    {
        SetStatus(TelegramState.Connecting, null, "Подключение…");
        await DisposeClientAsync();
        try
        {
            _client = CreateClient(_credentials!, interactive: false);
            var user = await Limited(_client.LoginUserIfNeeded(null, reloginOnFailedResume: false));
            AfterLogin(user);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Pepa закрывается.
        }
        catch (LoginRequiredException)
        {
            await SessionExpiredAsync();
        }
        catch (RpcException ex) when (ex.Message.Contains("AUTH_KEY", StringComparison.Ordinal) || ex.Message.Contains("SESSION", StringComparison.Ordinal))
        {
            await SessionExpiredAsync();
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "Не удалось подключиться к Telegram.");
            await DisposeClientAsync();
            var reason = ex is TimeoutException ? NoConnectionHint : $"Нет связи с Telegram: {ex.Message}.";
            SetStatus(TelegramState.Disconnected, null, $"{reason} Повторю попытку.");
            ScheduleReconnect();
        }
    }

    private async Task SessionExpiredAsync()
    {
        _credentials = _credentials! with { Authorized = false };
        SaveCredentials(_credentials);
        // Исключение не нужно: это не ответ на запрос входа, а состояние для интерфейса.
        _ = await FailLoginAsync("Сессия Telegram больше не действует (вход отменён на другом устройстве или истёк) — войдите снова.");
    }

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
            CancelReconnect();
            await DisposeClientAsync();
            Directory.CreateDirectory(_folder);
            foreach (var file in new[] { SessionPath, StatePath })
            {
                File.Delete(file);
            }

            _credentials = credentials with { Phone = NormalizePhone(credentials.Phone), ApiHash = credentials.ApiHash.Trim(), Authorized = false };
            SaveCredentials(_credentials);
            SetStatus(TelegramState.Connecting, null, "Подключение к Telegram…");
            try
            {
                _client = CreateClient(_credentials, interactive: true);
            }
            catch (Exception ex) when (ex is not LoginRequiredException)
            {
                Logger.LogWarning(ex, "Не удалось начать вход в Telegram.");
                throw await FailLoginAsync($"Не удалось начать вход: {ex.Message}");
            }

            return await LoginStepAsync(_credentials.Phone);
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
            if (_client is null || Status.State is not (TelegramState.WaitingCode or TelegramState.WaitingPassword))
            {
                throw new TelegramLoginException("Вход не начат. Нажмите «Подключить» ещё раз.");
            }

            // Пароль может начинаться или заканчиваться пробелом — его не обрезаем.
            var input = Status.State == TelegramState.WaitingCode ? value.Trim() : value;
            if (input.Length == 0)
            {
                throw new TelegramLoginException(Status.State == TelegramState.WaitingCode ? "Введите код из Telegram." : "Введите пароль.");
            }

            return await LoginStepAsync(input);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<TelegramStatus> LoginStepAsync(string value)
    {
        var state = Status.State;
        string? next;
        _clientAwaitsInput = false;
        try
        {
            next = await Limited(_client!.Login(value));
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

        switch (next)
        {
            case null:
                break;
            case "verification_code":
                _clientAwaitsInput = true;
                return SetStatus(TelegramState.WaitingCode, null, state == TelegramState.WaitingCode
                    ? "Код не подошёл. Проверьте его и введите ещё раз."
                    : "Telegram отправил код в приложение Telegram (или по SMS). Введите его.");
            case "password":
                _clientAwaitsInput = true;
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
        AfterLogin(_client.User);
        return Status;
    }

    /// <summary>Вход не удался: закрыть подключение и вернуться к форме входа с причиной.</summary>
    private async Task<TelegramLoginException> FailLoginAsync(string message)
    {
        await DisposeClientAsync();
        // Неавторизованная сессия не нужна: без неё при следующем запуске Pepa не будет пытаться подключиться.
        foreach (var file in new[] { SessionPath, StatePath })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Не удалось удалить {File}.", file);
            }
        }

        SetStatus(TelegramState.LoginRequired, null, message);
        return new TelegramLoginException(message);
    }

    private void AfterLogin(User? user)
    {
        var name = user is null ? "аккаунт" : $"{user}{(user.MainUsername is { Length: > 0 } un ? $" (@{un})" : "")}";
        SetStatus(TelegramState.Connected, name, null);
        Logger.LogInformation("Telegram подключён: {Account}.", name);

        // Чаты из списка диалогов — чтобы находить их по ключу user:/chat:/channel: без поиска по имени. Загрузка в фоне:
        // у большого аккаунта она долгая (и Telegram может попросить подождать), а подключение от неё не зависит.
        var client = _client!;
        _dialogs = Task.Run(async () =>
        {
            try
            {
                var dialogs = await client.Messages_GetAllDialogs().WaitAsync(_shutdown.Token);
                foreach (var (_, u) in dialogs.users)
                {
                    Remember(u);
                }

                foreach (var (_, chat) in dialogs.chats)
                {
                    Remember(chat);
                }
            }
            catch (Exception ex) when (!_disposed)
            {
                Logger.LogWarning(ex, "Список чатов Telegram не загрузился: чаты по ключу будут находиться только после сообщений из них.");
            }
            catch (Exception) when (_disposed)
            {
                // Pepa закрывается.
            }
        });
    }

    public async Task LogoutAsync()
    {
        await _lock.WaitAsync();
        try
        {
            CancelReconnect();
            if (_client is not null && Status.State == TelegramState.Connected)
            {
                try
                {
                    await Limited(_client.Auth_LogOut());
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Выход из Telegram на сервере не подтвердился; локальная сессия удалена.");
                }
            }

            await DisposeClientAsync();
            foreach (var file in new[] { SessionPath, StatePath, CredentialsPath })
            {
                File.Delete(file);
            }

            _credentials = null;
            _inputPeers.Clear();
            _peers.Clear();
            SetStatus(TelegramState.NotConfigured, null, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <param name="interactive">
    /// Вход через <c>Client.Login</c>: номер, код и пароль библиотека запрашивает у <see cref="LoginStepAsync"/>, поэтому на эти
    /// вопросы здесь ответа нет (null). При восстановлении сессии такой вопрос означает, что сессия недействительна.
    /// </param>
    private WTelegram.Client CreateClient(TelegramCredentials credentials, bool interactive)
    {
        Directory.CreateDirectory(_folder);
        var client = new WTelegram.Client(what => what switch
        {
            "api_id" => credentials.ApiId.ToString(CultureInfo.InvariantCulture),
            "api_hash" => credentials.ApiHash,
            "session_pathname" => SessionPath,
            "device_model" => "Pepa",
            "app_version" => "Pepa",
            // Сохранённая сессия принадлежит этому аккаунту (номер не сверяем: в настройках он может быть записан иначе).
            "user_id" => "-1",
            "server_address" => ServerAddress,
            // Вход через Firebase доступен только официальным приложениям — пусть Telegram пришлёт код другим способом.
            "firebase" => "",
            // Никогда не спрашивать данные входа в консоли: вход идёт только через интерфейс Pepa.
            "phone_number" or "verification_code" or "password" or "first_name" or "last_name" or "email"
                or "email_verification_code" or "bot_token" => interactive ? null : throw new LoginRequiredException(),
            _ => null,
        });
        client.MaxAutoReconnects = 50;
        client.OnOther += OnOtherAsync;
        _manager = client.WithUpdateManager(OnUpdateAsync, StatePath);
        return client;
    }

    private Task OnOtherAsync(IObject item)
    {
        if (item is ReactorError error && !_disposed)
        {
            Logger.LogWarning(error.Exception, "Связь с Telegram потеряна.");
            SetStatus(TelegramState.Disconnected, Status.Account, "Связь с Telegram потеряна. Повторю подключение.");
            ScheduleReconnect();
        }

        return Task.CompletedTask;
    }

    private void ScheduleReconnect()
    {
        if (_disposed || _credentials is null)
        {
            return;
        }

        CancelReconnect();
        var cancel = _reconnect = new CancellationTokenSource();
        var token = cancel.Token;
        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(10);
            while (!token.IsCancellationRequested && Status.State == TelegramState.Disconnected)
            {
                try
                {
                    await Task.Delay(delay, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await _lock.WaitAsync(token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    if (!token.IsCancellationRequested && Status.State == TelegramState.Disconnected)
                    {
                        await ResumeAsync();
                    }
                }
                finally
                {
                    _lock.Release();
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        });
    }

    private void CancelReconnect()
    {
        _reconnect?.Cancel();
        _reconnect?.Dispose();
        _reconnect = null;
    }

    // ───────────── Входящие сообщения ─────────────

    private async Task OnUpdateAsync(Update update)
    {
        if (update is not UpdateNewMessage { message: Message message } || message.flags.HasFlag(Message.Flags.out_))
        {
            return;
        }

        var chat = PeerOf(message.peer_id);
        var from = message.from_id is null ? chat : PeerOf(message.from_id);
        var incoming = new TelegramIncomingMessage
        {
            Chat = chat,
            From = from,
            MessageId = message.id,
            Text = message.message ?? "",
            Date = new DateTimeOffset(DateTime.SpecifyKind(message.date, DateTimeKind.Utc)),
            ReplyToMessageId = message.reply_to is MessageReplyHeader { reply_to_msg_id: var replyTo } && replyTo != 0 ? replyTo : null,
        };

        try
        {
            await _publish(TelegramEvents.ToEvent(incoming));
            _manager?.SaveState(StatePath);
        }
        catch (Exception ex)
        {
            // Состояние не сохранено — после перезапуска сообщение придёт снова (повтор отсеется по ключу).
            Logger.LogError(ex, "Не удалось принять сообщение Telegram {Chat}:{Message}.", chat.Key, message.id);
        }
    }

    private TelegramPeer PeerOf(Peer peer)
    {
        var info = _manager?.UserOrChat(peer);
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
        var key = new TelegramPeer(kind, peer.ID, peer.ID.ToString(CultureInfo.InvariantCulture), null).Key;
        return _peers.GetValueOrDefault(key) ?? new TelegramPeer(kind, peer.ID, peer.ID.ToString(CultureInfo.InvariantCulture), null);
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
        _peers[peer.Key] = peer;
        if (info.ToInputPeer() is { } input)
        {
            _inputPeers[peer.Key] = input;
        }

        return peer;
    }

    // ───────────── Для блоков ─────────────

    private WTelegram.Client Connected() =>
        Status.State == TelegramState.Connected && _client is { } client
            ? client
            : throw new TelegramUnavailableException(Status.State switch
            {
                TelegramState.NotConfigured or TelegramState.LoginRequired =>
                    "Telegram не подключён. Подключите аккаунт: «Интеграции» → Telegram.",
                _ => "Сейчас нет связи с Telegram. Pepa подключится сама; попробуйте позже.",
            });

    public async Task<TelegramPeer> ResolveAsync(string chat, CancellationToken cancellationToken)
    {
        var client = Connected();
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
                await _dialogs.WaitAsync(cancellationToken);
            }

            throw new TelegramChatNotFoundException($"Чат {key} не найден среди ваших диалогов.");
        }

        try
        {
            Contacts_ResolvedPeer resolved;
            if (text.StartsWith('+') || text.All(c => char.IsDigit(c) || c is ' ' or '-' or '(' or ')'))
            {
                resolved = await client.Contacts_ResolvePhone(NormalizePhone(text)).WaitAsync(cancellationToken);
            }
            else
            {
                resolved = await client.Contacts_ResolveUsername(text.TrimStart('@')).WaitAsync(cancellationToken);
            }

            return Remember(resolved.UserOrChat);
        }
        catch (RpcException ex) when (ex.Message.Contains("NOT_OCCUPIED", StringComparison.Ordinal) ||
                                      ex.Message.Contains("INVALID", StringComparison.Ordinal) ||
                                      ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
        {
            throw new TelegramChatNotFoundException($"Чат «{text}» не найден в Telegram ({ex.Message}).");
        }
    }

    public async Task<TelegramSentMessage> SendAsync(TelegramPeer chat, string text, bool silent, CancellationToken cancellationToken)
    {
        var client = Connected();
        var input = _inputPeers.GetValueOrDefault(chat.Key) ?? throw new TelegramChatNotFoundException($"Чат {chat.Key} не найден.");
        var message = await client.SendMessageAsync(input, text).WaitAsync(cancellationToken);
        return new TelegramSentMessage(chat, message.id, new DateTimeOffset(DateTime.SpecifyKind(message.date, DateTimeKind.Utc)));
    }

    public async Task<IReadOnlyList<TelegramHistoryMessage>> RecentAsync(TelegramPeer chat, int limit, CancellationToken cancellationToken)
    {
        var client = Connected();
        var input = _inputPeers.GetValueOrDefault(chat.Key) ?? throw new TelegramChatNotFoundException($"Чат {chat.Key} не найден.");
        var history = await client.Messages_GetHistory(input, limit: limit).WaitAsync(cancellationToken);
        return history.Messages.OfType<Message>()
            .Select(m => new TelegramHistoryMessage(m.id, m.flags.HasFlag(Message.Flags.out_), m.message ?? "",
                new DateTimeOffset(DateTime.SpecifyKind(m.date, DateTimeKind.Utc))))
            .ToList();
    }

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
            return File.Exists(CredentialsPath)
                ? JsonSerializer.Deserialize<TelegramCredentials>(File.ReadAllText(CredentialsPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveCredentials(TelegramCredentials credentials)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(CredentialsPath, JsonSerializer.Serialize(credentials));
    }

    private async Task DisposeClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is null)
        {
            return;
        }

        try
        {
            _manager?.SaveState(StatePath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Не удалось сохранить состояние обновлений Telegram.");
        }

        _manager = null;
        client.OnOther -= OnOtherAsync;
        try
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("Подключение к Telegram не закрылось за 5 секунд.");
        }

        if (_clientAwaitsInput)
        {
            // Брошенный вход: библиотека держит поток в ожидании кода. Пустой ответ завершает его (клиент уже закрыт,
            // в Telegram ничего не уйдёт).
            _clientAwaitsInput = false;
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
        CancelReconnect();
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
            await DisposeClientAsync();
        }
        finally
        {
            _lock.Release();
        }

        _lock.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Библиотека попросила данные входа — значит, сессии нет или она недействительна.</summary>
    private sealed class LoginRequiredException : Exception;
}

/// <summary>Ошибка входа в Telegram с понятным текстом.</summary>
public sealed class TelegramLoginException(string message) : Exception(message);
