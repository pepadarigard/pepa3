using Pepa.Core;
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
}
