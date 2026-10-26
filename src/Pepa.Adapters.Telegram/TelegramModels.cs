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
    /// <summary>Время отправки по часам Telegram.</summary>
    public required DateTimeOffset Date { get; init; }
    public int? ReplyToMessageId { get; init; }

    /// <summary>Поправка на часы компьютера (их отличие от часов Telegram): время события сравнивается с часами компьютера.</summary>
    public TimeSpan ClockOffset { get; init; }

    /// <summary>Кнопки под сообщением (бот) или клавиатура бота.</summary>
    public IReadOnlyList<TelegramButton> Buttons { get; init; } = [];

    /// <summary>Вложение (фото, документ, голосовое…), если есть.</summary>
    public TelegramMedia? Media { get; init; }
}

public enum TelegramButtonKind
{
    /// <summary>Кнопка под сообщением: нажатие сообщает боту, бот отвечает (всплывающим текстом, новым сообщением…).</summary>
    Callback,

    /// <summary>Кнопка-ссылка: открывает адрес.</summary>
    Url,

    /// <summary>Кнопка клавиатуры бота: нажатие отправляет её текст сообщением.</summary>
    Text,

    /// <summary>Другие (оплата, игра, запрос телефона…) — Pepa их не нажимает.</summary>
    Other,
}

/// <param name="Row">Ряд (с 1).</param>
/// <param name="Column">Место в ряду (с 1).</param>
/// <param name="Keyboard">Клавиатура бота (под полем ввода), а не кнопки под сообщением.</param>
public sealed record TelegramButton(int Row, int Column, string Text, TelegramButtonKind Kind, string? Url = null, bool Keyboard = false);

/// <param name="Kind">photo, document, video, voice, audio, sticker, other.</param>
public sealed record TelegramMedia(string Kind, string? FileName, long? Size, string? MimeType);

/// <summary>Сообщение, отправленное процессом.</summary>
public sealed record TelegramSentMessage(TelegramPeer Chat, int MessageId, DateTimeOffset Date);

/// <summary>Сообщение из истории чата (для проверки, отправлено ли сообщение; кнопки и вложение — для «Нажать кнопку» и «Сохранить вложение»).</summary>
public sealed record TelegramHistoryMessage(int MessageId, bool Outgoing, string Text, DateTimeOffset Date)
{
    public IReadOnlyList<TelegramButton> Buttons { get; init; } = [];
    public TelegramMedia? Media { get; init; }
}

/// <summary>Ответ бота на нажатие кнопки (всплывающее уведомление или ссылка); бот может и ничего не ответить.</summary>
public sealed record TelegramCallbackAnswer(string? Message, string? Url, bool Alert);

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

    /// <summary>Сообщение чата по номеру; null — его нет (удалено или номер неверный).</summary>
    Task<TelegramHistoryMessage?> GetMessageAsync(TelegramPeer chat, int messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Нажать кнопку под сообщением (<see cref="TelegramButtonKind.Callback"/>): Telegram передаёт нажатие боту и возвращает
    /// его ответ. Кнопка ищется заново по месту и тексту — если сообщение изменилось, нажатия не будет.
    /// </summary>
    Task<TelegramCallbackAnswer> PressAsync(TelegramPeer chat, int messageId, TelegramButton button, CancellationToken cancellationToken);

    /// <summary>Отправить файл (с подписью). <paramref name="randomId"/> — ключ от повторов, как у сообщения.</summary>
    Task<TelegramSentMessage> SendFileAsync(TelegramPeer chat, string path, string? caption, long randomId, CancellationToken cancellationToken);

    /// <summary>Сохранить вложение сообщения в файл; возвращает размер.</summary>
    Task<long> DownloadAsync(TelegramPeer chat, int messageId, string targetPath, CancellationToken cancellationToken);
}

/// <summary>Telegram не подключён или недоступен — действие не выполнялось.</summary>
public sealed class TelegramUnavailableException(string message) : Exception(message);

/// <summary>В сообщении нет файла или фото — сохранять нечего.</summary>
public sealed class TelegramNoMediaException(string message) : Exception(message);

/// <summary>Чат не найден — действие не выполнялось.</summary>
public sealed class TelegramChatNotFoundException(string message) : Exception(message);

/// <summary>Telegram отказался принять сообщение (нет прав, слишком длинное, ограничение частоты…) — оно не отправлено.</summary>
public sealed class TelegramSendRejectedException(string message, bool retryable) : Exception(message)
{
    /// <summary>Повтор позже может пройти (например, после ограничения частоты).</summary>
    public bool Retryable { get; } = retryable;
}

/// <summary>Сообщение с этим ключом от повторов уже отправлено раньше (прошлой попыткой шага): Telegram второе не принял.</summary>
public sealed class TelegramAlreadySentException() : Exception("Telegram сообщает, что это сообщение уже отправлено прошлой попыткой.");

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
        OccurredAt = message.Date + message.ClockOffset,
        Payload = new JsonObject
        {
            ["text"] = message.Text,
            ["messageId"] = message.MessageId,
            ["date"] = message.Date.ToString("O"),
            ["replyTo"] = message.ReplyToMessageId,
            ["chat"] = PeerJson(message.Chat),
            ["from"] = message.From is null ? null : PeerJson(message.From),
            ["buttons"] = new JsonArray(message.Buttons.Select(b => (JsonNode)ButtonJson(b)).ToArray()),
            ["media"] = message.Media is { } media ? MediaJson(media) : null,
        },
    };

    public static JsonObject ButtonJson(TelegramButton button) => new()
    {
        ["row"] = button.Row,
        ["column"] = button.Column,
        ["text"] = button.Text,
        ["kind"] = button.Kind.ToString().ToLowerInvariant(),
        ["url"] = button.Url,
        ["keyboard"] = button.Keyboard,
    };

    public static JsonObject MediaJson(TelegramMedia media) => new()
    {
        ["kind"] = media.Kind,
        ["fileName"] = media.FileName,
        ["size"] = media.Size,
        ["mimeType"] = media.MimeType,
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
