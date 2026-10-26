using System.Text.Json.Nodes;
using Pepa.Core;

namespace Pepa.Adapters.Telegram;

public enum TelegramPeerKind
{
    User,
    Chat,
    Channel,
}

/// <summary>
/// Собеседник или чат. <see cref="Key"/> — устойчивый ключ сопоставления («user:123», «chat:45», «channel:67»):
/// по нему процесс, отправивший сообщение, узнаёт ответ именно из этого чата.
/// </summary>
public sealed record TelegramPeer(TelegramPeerKind Kind, long Id, string Title, string? Username)
{
    public string Key => $"{Kind.ToString().ToLowerInvariant()}:{Id}";
}

/// <summary>Входящее сообщение.</summary>
public sealed record TelegramIncomingMessage
{
    /// <summary>Аккаунт, который получил сообщение: номера сообщений уникальны только в пределах аккаунта.</summary>
    public long AccountId { get; init; }

    public required TelegramPeer Chat { get; init; }
    public TelegramPeer? From { get; init; }
    public required int MessageId { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Date { get; init; }
    public int? ReplyToMessageId { get; init; }
}

/// <summary>Сообщение, отправленное процессом.</summary>
public sealed record TelegramSentMessage(TelegramPeer Chat, int MessageId, DateTimeOffset Date);

/// <summary>Сообщение из истории чата (для проверки, отправлено ли сообщение).</summary>
public sealed record TelegramHistoryMessage(int MessageId, bool Outgoing, string Text, DateTimeOffset Date);

public enum TelegramState
{
    /// <summary>Не настроено: нет данных для входа.</summary>
    NotConfigured,

    /// <summary>Нужен вход (или повторный вход: сессия истекла).</summary>
    LoginRequired,

    Connecting,

    /// <summary>Вход начат, Telegram прислал код — ждём его.</summary>
    WaitingCode,

    /// <summary>Включена двухэтапная проверка — ждём пароль.</summary>
    WaitingPassword,

    Connected,

    /// <summary>Связи нет (будет восстановлена автоматически).</summary>
    Disconnected,
}

public sealed record TelegramStatus(TelegramState State, string? Account, string? Message);

/// <summary>
/// Работа с Telegram для блоков. Отдельный интерфейс — чтобы блоки можно было проверить без настоящего Telegram.
/// <see cref="TelegramUnavailableException"/>, <see cref="TelegramChatNotFoundException"/> и
/// <see cref="TelegramSendRejectedException"/> означают, что действие точно не выполнялось; любое другое исключение
/// при отправке — итог неизвестен.
/// </summary>
public interface ITelegramGateway
{
    TelegramStatus Status { get; }

    /// <summary>Найти чат: @username, номер телефона +7…, «me» (Избранное) или ключ user:123 / chat:45 / channel:67.</summary>
    Task<TelegramPeer> ResolveAsync(string chat, CancellationToken cancellationToken);

    /// <summary>Номер последнего сообщения чата (0 — сообщений нет). Только чтение: отметка «до отправки» для проверки.</summary>
    Task<int> LastMessageIdAsync(TelegramPeer chat, CancellationToken cancellationToken);

    /// <summary>Отправить сообщение. <paramref name="randomId"/> — ключ от повторов: Telegram не примет его дважды.</summary>
    Task<TelegramSentMessage> SendAsync(TelegramPeer chat, string text, long randomId, CancellationToken cancellationToken);

    /// <summary>Сообщения чата с номером больше <paramref name="afterId"/>, новые первыми, не больше <paramref name="max"/>.</summary>
    Task<IReadOnlyList<TelegramHistoryMessage>> MessagesAfterAsync(TelegramPeer chat, int afterId, int max, CancellationToken cancellationToken);
}

/// <summary>Telegram не подключён или недоступен — действие не выполнялось.</summary>
public sealed class TelegramUnavailableException(string message) : Exception(message);

/// <summary>Чат не найден — действие не выполнялось.</summary>
public sealed class TelegramChatNotFoundException(string message) : Exception(message);

/// <summary>Telegram отказался принять сообщение (нет прав, слишком длинное, ограничение частоты…) — оно не отправлено.</summary>
public sealed class TelegramSendRejectedException(string message, bool retryable) : Exception(message)
{
    /// <summary>Повтор позже может пройти (например, после ограничения частоты).</summary>
    public bool Retryable { get; } = retryable;
}

/// <summary>Входящие сообщения — события Pepa с источником «telegram».</summary>
public static class TelegramEvents
{
    public const string Source = "telegram";
    public const string MessageType = "message";

    public static EventInput ToEvent(TelegramIncomingMessage message) => new()
    {
        Source = Source,
        Type = MessageType,
        ExternalId = $"{message.AccountId}:{message.Chat.Key}:{message.MessageId}",
        CorrelationKey = message.Chat.Key,
        OccurredAt = message.Date,
        Payload = new JsonObject
        {
            ["text"] = message.Text,
            ["messageId"] = message.MessageId,
            ["date"] = message.Date.ToString("O"),
            ["replyTo"] = message.ReplyToMessageId,
            ["chat"] = PeerJson(message.Chat),
            ["from"] = message.From is null ? null : PeerJson(message.From),
        },
    };

    public static JsonObject PeerJson(TelegramPeer peer) => new()
    {
        ["key"] = peer.Key,
        ["kind"] = peer.Kind.ToString().ToLowerInvariant(),
        ["id"] = peer.Id,
        ["title"] = peer.Title,
        ["username"] = peer.Username,
    };

    /// <summary>Текст так, как его хранит Telegram: без пробелов по краям, переводы строк — «\n».</summary>
    public static string NormalizeText(string text) => text.ReplaceLineEndings("\n").Trim();

    /// <summary>Разобрать ключ вида «user:123».</summary>
    public static bool TryParseKey(string text, out TelegramPeerKind kind, out long id)
    {
        kind = default;
        id = 0;
        var parts = text.Trim().Split(':', 2);
        return parts.Length == 2 && Enum.TryParse(parts[0], ignoreCase: true, out kind) &&
               long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id);
    }
}
