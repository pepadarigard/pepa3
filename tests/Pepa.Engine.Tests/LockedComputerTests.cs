using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Компьютер заблокирован (D-086): мышь и клавиатура «заняты Pepa», и процессы ждут их в очереди.</summary>
public sealed class LockedComputerTests
{
    [Fact]
    public async Task Process_waits_for_the_input_while_the_computer_is_locked_and_goes_on_after_unlock()
    {
        await using var h = await EngineHarness.CreateAsync();
        Assert.Null(await h.Engine.TryHoldExternallyAsync("desktop:input", "Мышь и клавиатура", ProcessEngine.LockedHolder));
        Assert.Equal("компьютер заблокирован", ProcessEngine.ExternalHolderTitle(ProcessEngine.LockedHolder));

        var id = await h.RunAsync(new ScenarioBuilder("Окна")
            .Node("start", "start")
            .Node("lock", "resource.acquire", new { key = "desktop:input", title = "Мышь и клавиатура" })
            .Node("use", TestActionNode.TypeName, new { key = "click" })
            .Node("end", "end")
            .Chain("start", "lock", "use", "end")
            .Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.World.Executions("click"));

        await h.Engine.ReleaseExternalAsync("desktop:input", ProcessEngine.LockedHolder);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("click"));
    }

    private const string Input = "desktop:input";

    /// <summary>Держит мышь и клавиатуру, пока не придёт событие «go», затем завершается (и освобождает их).</summary>
    private static ScenarioDefinition Holder() => new ScenarioBuilder("Держатель")
        .Node("start", "start")
        .Node("lock", "resource.acquire", new { key = Input, title = "Мышь и клавиатура" })
        .Node("hold", "event.wait", new { source = "manual", correlationKey = "go" })
        .Node("end", "end")
        .Edge("start", "lock").Edge("lock", "hold").Edge("hold", "end", Ports.Event).Build();

    private static ScenarioDefinition User(string key) => new ScenarioBuilder("Окна " + key)
        .Node("start", "start")
        .Node("lock", "resource.acquire", new { key = Input, title = "Мышь и клавиатура" })
        .Node("use", TestActionNode.TypeName, new { key })
        .Node("end", "end")
        .Chain("start", "lock", "use", "end")
        .Build();

    private static async Task<string?> HolderOf(EngineHarness h) =>
        (await h.Store.ListResourcesAsync()).FirstOrDefault(r => r.Key == Input)?.HolderProcessId;

    [Fact]
    public async Task Lock_while_a_process_holds_the_input_takes_it_after_that_process_and_keeps_the_queue_waiting()
    {
        await using var h = await EngineHarness.CreateAsync();
        var a = await h.RunAsync(Holder());
        await h.UntilAsync(async () => await HolderOf(h) == a, "A занял мышь и клавиатуру");

        // Win+L, пока ввод у процесса A: Pepa заберёт его, как только A отпустит.
        Assert.Equal(a, await h.Engine.ClaimExternallyAsync(Input, "Мышь и клавиатура", ProcessEngine.LockedHolder));
        Assert.True(h.Engine.HasExternalClaim(Input, ProcessEngine.LockedHolder));

        var b = await h.RunAsync(User("b"));
        await h.WaitForStatusAsync(b, ProcessStatus.Waiting);

        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x", CorrelationKey = "go" });
        await h.WaitForStatusAsync(a, ProcessStatus.Completed);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessEngine.LockedHolder, await HolderOf(h));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(b)).Status);
        Assert.Equal(0, h.World.Executions("b"));

        // Новый процесс во время блокировки тоже ждёт, а не берёт ввод.
        var c = await h.RunAsync(User("c"));
        await h.WaitForStatusAsync(c, ProcessStatus.Waiting);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.World.Executions("c"));

        await h.Engine.ReleaseExternalAsync(Input, ProcessEngine.LockedHolder);
        Assert.False(h.Engine.HasExternalClaim(Input, ProcessEngine.LockedHolder));
        await h.WaitForStatusAsync(b, ProcessStatus.Completed);
        await h.WaitForStatusAsync(c, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("b"));
        Assert.Equal(1, h.World.Executions("c"));
    }

    [Fact]
    public async Task Unlock_before_the_holder_finishes_drops_the_claim_and_the_queue_gets_the_input()
    {
        await using var h = await EngineHarness.CreateAsync();
        var a = await h.RunAsync(Holder());
        await h.UntilAsync(async () => await HolderOf(h) == a, "A занял мышь и клавиатуру");
        Assert.Equal(a, await h.Engine.ClaimExternallyAsync(Input, "Мышь и клавиатура", ProcessEngine.LockedHolder));
        var b = await h.RunAsync(User("b"));
        await h.WaitForStatusAsync(b, ProcessStatus.Waiting);

        // Разблокировали, пока ввод ещё у A: заявка снята, после A ввод идёт в очередь.
        await h.Engine.ReleaseExternalAsync(Input, ProcessEngine.LockedHolder);
        Assert.False(h.Engine.HasExternalClaim(Input, ProcessEngine.LockedHolder));
        Assert.Equal(a, await HolderOf(h));

        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x", CorrelationKey = "go" });
        await h.WaitForStatusAsync(b, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("b"));
    }

    [Fact]
    public async Task User_releasing_the_locked_hold_drops_the_claim()
    {
        await using var h = await EngineHarness.CreateAsync();
        Assert.Null(await h.Engine.ClaimExternallyAsync(Input, "Мышь и клавиатура", ProcessEngine.LockedHolder));
        var b = await h.RunAsync(User("b"));
        await h.WaitForStatusAsync(b, ProcessStatus.Waiting);

        // Событие разблокировки потерялось — пользователь сам освобождает ресурс: он не должен тут же вернуться к Pepa.
        await h.Engine.ForceReleaseResourceAsync(Input);
        Assert.False(h.Engine.HasExternalClaim(Input, ProcessEngine.LockedHolder));
        await h.WaitForStatusAsync(b, ProcessStatus.Completed);
    }

    [Fact]
    public async Task After_a_restart_the_locked_hold_is_free_and_not_counted_as_a_finished_process_resource()
    {
        await using var h = await EngineHarness.CreateAsync();
        Assert.Null(await h.Engine.TryHoldExternallyAsync(Input, "Мышь и клавиатура", ProcessEngine.LockedHolder));
        await h.CrashAndRestartAsync();

        Assert.Null(await HolderOf(h));
        Assert.Equal(0, h.LastRecovery!.ReleasedResources);
    }
}
