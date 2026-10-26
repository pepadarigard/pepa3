using Pepa.Core.Model;

namespace Pepa.Assistant;

/// <summary>Что составить: описание словами и переменные, которые в сценарии уже есть.</summary>
public sealed record AssistantRequest
{
    public required string Description { get; init; }
    public IReadOnlyList<VariableDefinition> ExistingVariables { get; init; } = [];

    /// <summary>
    /// Сценарий, который нужно изменить по описанию (правка). null — составить новые блоки. При правке ответ — полный
    /// изменённый сценарий: неизменённые блоки сохраняют id и место на схеме.
    /// </summary>
    public ScenarioDefinition? Current { get; init; }
}

/// <summary>
/// Черновик от помощника: блоки, связи между ними и новые переменные — для вставки в схему (как вставка скопированных
/// блоков). Ничего не запускается: пользователь смотрит, правит и сохраняет сам.
/// </summary>
public sealed record AssistantDraft
{
    /// <summary>Что делает черновик — коротко, для пользователя.</summary>
    public required string Summary { get; init; }

    /// <summary>Что помощник не знал и подставил заглушкой — спросить пользователя (например, «какой чат?»).</summary>
    public IReadOnlyList<string> Questions { get; init; } = [];

    /// <summary>Новые переменные (тех, что уже есть в сценарии, здесь нет).</summary>
    public IReadOnlyList<VariableDefinition> Variables { get; init; } = [];

    public IReadOnlyList<NodeDefinition> Nodes { get; init; } = [];
    public IReadOnlyList<EdgeDefinition> Edges { get; init; } = [];

    /// <summary>Первый блок черновика — к нему подключается «Начало» (или блок, после которого вставлено).</summary>
    public string? EntryNodeId { get; init; }

    /// <summary>
    /// Ошибки проверки, которые помощник не исправил за отведённые попытки. Черновик всё равно можно вставить и
    /// поправить вручную — проверка конструктора покажет те же ошибки.
    /// </summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Сколько запросов к модели понадобилось (первый + исправления).</summary>
    public int Requests { get; init; }

    public AssistantUsage Usage { get; init; } = new(0, 0, 0);
}

/// <summary>Объяснение сценария простыми словами: что он делает, по шагам, и что стоит проверить.</summary>
public sealed record AssistantExplanation
{
    public required string Summary { get; init; }
    public IReadOnlyList<ExplainedBlock> Steps { get; init; } = [];
    public IReadOnlyList<string> Cautions { get; init; } = [];
    public AssistantUsage Usage { get; init; } = new(0, 0, 0);
}

/// <param name="BlockId">Блок сценария (null — модель назвала блок, которого в сценарии нет).</param>
public sealed record ExplainedBlock(string? BlockId, string Text);

/// <summary>Сколько токенов ушло (для понимания стоимости).</summary>
public sealed record AssistantUsage(long InputTokens, long OutputTokens, long CacheReadTokens);

public enum AssistantErrorKind
{
    /// <summary>Нет ключа или он не подошёл.</summary>
    Key,

    /// <summary>Нет связи, сервис перегружен, лимит запросов.</summary>
    Unavailable,

    /// <summary>Модель отказалась отвечать или ответ не получился.</summary>
    Answer,

    /// <summary>Описание пустое или слишком длинное.</summary>
    Input,
}

/// <summary>Помощник не смог составить черновик — с понятной причиной.</summary>
public sealed class AssistantException(AssistantErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public AssistantErrorKind Kind { get; } = kind;
}

// ───────────── Ответ модели (строго по схеме) ─────────────

internal sealed record DraftJson
{
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Questions { get; init; } = [];
    public IReadOnlyList<DraftVariable> Variables { get; init; } = [];
    public IReadOnlyList<DraftBlock> Blocks { get; init; } = [];
    public IReadOnlyList<DraftLink> Links { get; init; } = [];
}

internal sealed record DraftVariable(string Name, string Type, string Description, bool IsInput, string Default);

internal sealed record DraftBlock(string Id, string Type, string Label, string Note, IReadOnlyList<DraftSetting> Settings);

internal sealed record DraftSetting(string Field, string Value);

internal sealed record DraftLink(string From, string Port, string To);

internal sealed record ExplanationJson
{
    public string Summary { get; init; } = "";
    public IReadOnlyList<ExplanationStep> Steps { get; init; } = [];
    public IReadOnlyList<string> Cautions { get; init; } = [];
}

internal sealed record ExplanationStep(string Block, string Text);
