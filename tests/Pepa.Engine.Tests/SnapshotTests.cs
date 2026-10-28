using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Снимок экрана при ошибке шага: остаётся в результате шага, без значений секретов; на итог не влияет.</summary>
public sealed class SnapshotTests
{
    private static ScenarioDefinition Shop(int retries = 0) => new ScenarioBuilder("Магазин")
        .Variable("token", VariableType.Text, isInput: true, secret: true)
        .Node("start", "start")
        .Node("act", TestActionNode.TypeName, new { key = "buy", snapshotUrl = "https://shop.example/cart?t={{ vars.token }}" },
            settings: new NodeSettings { RetryCount = retries, RetryDelaySeconds = 0 })
        .Node("end", "end")
        .Chain("start", "act", "end").Build();

    [Fact]
    public async Task Failed_step_keeps_the_snapshot_with_secrets_masked()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("buy", ActionMode.Fail);
        var id = await h.RunAsync(Shop(), new JsonObject { ["token"] = "Tok-998877" });
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        var failed = (await h.StepsAsync(id)).Single(s => s.NodeId == "act");
        Assert.Equal(StepState.Failed, failed.State);
        var snapshot = failed.Output!["snapshot"]!.AsObject();
        Assert.Equal("001.jpg", snapshot["image"]!.GetValue<string>());
        Assert.Equal($"https://shop.example/cart?t={ProcessEngine.SecretMask}", snapshot["url"]!.GetValue<string>());
        await h.Engine.CancelAsync(id);
    }

    [Fact]
    public async Task Each_failed_attempt_has_its_own_snapshot_and_a_successful_one_has_none()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("buy", ActionMode.Fail);
        var id = await h.RunAsync(Shop(retries: 1), new JsonObject { ["token"] = "Tok-998877" });
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        var attempts = (await h.StepsAsync(id)).Where(s => s.NodeId == "act").OrderBy(s => s.Attempt).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.NotNull(attempts[0].Output?["snapshot"]);
        Assert.Null(attempts[1].Output?["snapshot"]);
    }
}
