using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Ресурсы: один процесс на ресурс, очередь по порядку, освобождение, взаимная блокировка, восстановление.</summary>
public sealed class ResourceTests
{
    private const string Action = TestActionNode.TypeName;

    /// <summary>Занять ресурс → действие «use-{name}» → освободить → конец.</summary>
    private static ScenarioDefinition UseResource(bool release = true, int? timeoutSeconds = null, int waitSeconds = 0)
    {
        var builder = new ScenarioBuilder("Работа с профилем")
            .Variable("name", VariableType.Text, "", isInput: true)
            .Node("start", "start")
            .Node("lock", "resource.acquire", new { key = "profile:main", title = "Основной профиль", timeoutSeconds })
            .Node("use", Action, new { key = "use-{{ vars.name }}" })
            .Node("end", "end")
            .Edge("start", "lock")
            .Edge("lock", "use");
        var last = "use";
        if (waitSeconds > 0)
        {
            builder.Node("hold", "wait", new { mode = "duration", amount = waitSeconds, unit = "seconds" }).Edge(last, "hold");
            last = "hold";
        }

        if (release)
        {
            builder.Node("free", "resource.release", new { key = "profile:main" }).Edge(last, "free");
            last = "free";
        }

        builder.Edge(last, "end");
        if (timeoutSeconds is not null)
        {
            builder.Node("busy", "end", new { outcome = "failure", message = "Профиль занят" }).Edge("lock", "busy", Ports.Timeout);
        }

        return builder.Build();
    }

