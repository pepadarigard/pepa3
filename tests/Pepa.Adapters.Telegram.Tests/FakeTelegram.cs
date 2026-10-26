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

    public Task<TelegramSentMessage> SendAsync(TelegramPeer chat, string text, bool silent, CancellationToken cancellationToken)
    {
        if (FailBeforeSend)
        {
            throw new IOException("Нет связи — сообщение не ушло.");
        }

        var message = new TelegramHistoryMessage(Interlocked.Increment(ref _nextId), true, text, DateTimeOffset.UtcNow);
        Sent.Enqueue((chat, message));
        if (LoseConfirmation)
        {
            throw new IOException("Связь оборвалась после отправки.");
        }

        return Task.FromResult(new TelegramSentMessage(chat, message.MessageId, message.Date));
    }

    public Task<IReadOnlyList<TelegramHistoryMessage>> RecentAsync(TelegramPeer chat, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TelegramHistoryMessage>>(Sent.Where(s => s.Chat.Key == chat.Key).Select(s => s.Message).Reverse().Take(limit).ToList());

    public static TelegramIncomingMessage Incoming(TelegramPeer chat, string text, int id, DateTimeOffset? date = null) => new()
    {
        Chat = chat,
        From = chat,
        MessageId = id,
        Text = text,
        Date = date ?? DateTimeOffset.UtcNow,
    };
}
