using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>«Несколько запусков» (D-088): по процессу на набор входных данных, все или ни одного, без повторного запуска.</summary>
public sealed class BatchRunTests
{
    private static ScenarioDefinition PerProfile() => new ScenarioBuilder("По профилю")
        .Variable("profile", VariableType.Text, "", isInput: true)
        .Variable("count", VariableType.Number, 1, isInput: true)
        .Node("start", "start")
        .Node("act", TestActionNode.TypeName, new { key = "act-{{ vars.profile }}" })
        .Node("end", "end", new { message = "{{ vars.profile }}" })
        .Chain("start", "act", "end")
        .Build();

    [Fact]
    public async Task Each_set_of_inputs_is_its_own_process()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(PerProfile());
        var ids = await h.Engine.StartProcessesAsync(scenario,
            [new JsonObject { ["profile"] = "k1" }, new JsonObject { ["profile"] = "k2" }, new JsonObject { ["profile"] = "k3" }], "b1");

        Assert.Equal(3, ids.Distinct().Count());
        foreach (var (id, profile) in ids.Zip(["k1", "k2", "k3"]))
        {
            Assert.Equal(profile, (await h.WaitForStatusAsync(id, ProcessStatus.Completed)).StatusMessage);
            Assert.Equal(1, h.World.Executions($"act-{profile}"));
        }

        var journal = await h.Store.GetJournalAsync(ids[1]);
        Assert.Contains(journal, j => j.Message.StartsWith("Процесс запущен (запуск 2 из 3)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_wrong_set_starts_none_and_the_same_batch_sent_again_starts_nothing_new()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(PerProfile());
        var refused = await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.StartProcessesAsync(scenario,
            [new JsonObject { ["profile"] = "k1" }, new JsonObject { ["profile"] = "k2", ["count"] = "много" }], "b2"));
        Assert.StartsWith("Запуск 2 из 2:", refused.Message);
        Assert.Contains("Ни один процесс не запущен", refused.Message);
        Assert.Empty(await h.Store.ListProcessesAsync(new ProcessQuery()));

        // Та же отправка ещё раз (двойное нажатие, повтор после обрыва связи) — те же процессы.
        JsonObject?[] runs = [new JsonObject { ["profile"] = "k1" }, new JsonObject { ["profile"] = "k2" }];
        var first = await h.Engine.StartProcessesAsync(scenario, runs, "b3");
        var again = await h.Engine.StartProcessesAsync(scenario, runs, "b3");
        Assert.Equal(first, again);
        Assert.Equal(2, (await h.Store.ListProcessesAsync(new ProcessQuery())).Count);

        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.StartProcessesAsync(scenario, [], null));
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h.Engine.StartProcessesAsync(scenario, [.. Enumerable.Range(0, ProcessEngine.MaxBatchRuns + 1).Select(_ => (JsonObject?)null)], null));
    }
}
