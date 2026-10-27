using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Очистка истории: удаляются только старые завершённые процессы, у которых и связанные процессы завершены.</summary>
public sealed class HistoryCleanupTests
{
    [Fact]
    public async Task Only_old_finished_processes_are_removed_and_never_a_child_of_a_running_parent()
    {
        await using var h = await EngineHarness.CreateAsync();
        var simple = await h.RunAsync(new ScenarioBuilder("Простой").Node("start", "start").Node("end", "end").Chain("start", "end").Build());
        await h.WaitForStatusAsync(simple, ProcessStatus.Completed);

        var waiting = await h.RunAsync(new ScenarioBuilder("Ждёт")
            .Node("start", "start").Node("w", "wait", new { mode = "duration", amount = 1, unit = "days" }).Node("end", "end")
            .Chain("start", "w", "end").Build());
        await h.WaitForStatusAsync(waiting, ProcessStatus.Waiting);

        var childScenario = await h.CreateScenarioAsync(new ScenarioBuilder("Ребёнок").Node("start", "start").Node("end", "end").Chain("start", "end").Build());
        var parent = await h.RunAsync(new ScenarioBuilder("Родитель")
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = childScenario })
            .Node("w", "wait", new { mode = "duration", amount = 10, unit = "minutes" })
            .Node("end", "end")
            .Node("failed", "end", new { outcome = "failure", message = "нет" })
            .Edge("start", "call").Edge("call", "w", Ports.Done).Edge("call", "failed", Ports.Error).Edge("call", "failed", Ports.Timeout)
            .Edge("w", "end")
            .Build());
        await h.WaitForAsync(parent, p => p.StepCount >= 2);
        var child = (await h.Store.ListProcessesAsync(new ProcessQuery { Limit = 100 })).Single(p => p.ParentProcessId == parent);
        await h.WaitForStatusAsync(child.Id, ProcessStatus.Completed);
        // «Родитель» получил итог и ждёт уже своё (10 минут).
        await h.WaitForAsync(parent, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        var cutoff = EngineHarness.StartTime.AddDays(1);
        Assert.Equal(1, await h.Store.DeleteFinishedProcessesAsync(cutoff, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await h.Store.GetProcessAsync(simple));
        Assert.Empty(await h.Store.GetStepsAsync(simple));
        Assert.Empty(await h.Store.GetJournalAsync(simple));
        // Ждущий процесс и завершённый «ребёнок» ещё работающего «родителя» остаются.
        Assert.NotNull(await h.Store.GetProcessAsync(waiting));
        Assert.NotNull(await h.Store.GetProcessAsync(child.Id));
        Assert.NotNull(await h.Store.GetProcessAsync(parent));

        // «Родитель» закончил — теперь оба уходят из истории, по маленьким пачкам тоже.
        await h.WaitForStatusAsync(parent, ProcessStatus.Completed, advance: TimeSpan.FromMinutes(1));
        Assert.Equal(2, await h.Store.DeleteFinishedProcessesAsync(cutoff, batch: 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await h.Store.GetProcessAsync(parent));
        Assert.NotNull(await h.Store.GetProcessAsync(waiting));

        // Свежие (позже срока) не удаляются.
        Assert.Equal(0, await h.Store.DeleteFinishedProcessesAsync(EngineHarness.StartTime, cancellationToken: TestContext.Current.CancellationToken));
    }
}
