using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

public sealed class StoreTests
{
    [Fact]
    public async Task Initialization_is_idempotent()
    {
        await using var h = await EngineHarness.CreateAsync();
        await h.Store.InitializeAsync();
        await h.Store.InitializeAsync();
        Assert.Empty(await h.Store.ListScenariosAsync());
    }

    [Fact]
    public async Task Stale_scenario_version_is_rejected()
    {
        await using var h = await EngineHarness.CreateAsync();
        var definition = new ScenarioBuilder("Версии")
            .Node("start", "start").Node("end", "end").Chain("start", "end").Build();
        var created = await h.Scenarios.CreateAsync(definition);

        var updated = await h.Scenarios.UpdateAsync(created.Id, definition with { Name = "Версия 2" }, created.Version);
        Assert.Equal(2, updated.Version);

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            h.Scenarios.UpdateAsync(created.Id, definition with { Name = "Устаревшая" }, created.Version));
        Assert.Equal("Версия 2", (await h.Scenarios.GetAsync(created.Id))!.Name);
    }

    [Fact]
    public async Task Uncommitted_write_is_rolled_back()
    {
        await using var h = await EngineHarness.CreateAsync();
        var definition = new ScenarioBuilder("Откат")
            .Node("start", "start").Node("end", "end").Chain("start", "end").Build();

        await using (var write = await h.Store.BeginWriteAsync())
        {
            await write.SaveScenarioAsync(new StoredScenario
            {
                Id = "rollback",
                Name = "Откат",
                Description = "",
                DefinitionJson = Pepa.Core.Serialization.PepaJson.Serialize(definition),
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, null);
            // Без CommitAsync.
        }

        Assert.Null(await h.Store.GetScenarioAsync("rollback"));
    }

    [Fact]
    public async Task Duplicate_creates_independent_copy()
    {
        await using var h = await EngineHarness.CreateAsync();
        var definition = new ScenarioBuilder("Оригинал")
            .Node("start", "start").Node("end", "end").Chain("start", "end").Build();
        var created = await h.Scenarios.CreateAsync(definition);

        var copy = await h.Scenarios.DuplicateAsync(created.Id);

        Assert.NotEqual(created.Id, copy.Id);
        Assert.Equal("Оригинал (копия)", copy.Name);
        Assert.Equal(2, (await h.Scenarios.ListAsync()).Count);
    }
}
