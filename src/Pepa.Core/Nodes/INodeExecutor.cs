using System.Text.Json.Nodes;
using Pepa.Core.Model;

namespace Pepa.Core.Nodes;

/// <summary>
/// Поведение типа блока. Реализации не хранят состояние процесса: всё нужное приходит в контексте,
/// а результат возвращается движку, который сам решает, что сохранить и куда идти дальше.
/// </summary>
public interface INodeExecutor
{
    NodeTypeDescriptor Descriptor { get; }

    /// <summary>Дополнительная проверка параметров сверх общих правил (обязательность, типы).</summary>
    IEnumerable<NodeConfigIssue> Validate(NodeDefinition node) => [];

    /// <summary>
    /// Снимок внешнего состояния до действия (только чтение). Сохраняется вместе с отметкой «выполняется»
    /// и передаётся в <see cref="VerifyAsync"/>.
    /// </summary>
    ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<JsonObject?>(null);

    ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Проверить, выполнилось ли действие, не повторяя его. Вызывается, когда результат неизвестен.
    /// </summary>
    ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(VerificationResult.NotSupported());
}

public sealed record NodeConfigIssue(string Message, string? Field = null);

/// <summary>Всё, что нужно исполнителю для одного шага.</summary>
public sealed record NodeExecutionContext
{
    public required string ProcessId { get; init; }
    public required NodeDefinition Node { get; init; }

    /// <summary>Параметры блока с уже подставленными значениями выражений и шаблонов.</summary>
    public required JsonObject Input { get; init; }

    /// <summary>Снимок, полученный в <see cref="INodeExecutor.PrepareAsync"/>, если был.</summary>
    public JsonObject? Checkpoint { get; init; }

    /// <summary>Копия переменных процесса (изменения через результат шага, а не напрямую).</summary>
    public required JsonObject Variables { get; init; }

    public required int Attempt { get; init; }
    public required TimeProvider Time { get; init; }

    public string? GetString(string name) => Input.GetString(name);

    public string RequireString(string name) =>
        Input.GetString(name) is { Length: > 0 } value
            ? value
            : throw new NodeInputException($"Не заполнен параметр «{name}».");

    public bool GetBoolean(string name, bool fallback = false) => Input.GetBoolean(name) ?? fallback;

    public double? GetNumber(string name) => Input.GetNumber(name);
}

/// <summary>Параметры шага некорректны — действие заведомо не выполнялось.</summary>
public sealed class NodeInputException(string message) : Exception(message);

/// <summary>Итог выполнения шага.</summary>
public abstract record NodeResult
{
    /// <summary>Действие подтверждено выполненным; процесс идёт по порту <paramref name="Port"/>.</summary>
    public sealed record Success(
        string Port,
        IReadOnlyDictionary<string, JsonNode?> VariableUpdates,
        JsonObject? Output,
        string? Message,
        MessageLevel Level = MessageLevel.Info) : NodeResult;

    /// <summary>Действие подтверждено НЕ выполненным. Повтор безопасен.</summary>
    public sealed record Failure(string Error, bool Retryable = true) : NodeResult;

    /// <summary>Неизвестно, выполнилось ли действие. Автоматически не повторяется.</summary>
    public sealed record Unknown(string Reason) : NodeResult;

    /// <summary>Ждать до указанного момента (не занимая поток), затем идти по порту «out».</summary>
    public sealed record Wait(DateTimeOffset Until, string? Message) : NodeResult;

    /// <summary>Завершить процесс.</summary>
    public sealed record Finish(bool Succeeded, string? Message) : NodeResult;

    public static Success Ok(string port = Ports.Out, string? message = null) =>
        new(port, EmptyUpdates, null, message);

    public static Success Ok(string port, IReadOnlyDictionary<string, JsonNode?> updates, JsonObject? output = null,
        string? message = null) => new(port, updates, output, message);

    public static Failure Fail(string error, bool retryable = true) => new(error, retryable);

    private static readonly IReadOnlyDictionary<string, JsonNode?> EmptyUpdates =
        new Dictionary<string, JsonNode?>();
}

/// <summary>Важность сообщения шага для журнала процесса.</summary>
public enum MessageLevel
{
    Info,
    Warning,
}

public enum VerificationOutcome
{
    /// <summary>Подтверждено: действие выполнено.</summary>
    Done,

    /// <summary>Подтверждено: действие не выполнено, его можно выполнить снова.</summary>
    NotDone,

    /// <summary>Однозначно определить нельзя — решает пользователь.</summary>
    Inconclusive,

    /// <summary>Блок не умеет проверять результат.</summary>
    NotSupported,
}

public sealed record VerificationResult(
    VerificationOutcome Outcome,
    string Message,
    string Port = Ports.Out,
    IReadOnlyDictionary<string, JsonNode?>? VariableUpdates = null,
    JsonObject? Output = null)
{
    public static VerificationResult Done(string message, JsonObject? output = null) =>
        new(VerificationOutcome.Done, message, Output: output);

    public static VerificationResult NotDone(string message) => new(VerificationOutcome.NotDone, message);

    public static VerificationResult Inconclusive(string message) => new(VerificationOutcome.Inconclusive, message);

    public static VerificationResult NotSupported() =>
        new(VerificationOutcome.NotSupported, "Этот блок не умеет проверять результат.");
}
