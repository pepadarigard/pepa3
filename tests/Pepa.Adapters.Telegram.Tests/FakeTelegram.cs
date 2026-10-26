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

        RandomIds.Enqueue(randomId);
        // Как Telegram: края текста обрезаются.
        var message = new TelegramHistoryMessage(Interlocked.Increment(ref _nextId), true, TelegramEvents.NormalizeText(text), DateTimeOffset.UtcNow);
        Sent.Enqueue((chat, message));
        History.Enqueue((chat, message));
        if (LoseConfirmation)
        {
            throw new IOException("Связь оборвалась после отправки.");
        }

        return Task.FromResult(new TelegramSentMessage(chat, message.MessageId, message.Date));
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
