using Pepa.Core.Model;
using Pepa.Core.Serialization;
using Pepa.Core.Validation;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

/// <summary>Создание, изменение и проверка сценариев. Сценарий с ошибками сохраняется как черновик.</summary>
public sealed class ScenarioService(IPepaStore store, ScenarioValidator validator, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<IReadOnlyList<StoredScenario>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListScenariosAsync(cancellationToken);

    public Task<StoredScenario?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        store.GetScenarioAsync(id, cancellationToken);

    public ValidationReport Validate(ScenarioDefinition definition) => validator.Validate(definition);

    public static ScenarioDefinition Parse(StoredScenario scenario) =>
        PepaJson.Deserialize<ScenarioDefinition>(scenario.DefinitionJson);

    public async Task<StoredScenario> CreateAsync(ScenarioDefinition definition, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(definition.Id) || await store.GetScenarioAsync(definition.Id, cancellationToken) is not null
            ? ProcessEngine.NewId()
            : definition.Id;
        var now = _time.GetUtcNow();
        var normalized = Normalize(definition with { Id = id });
        var stored = new StoredScenario
        {
            Id = id,
            Name = normalized.Name,
            Description = normalized.Description,
            DefinitionJson = PepaJson.Serialize(normalized),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var write = await store.BeginWriteAsync(cancellationToken);
        await write.SaveScenarioAsync(stored, expectedVersion: null);
        await write.CommitAsync();
        return stored;
    }

    /// <summary>Сохранить изменения. <paramref name="expectedVersion"/> защищает от потери чужих правок.</summary>
    public async Task<StoredScenario> UpdateAsync(string id, ScenarioDefinition definition, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var existing = await store.GetScenarioAsync(id, cancellationToken)
                       ?? throw new EngineOperationException("Сценарий не найден.");
        if (existing.Version != expectedVersion)
        {
            throw new ConcurrencyException(
                "Сценарий уже изменён в другом месте. Обновите страницу, чтобы не потерять чужие изменения.");
        }

        var normalized = Normalize(definition with { Id = id });
        var stored = existing with
        {
            Name = normalized.Name,
            Description = normalized.Description,
            DefinitionJson = PepaJson.Serialize(normalized),
            Version = existing.Version + 1,
            UpdatedAt = _time.GetUtcNow(),
        };
        await using var write = await store.BeginWriteAsync(cancellationToken);
        await write.SaveScenarioAsync(stored, expectedVersion);
        await write.CommitAsync();
        return stored;
    }

    public async Task<StoredScenario> DuplicateAsync(string id, CancellationToken cancellationToken = default)
    {
        var existing = await store.GetScenarioAsync(id, cancellationToken)
                       ?? throw new EngineOperationException("Сценарий не найден.");
        var definition = Parse(existing);
        return await CreateAsync(definition with { Id = "", Name = $"{definition.Name} (копия)" }, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var write = await store.BeginWriteAsync(cancellationToken);
        var deleted = await write.DeleteScenarioAsync(id);
        await write.CommitAsync();
        return deleted;
    }

    private static ScenarioDefinition Normalize(ScenarioDefinition definition) => definition with
    {
        SchemaVersion = definition.SchemaVersion == 0 ? ScenarioDefinition.CurrentSchemaVersion : definition.SchemaVersion,
        Name = definition.Name.Trim(),
        Description = definition.Description.Trim(),
    };
}
