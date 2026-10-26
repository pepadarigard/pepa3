using System.Text.Json.Nodes;

namespace Pepa.Core;

/// <summary>Входящее событие: сообщение, сигнал другого процесса, внешний вызов и т.п.</summary>
public sealed record EventInput
{
    /// <summary>Откуда событие: «manual», «http», «process», «pepa.process», в будущем «telegram» и т.д.</summary>
    public required string Source { get; init; }

    /// <summary>Вид события внутри источника, например «message» или «finished».</summary>
    public string Type { get; init; } = "";

    /// <summary>Идентификатор события у источника — для защиты от повторной обработки.</summary>
    public string? ExternalId { get; init; }

    /// <summary>Ключ сопоставления: например, номер заказа или диалога, который ждёт процесс.</summary>
    public string? CorrelationKey { get; init; }

    public JsonNode? Payload { get; init; }

    /// <summary>
    /// Когда событие произошло у источника. Событие, произошедшее до запуска Pepa (например, сообщение, полученное,
    /// пока приложение было закрыто), отдаётся ожидающим процессам, но процессы по нему сами не запускаются.
    /// </summary>
    public DateTimeOffset? OccurredAt { get; init; }
}

/// <summary>Чего ждёт процесс: правила, по которым событие считается «его».</summary>
public sealed record EventSubscriptionSpec
{
    public required string Source { get; init; }

    /// <summary>null или пусто — любой вид события этого источника.</summary>
    public string? EventType { get; init; }

    /// <summary>Если задан — ключ события обязан совпасть точно.</summary>
    public string? CorrelationKey { get; init; }

    /// <summary>Дополнительное условие (JavaScript): доступны <c>event</c>, <c>vars</c>, <c>process</c>.</summary>
    public string? MatchExpression { get; init; }

    /// <summary>При нескольких подходящих процессах событие получает тот, у кого приоритет выше; при равенстве — конфликт.</summary>
    public int Priority { get; init; }

    /// <summary>Принимать события, пришедшие не раньше чем за столько секунд до начала ожидания.</summary>
    public int LookbackSeconds { get; init; }

    /// <summary>Переменная, в которую сохранить событие.</summary>
    public string? SaveTo { get; init; }

    /// <summary>Назначение: обычное ожидание события или ожидание завершения дочернего процесса.</summary>
    public SubscriptionPurpose Purpose { get; init; } = SubscriptionPurpose.Event;
}

public enum SubscriptionPurpose
{
    Event,
    ChildProcess,
}

public static class EventSources
{
    public const string Manual = "manual";
    public const string Http = "http";
    public const string Process = "process";

    /// <summary>Служебные события платформы (например, завершение дочернего процесса).</summary>
    public const string Platform = "pepa.process";
}