    private static async Task<string> StartAsync(EngineHarness h, string scenarioId, string name) =>
        await h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["name"] = name });

    private static async Task<ResourceRecord?> ResourceAsync(EngineHarness h) =>
        (await h.Store.ListResourcesAsync()).SingleOrDefault(r => r.Key == "profile:main");

    [Fact]
    public async Task Second_process_waits_until_resource_is_released()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(UseResource());
        h.World.Block("use-first");

        var first = await StartAsync(h, scenario, "first");
        await h.World.EnteredAsync("use-first");
        var second = await StartAsync(h, scenario, "second");
        await h.WaitForStatusAsync(second, ProcessStatus.Waiting);

        Assert.Equal(first, (await ResourceAsync(h))!.HolderProcessId);
        Assert.Equal(0, h.World.Executions("use-second"));
        Assert.Single(await h.Store.ListResourceWaitersAsync());

        h.World.Release("use-first");
        await h.WaitForStatusAsync(first, ProcessStatus.Completed);
        await h.WaitForStatusAsync(second, ProcessStatus.Completed);

        Assert.Equal(1, h.World.Executions("use-second"));
        Assert.Null((await ResourceAsync(h))!.HolderProcessId);
        Assert.Empty(await h.Store.ListResourceWaitersAsync());
    }

    [Fact]
    public async Task Queue_is_served_in_order_of_requests()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(UseResource());
        h.World.Block("use-a");
        var a = await StartAsync(h, scenario, "a");
        await h.World.EnteredAsync("use-a");

        h.World.Block("use-b");
        var b = await StartAsync(h, scenario, "b");
        await h.WaitForStatusAsync(b, ProcessStatus.Waiting);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        var c = await StartAsync(h, scenario, "c");
        await h.WaitForStatusAsync(c, ProcessStatus.Waiting);

        h.World.Release("use-a");
        await h.WaitForStatusAsync(a, ProcessStatus.Completed);
        await h.World.EnteredAsync("use-b");
        Assert.Equal(b, (await ResourceAsync(h))!.HolderProcessId);
        Assert.Equal(0, h.World.Executions("use-c"));

        h.World.Release("use-b");
        await h.WaitForStatusAsync(c, ProcessStatus.Completed);
    }

    [Fact]
    public async Task Waiting_for_resource_does_not_block_other_processes()
    {
        await using var h = await EngineHarness.CreateAsync(new EngineOptions { MaxParallelProcesses = 1 });
        var scenario = await h.CreateScenarioAsync(UseResource(waitSeconds: 3600));
        var holder = await StartAsync(h, scenario, "holder");
        await h.WaitForStatusAsync(holder, ProcessStatus.Waiting);
        var queued = await StartAsync(h, scenario, "queued");
        await h.WaitForAsync(queued, p => p.Status == ProcessStatus.Waiting);

        var other = await h.RunAsync(new ScenarioBuilder("Другое").Node("start", "start").Node("a", Action, new { key = "other" })
            .Node("end", "end").Chain("start", "a", "end").Build());
        await h.WaitForStatusAsync(other, ProcessStatus.Completed);
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(queued)).Status);
    }

    [Fact]
    public async Task Resource_is_released_automatically_when_process_ends()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(UseResource(release: false));
        h.World.Block("use-first");
        var first = await StartAsync(h, scenario, "first");
        await h.World.EnteredAsync("use-first");
        var second = await StartAsync(h, scenario, "second");
        await h.WaitForStatusAsync(second, ProcessStatus.Waiting);

        h.World.Release("use-first");
        await h.WaitForStatusAsync(first, ProcessStatus.Completed);
        await h.WaitForStatusAsync(second, ProcessStatus.Completed);
        Assert.Null((await ResourceAsync(h))!.HolderProcessId);
    }

    [Fact]
    public async Task Cancelling_holder_passes_resource_to_next_in_queue()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(UseResource(waitSeconds: 3600));
        var first = await StartAsync(h, scenario, "first");
        await h.WaitForAsync(first, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);
        var second = await StartAsync(h, scenario, "second");
        await h.WaitForStatusAsync(second, ProcessStatus.Waiting);

        await h.Engine.CancelAsync(first);
        await h.WaitForAsync(second, _ => h.World.Executions("use-second") == 1);
        Assert.Equal(second, (await ResourceAsync(h))!.HolderProcessId);
    }

    [Fact]
    public async Task Resource_wait_timeout_follows_timeout_branch()
    {
        await using var h = await EngineHarness.CreateAsync();
        var holderScenario = await h.CreateScenarioAsync(UseResource(waitSeconds: 3600));
        var impatient = await h.CreateScenarioAsync(UseResource(timeoutSeconds: 60));
        var holder = await StartAsync(h, holderScenario, "holder");
        await h.WaitForAsync(holder, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        var waiting = await StartAsync(h, impatient, "impatient");
        var failed = await h.WaitForStatusAsync(waiting, ProcessStatus.Failed, advance: TimeSpan.FromSeconds(5));
        Assert.Equal("Профиль занят", failed.StatusMessage);
        Assert.Equal(0, h.World.Executions("use-impatient"));
        Assert.Empty(await h.Store.ListResourceWaitersAsync());
        Assert.Equal(holder, (await ResourceAsync(h))!.HolderProcessId);
    }

    [Fact]
    public async Task Paused_waiter_keeps_its_place_but_is_skipped_while_paused()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(UseResource());
        h.World.Block("use-a");
        var a = await StartAsync(h, scenario, "a");
        await h.World.EnteredAsync("use-a");
        var b = await StartAsync(h, scenario, "b");
        await h.WaitForStatusAsync(b, ProcessStatus.Waiting);
        var c = await StartAsync(h, scenario, "c");
        await h.WaitForStatusAsync(c, ProcessStatus.Waiting);
        await h.Engine.PauseAsync(b);

        h.World.Release("use-a");
        await h.WaitForStatusAsync(c, ProcessStatus.Completed);
        Assert.Equal(0, h.World.Executions("use-b"));
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(b)).Status);

        await h.Engine.ResumeAsync(b, ResumeAction.Continue);
        await h.WaitForStatusAsync(b, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("use-b"));
    }

    [Fact]
    public async Task Deadlock_is_detected_and_stops_with_explanation()
    {
        await using var h = await EngineHarness.CreateAsync();
        ScenarioDefinition TwoLocks(string firstKey, string secondKey, int delay) => new ScenarioBuilder($"{firstKey}→{secondKey}")
            .Node("start", "start")
            .Node("l1", "resource.acquire", new { key = firstKey })
            .Node("w", "wait", new { mode = "duration", amount = delay, unit = "seconds" })
            .Node("l2", "resource.acquire", new { key = secondKey })
            .Node("a", Action, new { key = $"both-{firstKey}" })
            .Node("end", "end")
            .Chain("start", "l1", "w", "l2", "a", "end")
            .Build();

        var p1 = await h.RunAsync(TwoLocks("A", "B", 10));
        var p2 = await h.RunAsync(TwoLocks("B", "A", 20));
        await h.WaitForAsync(p2, p => p.Status == ProcessStatus.NeedsAttention, advance: TimeSpan.FromSeconds(1));

        var stuck = await h.ProcessAsync(p2);
        Assert.Equal(AttentionKind.StepFailed, stuck.Attention);
        Assert.Contains("Взаимная блокировка", stuck.StatusMessage);
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(p1)).Status);

        // Решение: остановить второй — первый получает ресурс и завершается.
        await h.Engine.CancelAsync(p2);
        await h.WaitForStatusAsync(p1, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("both-A"));
        Assert.Equal(0, h.World.Executions("both-B"));
    }

    [Fact]
    public async Task After_restart_paused_holder_keeps_resource_and_stale_locks_are_released()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(UseResource(waitSeconds: 3600));
        var holder = await StartAsync(h, scenario, "holder");
        await h.WaitForAsync(holder, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        // Испорченное состояние: ресурс числится за давно завершённым процессом.
        var finished = await h.RunAsync(new ScenarioBuilder("Пусто").Node("start", "start").Node("end", "end").Chain("start", "end").Build());
        await h.WaitForStatusAsync(finished, ProcessStatus.Completed);
        await using (var write = await h.Store.BeginWriteAsync())
        {
            await write.UpsertResourceAsync(new ResourceRecord
            {
                Key = "stale", Title = "Старый", HolderProcessId = finished, AcquiredAt = h.Time.GetUtcNow(), UpdatedAt = h.Time.GetUtcNow(),
            });
            await write.CommitAsync();
        }

        await h.RestartAsync();
        Assert.Equal(1, h.LastRecovery!.ReleasedResources);
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(holder)).Status);
        Assert.Equal(holder, (await ResourceAsync(h))!.HolderProcessId);
        Assert.Null((await h.Store.ListResourcesAsync()).Single(r => r.Key == "stale").HolderProcessId);

        // Пока держатель на паузе, новый процесс ждёт; ручное освобождение передаёт ресурс ему.
        var next = await StartAsync(h, scenario, "next");
        await h.WaitForStatusAsync(next, ProcessStatus.Waiting);
        await h.Engine.ForceReleaseResourceAsync("profile:main");
        await h.WaitForAsync(next, _ => h.World.Executions("use-next") == 1);
        Assert.Equal(next, (await ResourceAsync(h))!.HolderProcessId);
    }

    [Fact]
    public async Task Releasing_resource_not_held_is_harmless()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Освободить чужое")
            .Node("start", "start").Node("free", "resource.release", new { key = "nobody" }).Node("end", "end")
            .Chain("start", "free", "end").Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Message.Contains("освобождать нечего"));
    }
}
