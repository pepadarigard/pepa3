using System.Text.Json.Nodes;

namespace Pepa.Core.Model;

/// <summary>
/// Постоянное описание логики автоматизации: граф блоков и связей плюс объявленные переменные.
/// Один и тот же формат создают конструктор, запись действий и ИИ-помощник.
/// </summary>
public sealed record ScenarioDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<VariableDefinition> Variables { get; init; } = [];
    public IReadOnlyList<NodeDefinition> Nodes { get; init; } = [];
    public IReadOnlyList<EdgeDefinition> Edges { get; init; } = [];

    /// <summary>Данные только для редактора (положение холста и т.п.). Движок их не читает.</summary>
    public JsonObject? Editor { get; init; }
}

/// <summary>Блок сценария. Тип определяет поведение, <see cref="Config"/> — его параметры.</summary>
public sealed record NodeDefinition
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string? Label { get; init; }
    public NodePosition Position { get; init; } = new(0, 0);
    public JsonObject Config { get; init; } = new();
    public NodeSettings Settings { get; init; } = new();
    public string? Note { get; init; }
}

public sealed record NodePosition(double X, double Y);

/// <summary>Общие настройки исполнения блока (для блоков с внешними действиями).</summary>
public sealed record NodeSettings
{
    /// <summary>Сколько раз повторить шаг после подтверждённой ошибки. 0 — не повторять.</summary>
    public int RetryCount { get; init; }

    /// <summary>Пауза перед повтором, секунды.</summary>
    public int RetryDelaySeconds { get; init; } = 5;

    /// <summary>Предельное время выполнения шага; null — значение по умолчанию из настроек.</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>Связь из выходного порта одного блока во вход другого.</summary>
public sealed record EdgeDefinition
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string SourcePort { get; init; } = Ports.Out;
    public string Target { get; init; } = "";
}

public enum VariableType
{
    Any,
    Text,
    Number,
    Boolean,
    List,
    Object,
}

public sealed record VariableDefinition
{
    public string Name { get; init; } = "";
    public VariableType Type { get; init; } = VariableType.Any;
    public JsonNode? Default { get; init; }
    public string Description { get; init; } = "";

    /// <summary>Значение запрашивается при запуске процесса.</summary>
    public bool IsInput { get; init; }
}

/// <summary>Стандартные имена портов.</summary>
public static class Ports
{
    public const string Out = "out";
    public const string Error = "error";
    public const string True = "true";
    public const string False = "false";
}
