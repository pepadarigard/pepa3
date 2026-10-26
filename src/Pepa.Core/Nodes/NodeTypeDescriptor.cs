using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pepa.Core.Nodes;

/// <summary>Какое влияние блок оказывает на внешний мир. Определяет правила восстановления.</summary>
public enum NodeEffect
{
    /// <summary>Чистая логика без ввода-вывода (условие, переменная). Можно безопасно выполнить повторно.</summary>
    None,

    /// <summary>Только читает внешнее состояние (прочитать файл). Повтор безопасен.</summary>
    ReadOnly,

    /// <summary>Меняет внешнее состояние (записать файл, нажать кнопку). Повтор без сверки запрещён.</summary>
    External,
}

public enum PortKind
{
    Normal,

    /// <summary>Порт для обработки ошибки. Подключать необязательно.</summary>
    Error,

    /// <summary>
    /// Необязательный обычный выход (например, «тайм-аут»). Если он не подключён, а процесс должен пойти по нему,
    /// шаг считается невыполненным, и процесс останавливается с понятной причиной.
    /// </summary>
    Optional,
}

public sealed record PortDescriptor(string Id, string Label, PortKind Kind = PortKind.Normal)
{
    public bool Required => Kind == PortKind.Normal;
}

public enum FieldKind
{
    /// <summary>Строка как есть.</summary>
    Text,

    /// <summary>Строка с подстановками <c>{{ выражение }}</c>.</summary>
    Template,

    /// <summary>Многострочный текст с подстановками.</summary>
    MultilineTemplate,

    /// <summary>Путь к файлу или папке (с подстановками).</summary>
    Path,

    /// <summary>Выражение, результат — любое значение.</summary>
    Expression,

    /// <summary>Выражение, результат — строго да/нет.</summary>
    Condition,

    Number,
    Boolean,
    Select,

    /// <summary>Имя объявленной переменной сценария.</summary>
    Variable,

    /// <summary>Время суток ЧЧ:ММ.</summary>
    Time,

    /// <summary>
    /// Условие, которое проверяется позже (при сопоставлении события): синтаксис проверяется сразу,
    /// значение не вычисляется при выполнении шага.
    /// </summary>
    MatchCondition,

    /// <summary>Идентификатор сценария (выбор из списка).</summary>
    Scenario,

    /// <summary>Длительность в секундах.</summary>
    Seconds,
}

public sealed record SelectOption(string Value, string Label);

/// <summary>Поле показывается, только если другое поле имеет одно из значений.</summary>
public sealed record FieldCondition(string Field, IReadOnlyList<string> Values);

public sealed record FieldDescriptor
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required FieldKind Kind { get; init; }
    public bool Required { get; init; }
    public JsonNode? Default { get; init; }
    public string? Help { get; init; }
    public string? Placeholder { get; init; }
    public IReadOnlyList<SelectOption>? Options { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public FieldCondition? VisibleWhen { get; init; }

    /// <summary>Откуда конструктор может предложить значения (например, «adspower-profiles» — профили AdsPower).</summary>
    public string? Lookup { get; init; }
}

/// <summary>Описание типа блока: всё, что нужно конструктору, проверке и движку, кроме самого поведения.</summary>
public sealed record NodeTypeDescriptor
{
    public required string Type { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>Имя значка (набор lucide) для интерфейса.</summary>
    public string Icon { get; init; } = "box";

    public NodeEffect Effect { get; init; } = NodeEffect.None;

    /// <summary>Умеет ли блок проверить, выполнилось ли действие (после сбоя или неизвестного результата).</summary>
    public bool SupportsVerification { get; init; }

    public bool IsStart { get; init; }
    public bool IsEnd { get; init; }
    public IReadOnlyList<PortDescriptor> Outputs { get; init; } = [];
    public IReadOnlyList<FieldDescriptor> Fields { get; init; } = [];

    /// <summary>Поле, значение которого показывается на карточке блока в схеме.</summary>
    public string? SummaryField { get; init; }

    /// <summary>Пояснение для конструктора, если блок сам занимает ресурс (см. <see cref="INodeExecutor.RequiredResource"/>).</summary>
    public string? ResourceHint { get; init; }

    /// <summary>Можно ли настраивать повторы и тайм-аут (имеет смысл только для действий с вводом-выводом).</summary>
    public bool SupportsRetry => Effect != NodeEffect.None;

    public PortDescriptor? FindOutput(string id) => Outputs.FirstOrDefault(p => p.Id == id);

    /// <summary>Порты успешного завершения (не порты ошибки).</summary>
    [JsonIgnore]
    public IEnumerable<PortDescriptor> SuccessOutputs => Outputs.Where(p => p.Kind != PortKind.Error);
}

public sealed record NodeCategory(string Id, string Title, int Order);

public static class NodeCategories
{
    public static readonly NodeCategory Logic = new("logic", "Логика", 0);
    public static readonly NodeCategory Flow = new("flow", "События и процессы", 5);
    public static readonly NodeCategory Files = new("files", "Файлы и папки", 10);
}
