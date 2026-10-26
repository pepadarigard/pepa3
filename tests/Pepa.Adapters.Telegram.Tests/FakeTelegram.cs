using System.Collections.Concurrent;

namespace Pepa.Adapters.Telegram.Tests;

/// <summary>Имитация Telegram: известные чаты, история отправленных сообщений, сбои связи по заказу.</summary>
internal sealed class FakeTelegram : ITelegramGateway
{
    private readonly ConcurrentDictionary<string, TelegramPeer> _chats = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = 100;

    public FakeTelegram()
    {
        Add("@ivan", new TelegramPeer(TelegramPeerKind.User, 1001, "Иван", "ivan"));
        Add("@shop_bot", new TelegramPeer(TelegramPeerKind.User, 2002, "Магазин", "shop_bot"));
    }

    public TelegramStatus Status { get; set; } = new(TelegramState.Connected, "Тест", null);

    public ConcurrentQueue<(TelegramPeer Chat, TelegramHistoryMessage Message)> Sent { get; } = new();

    /// <summary>Сообщение уходит, но ответ сервера теряется (итог неизвестен).</summary>
    public bool LoseConfirmation { get; set; }

    /// <summary>Отправка падает до того, как сообщение ушло.</summary>
    public bool FailBeforeSend { get; set; }

    /// <summary>Telegram отказывается принять сообщение (например, нет прав писать в чат).</summary>
    public TelegramSendRejectedException? Reject { get; set; }

    /// <summary>Входящие и чужие сообщения в истории чатов (для проверки отправки).</summary>
    public ConcurrentQueue<(TelegramPeer Chat, TelegramHistoryMessage Message)> History { get; } = new();

    /// <summary>Ключи от повторов, с которыми отправлялись сообщения.</summary>
    public ConcurrentQueue<long> RandomIds { get; } = new();

    /// <summary>
    /// Связь оборвалась, а запрос остался в библиотеке: сообщение дойдёт позже (при следующем обращении к Telegram),
    /// а сейчас в истории его нет.
    /// </summary>
    public bool DeliverLater { get; set; }

    private readonly ConcurrentQueue<(TelegramPeer Chat, TelegramHistoryMessage Message)> _late = new();
    private readonly ConcurrentDictionary<long, bool> _usedRandomIds = new();

    public int NextId => Volatile.Read(ref _nextId);

    /// <summary>Чужое сообщение в истории чата (например, ответ собеседника или то же сообщение, отправленное с телефона).</summary>
    public TelegramHistoryMessage AddToHistory(TelegramPeer chat, string text, bool outgoing)
    {
        var message = new TelegramHistoryMessage(Interlocked.Increment(ref _nextId), outgoing, text, DateTimeOffset.UtcNow);
        History.Enqueue((chat, message));
        return message;
    }

    public void Add(string name, TelegramPeer peer)
    {
        _chats[name] = peer;
        _chats[peer.Key] = peer;
    }

    public Task<TelegramPeer> ResolveAsync(string chat, CancellationToken cancellationToken)
    {
        if (Status.State != TelegramState.Connected)
        {
            throw new TelegramUnavailableException("Telegram не подключён.");
        }

        return _chats.TryGetValue(chat.Trim(), out var peer)
            ? Task.FromResult(peer)
            : throw new TelegramChatNotFoundException($"Чат «{chat}» не найден.");
    }

    public Task<int> LastMessageIdAsync(TelegramPeer chat, CancellationToken cancellationToken) =>
        Status.State != TelegramState.Connected
            ? throw new TelegramUnavailableException("Telegram не подключён.")
            : Task.FromResult(All(chat).Select(m => m.MessageId).DefaultIfEmpty(0).Max());

    public Task<TelegramSentMessage> SendAsync(TelegramPeer chat, string text, long randomId, CancellationToken cancellationToken)
    {
        if (Reject is { } reject)
        {
            throw reject;
        }

        if (FailBeforeSend)
        {
            throw new IOException("Нет связи — сообщение не ушло.");
        }

        // Запрос, застрявший после обрыва, библиотека отправляет при восстановлении связи.
        while (_late.TryDequeue(out var late))
        {
            Sent.Enqueue(late);
            History.Enqueue(late);
        }

        // Как Telegram: второе сообщение с тем же ключом не принимается.
        if (!_usedRandomIds.TryAdd(randomId, true))
        {
            throw new TelegramAlreadySentException();
        }

        RandomIds.Enqueue(randomId);
        // Как Telegram: края текста обрезаются.
        var message = new TelegramHistoryMessage(Interlocked.Increment(ref _nextId), true, TelegramEvents.NormalizeText(text), DateTimeOffset.UtcNow);
        if (DeliverLater)
        {
            _late.Enqueue((chat, message));
            throw new IOException("Связь оборвалась; запрос остался в очереди библиотеки.");
        }

        Sent.Enqueue((chat, message));
        History.Enqueue((chat, message));
        if (LoseConfirmation)
        {
            throw new IOException("Связь оборвалась после отправки.");
        }

        return Task.FromResult(new TelegramSentMessage(chat, message.MessageId, message.Date));
    }

