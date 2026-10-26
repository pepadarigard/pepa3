using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Storage.Sqlite;

namespace Pepa.Engine.Tests.Infrastructure;

/// <summary>
/// Движок над настоящим SQLite во временной папке, с управляемым временем и тестовым внешним миром.
/// «Перезапуск» создаёт новый движок над тем же файлом базы — как при закрытии и открытии приложения.
/// </summary>
public sealed class EngineHarness : IAsyncDisposable
{
    private readonly string _directory;
    private readonly List<ProcessEngine> _engines = [];

    private readonly Func<NodeRegistry, NodeRegistry>? _extraNodes;

    private EngineHarness(string directory, EngineOptions options, Func<NodeRegistry, NodeRegistry>? extraNodes)
    {
        _directory = directory;
        _extraNodes = extraNodes;
        Options = options;
        Store = new SqliteStore(Path.Combine(directory, "pepa.db"));
        Faults = new FaultInjectingStore(Store);
    }

    public static readonly DateTimeOffset StartTime = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Time { get; } = new(StartTime);
    public ExternalWorld World { get; } = new();
    public SqliteStore Store { get; }
    public FaultInjectingStore Faults { get; }
    public EngineOptions Options { get; }
    public ProcessEngine Engine { get; private set; } = null!;
    public ScenarioService Scenarios { get; private set; } = null!;

    /// <summary>Папка теста: база и (для файловых тестов) рабочая папка.</summary>
    public string Directory => _directory;

    public static async Task<EngineHarness> CreateAsync(EngineOptions? options = null, bool withFaults = false,
        Func<NodeRegistry, NodeRegistry>? extraNodes = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pepa-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var harness = new EngineHarness(directory, options ?? new EngineOptions
        {
            MaxParallelProcesses = 4,
            ShutdownGracePeriod = TimeSpan.FromSeconds(2),
        }, extraNodes);
        await harness.Store.InitializeAsync();
        await harness.StartEngineAsync(withFaults);
        return harness;
    }

    public NodeRegistry CreateRegistry()
    {
        var registry = new NodeRegistry().RegisterLogicNodes().Register(new TestActionNode(World));
        return _extraNodes?.Invoke(registry) ?? registry;
    }

    private async Task StartEngineAsync(bool withFaults)
    {
        IPepaStore store = withFaults ? Faults : Store;
        var registry = CreateRegistry();
        var expressions = new JintExpressionEvaluator();
        Engine = new ProcessEngine(store, registry, expressions, Options, Time);
        Scenarios = new ScenarioService(store, Engine.Validator, Time);
        _engines.Add(Engine);
        LastRecovery = await Engine.StartAsync();
    }

    public RecoveryReport? LastRecovery { get; private set; }

    /// <summary>Закрыть приложение (мягко) и открыть снова над тем же файлом базы.</summary>
    public async Task RestartAsync(bool withFaults = false)
    {
        await Engine.StopAsync();
        await StartEngineAsync(withFaults);
    }

    /// <summary>Аварийно бросить текущий движок (без ожидания) и открыть новый над тем же файлом базы.</summary>
    public async Task CrashAndRestartAsync()
    {
        // Старый движок не останавливаем «вежливо»: его записи уже падают из-за внедрённого сбоя,
        // а исполнители выключаются отменой. Новый движок работает с исправным хранилищем.
        _ = Engine.StopAsync();
        await StartEngineAsync(withFaults: false);
    }

    public async Task<string> CreateScenarioAsync(ScenarioDefinition definition) =>
        (await Scenarios.CreateAsync(definition)).Id;

    public async Task<string> RunAsync(ScenarioDefinition definition, JsonObject? inputs = null)
    {
        var id = await CreateScenarioAsync(definition);
        return await Engine.StartProcessAsync(id, inputs);
    }

    public Task<ProcessRecord> ProcessAsync(string id) => Store.GetProcessAsync(id)!;

    public async Task<IReadOnlyList<StepRecord>> StepsAsync(string id) => await Store.GetStepsAsync(id);

    /// <summary>
    /// Ждёт условия, продвигая виртуальное время на <paramref name="advance"/> за такт (если задано).
    /// </summary>
    public async Task<ProcessRecord> WaitForAsync(string processId, Func<ProcessRecord, bool> condition,
        TimeSpan? advance = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var process = await Store.GetProcessAsync(processId);
            if (process is not null && condition(process))
            {
                return process;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Не дождались условия. Состояние: {process?.Status} / {process?.Attention}: {process?.StatusMessage}");
            }

            if (advance is { } step)
            {
                Time.Advance(step);
            }

            await Task.Delay(10);
        }
    }

    public Task<ProcessRecord> WaitForStatusAsync(string processId, ProcessStatus status, TimeSpan? advance = null) =>
        WaitForAsync(processId, p => p.Status == status, advance);

    /// <summary>Дать движку поработать некоторое реальное время, продвигая виртуальное.</summary>
    public async Task IdleAsync(TimeSpan virtualTime, int ticks = 20)
    {
        for (var i = 0; i < ticks; i++)
        {
            Time.Advance(virtualTime / ticks);
            await Task.Delay(15);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines)
        {
            try
            {
                await engine.StopAsync();
            }
            catch
            {
                // Тестовая очистка.
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Файл может быть ещё занят на Windows — не мешаем тестам.
        }
    }
}
