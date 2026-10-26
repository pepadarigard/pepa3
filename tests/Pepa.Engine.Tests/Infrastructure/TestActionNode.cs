using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Engine.Tests.Infrastructure;

public enum ActionMode
{
    /// <summary>Выполнить и подтвердить.</summary>
    Succeed,

    /// <summary>Подтверждённая ошибка, внешнего эффекта нет.</summary>
    Fail,

    /// <summary>Внешний эффект произошёл, затем исключение (итог неизвестен).</summary>
    ThrowAfterEffect,

    /// <summary>Исключение до внешнего эффекта (итог неизвестен для движка).</summary>
    ThrowBeforeEffect,
}

/// <summary>
/// Имитация внешнего мира для тестового действия: сколько раз действие реально выполнялось,
/// что «сделано» во внешней системе, как вести себя на следующих вызовах.
/// </summary>
public sealed class ExternalWorld
{
    private readonly ConcurrentDictionary<string, int> _executions = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ActionMode>> _plans = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new();

    /// <summary>Что выполнено во внешней системе (ключ → число эффектов).</summary>
    public ConcurrentDictionary<string, int> Effects { get; } = new();

    /// <summary>Что вернёт проверка; null — по фактическому состоянию Effects.</summary>
    public ConcurrentDictionary<string, VerificationOutcome> ForcedVerification { get; } = new();

    public int Executions(string key) => _executions.GetValueOrDefault(key);

    public int EffectCount(string key) => Effects.GetValueOrDefault(key);

    /// <summary>Режимы для очередных вызовов действия с этим ключом; после исчерпания — Succeed.</summary>
    public void Plan(string key, params ActionMode[] modes)
    {
        var queue = _plans.GetOrAdd(key, _ => new ConcurrentQueue<ActionMode>());
        foreach (var mode in modes)
        {
            queue.Enqueue(mode);
        }
    }

    /// <summary>Действие с этим ключом остановится внутри выполнения до <see cref="Release"/>.</summary>
    public void Block(string key)
    {
        _gates[key] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _entered[key] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release(string key)
    {
        if (_gates.TryRemove(key, out var gate))
        {
            gate.TrySetResult();
        }
    }

    public Task EnteredAsync(string key) =>
        _entered.TryGetValue(key, out var entered) ? entered.Task : Task.CompletedTask;

    internal ActionMode NextMode(string key) =>
        _plans.TryGetValue(key, out var queue) && queue.TryDequeue(out var mode) ? mode : ActionMode.Succeed;

    internal void CountExecution(string key) => _executions.AddOrUpdate(key, 1, (_, v) => v + 1);

    internal async Task WaitGateAsync(string key, CancellationToken cancellationToken)
    {
        if (_entered.TryGetValue(key, out var entered))
        {
            entered.TrySetResult();
        }

        if (_gates.TryGetValue(key, out var gate))
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
    }
}

/// <summary>Тестовое внешнее действие «test.action» с ключом (параметр «key», поддерживает шаблоны).</summary>
public sealed class TestActionNode(ExternalWorld world) : INodeExecutor
{
    public const string TypeName = "test.action";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = "test",
        Title = "Тестовое действие",
        Description = "Действие для тестов.",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        Outputs = [new PortDescriptor(Ports.Out, "Далее"), new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error)],
        Fields =
        [
            new FieldDescriptor { Name = "key", Label = "Ключ", Kind = FieldKind.Template, Required = true },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить в", Kind = FieldKind.Variable },
            new FieldDescriptor { Name = "resource", Label = "Ресурс блока", Kind = FieldKind.Template },
            new FieldDescriptor { Name = "release", Label = "Освободить ресурс после", Kind = FieldKind.Template },
        ],
    };

    public ResourceRequirement? RequiredResource(JsonObject input) =>
        input.GetString("resource") is { Length: > 0 } key ? new ResourceRequirement(key, $"Тестовый ресурс {key}") : null;

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var key = context.RequireString("key");
        return ValueTask.FromResult<JsonObject?>(new JsonObject { ["effectsBefore"] = world.EffectCount(key) });
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var key = context.RequireString("key");
        world.CountExecution(key);
        var mode = world.NextMode(key);
        await world.WaitGateAsync(key, cancellationToken);

        switch (mode)
        {
            case ActionMode.Fail:
                return NodeResult.Fail($"Отказ внешней системы для «{key}».");
            case ActionMode.ThrowBeforeEffect:
                throw new InvalidOperationException("Сбой до действия.");
        }

        world.Effects.AddOrUpdate(key, 1, (_, v) => v + 1);
        if (mode == ActionMode.ThrowAfterEffect)
        {
            throw new IOException("Связь прервалась после отправки.");
        }

        var saveTo = context.GetString("saveTo");
        var result = string.IsNullOrEmpty(saveTo)
            ? NodeResult.Ok()
            : NodeResult.Ok(Ports.Out, new Dictionary<string, JsonNode?> { [saveTo] = key });
        return context.GetString("release") is { Length: > 0 } release ? result with { ReleaseResources = [release] } : result;
    }

    public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var key = context.RequireString("key");
        if (world.ForcedVerification.TryGetValue(key, out var forced))
        {
            return ValueTask.FromResult(new VerificationResult(forced, $"принудительно: {forced}"));
        }

        var before = context.Checkpoint?.GetNumber("effectsBefore") ?? 0;
        return ValueTask.FromResult(world.EffectCount(key) > before
            ? VerificationResult.Done("эффект найден во внешней системе")
            : VerificationResult.NotDone("эффекта во внешней системе нет"));
    }
}