    /// <summary>Нажатые кнопки под сообщениями: (номер сообщения, текст кнопки).</summary>
    public ConcurrentQueue<(int MessageId, string Text)> Pressed { get; } = new();

    /// <summary>Что ответит бот на нажатие.</summary>
    public string? BotAnswer { get; set; }

    /// <summary>Отправленные файлы: (чат, имя файла, подпись).</summary>
    public ConcurrentQueue<(TelegramPeer Chat, string FileName, string? Caption)> Files { get; } = new();

    /// <summary>Содержимое вложений по номеру сообщения.</summary>
    public ConcurrentDictionary<int, byte[]> Attachments { get; } = new();

    /// <summary>Сообщение бота с кнопками (и, если нужно, вложением) в истории чата.</summary>
    public TelegramHistoryMessage AddBotMessage(TelegramPeer chat, string text, IReadOnlyList<TelegramButton>? buttons = null,
        TelegramMedia? media = null, byte[]? content = null)
    {
        var message = new TelegramHistoryMessage(Interlocked.Increment(ref _nextId), false, text, DateTimeOffset.UtcNow)
        {
            Buttons = buttons ?? [],
            Media = media,
        };
        History.Enqueue((chat, message));
        if (content is not null)
        {
            Attachments[message.MessageId] = content;
        }

        return message;
    }

    public Task<TelegramHistoryMessage?> GetMessageAsync(TelegramPeer chat, int messageId, CancellationToken cancellationToken) =>
        Task.FromResult(All(chat).FirstOrDefault(m => m.MessageId == messageId));

    /// <summary>Нажатие уходит, но ответ теряется (обрыв связи).</summary>
    public bool PressLosesAnswer { get; set; }

    public Task<TelegramCallbackAnswer> PressAsync(TelegramPeer chat, int messageId, TelegramButton button, CancellationToken cancellationToken)
    {
        Pressed.Enqueue((messageId, button.Text));
        return PressLosesAnswer
            ? Task.FromException<TelegramCallbackAnswer>(new IOException("Связь оборвалась после нажатия."))
            : Task.FromResult(new TelegramCallbackAnswer(BotAnswer, null, false));
    }

    public Task<TelegramSentMessage> SendFileAsync(TelegramPeer chat, string path, string? caption, long randomId, CancellationToken cancellationToken)
    {
        if (!_usedRandomIds.TryAdd(randomId, true))
        {
            throw new TelegramAlreadySentException();
        }

        Files.Enqueue((chat, Path.GetFileName(path), caption));
        var message = new TelegramHistoryMessage(Interlocked.Increment(ref _nextId), true, TelegramEvents.NormalizeText(caption ?? ""), DateTimeOffset.UtcNow)
        {
            Media = new TelegramMedia("document", Path.GetFileName(path), new FileInfo(path).Length, "application/octet-stream"),
        };
        History.Enqueue((chat, message));
        if (LoseConfirmation)
        {
            throw new IOException("Связь оборвалась после отправки.");
        }

        return Task.FromResult(new TelegramSentMessage(chat, message.MessageId, message.Date));
    }

    public async Task<long> DownloadAsync(TelegramPeer chat, int messageId, string targetPath, CancellationToken cancellationToken)
    {
        var content = Attachments.GetValueOrDefault(messageId) ?? throw new TelegramNoMediaException($"В сообщении №{messageId} нет файла.");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllBytesAsync(targetPath, content, cancellationToken);
        return content.Length;
    }

    public Task<IReadOnlyList<TelegramHistoryMessage>> MessagesAfterAsync(TelegramPeer chat, int afterId, int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TelegramHistoryMessage>>(All(chat).Where(m => m.MessageId > afterId)
            .OrderByDescending(m => m.MessageId).Take(max).ToList());

    private IEnumerable<TelegramHistoryMessage> All(TelegramPeer chat) => History.Where(h => h.Chat.Key == chat.Key).Select(h => h.Message);

    public static TelegramIncomingMessage Incoming(TelegramPeer chat, string text, int id, DateTimeOffset? date = null) => new()
    {
        Chat = chat,
        From = chat,
        MessageId = id,
        Text = text,
        Date = date ?? DateTimeOffset.UtcNow,
    };
}
