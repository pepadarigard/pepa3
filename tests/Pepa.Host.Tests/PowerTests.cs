using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Pepa.Host.Power;

namespace Pepa.Host.Tests;

/// <summary>Запрет сна компьютера, пока процессы выполняются или ждут (D-080).</summary>
public sealed class PowerTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Keeps_the_computer_awake_only_while_there_is_work_and_the_setting_is_on()
    {
        var work = false;
        var enabled = true;
        var applied = new ConcurrentQueue<bool>();
        var keeper = new PowerKeeper(_ => Task.FromResult(Volatile.Read(ref work)), () => Volatile.Read(ref enabled), NullLogger.Instance,
            keep => { applied.Enqueue(keep); return true; }, debounce: TimeSpan.Zero);
        Assert.True(keeper.Supported);

        Volatile.Write(ref work, true);
        keeper.Refresh();
        await WaitUntilAsync(() => keeper.Active, "запрет сна не поставлен");

        Volatile.Write(ref enabled, false);
        keeper.Refresh();
        await WaitUntilAsync(() => !keeper.Active, "запрет сна не снят после выключения настройки");

        Volatile.Write(ref enabled, true);
        keeper.Refresh();
        await WaitUntilAsync(() => keeper.Active, "запрет сна не поставлен снова");

        // Закрытие Pepa снимает запрет.
        keeper.Dispose();
        Assert.False(keeper.Active);
        Assert.Equal([true, false, true, false], applied);
    }

    [Fact]
    public async Task Refusal_of_windows_is_not_shown_as_kept_awake()
    {
        var attempts = 0;
        using var keeper = new PowerKeeper(_ => Task.FromResult(true), () => true, NullLogger.Instance,
            _ => { Interlocked.Increment(ref attempts); return false; }, debounce: TimeSpan.Zero);
        keeper.Refresh();
        await WaitUntilAsync(() => Volatile.Read(ref attempts) > 0, "запрет сна не пробовали поставить");
        Assert.False(keeper.Active);
    }

    [Fact]
    public async Task Overview_shows_that_the_computer_is_kept_awake_while_a_process_waits()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Ждёт ответа",
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "wait", type = "event.wait", config = new { source = "manual", eventType = "reply" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "wait" },
                new { id = "b", source = "wait", sourcePort = "event", target = "end" },
            },
        });
        var power = (await f.GetAsync("/api/overview"))["power"]!;
        Assert.True(power["supported"]!.GetValue<bool>());
        Assert.True(power["enabled"]!.GetValue<bool>());
        Assert.False(power["keepingAwake"]!.GetValue<bool>());

        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario!["id"]!.GetValue<string>()}/run", new { });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");
        await WaitForPowerAsync(f, true);

        // Настройка выключена — компьютер может засыпать, даже пока процесс ждёт.
        var settings = await f.GetAsync("/api/settings");
        settings["keepAwake"] = false;
        var (status, saved) = await f.SendAsync(HttpMethod.Put, "/api/settings", settings);
        Assert.Equal(200, status);
        Assert.False(saved!["keepAwake"]!.GetValue<bool>());
        await WaitForPowerAsync(f, false);

        settings["keepAwake"] = true;
        await f.SendAsync(HttpMethod.Put, "/api/settings", settings);
        await WaitForPowerAsync(f, true);

        await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/cancel");
        await WaitForPowerAsync(f, false);
    }

    private static async Task WaitForPowerAsync(HostFixture f, bool keepingAwake)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while ((await f.GetAsync("/api/overview"))["power"]!["keepingAwake"]!.GetValue<bool>() != keepingAwake)
        {
            Assert.True(DateTime.UtcNow < deadline, $"запрет сна не стал {keepingAwake}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
