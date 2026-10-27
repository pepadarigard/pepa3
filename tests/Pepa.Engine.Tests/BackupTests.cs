using Microsoft.Data.Sqlite;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;
using Pepa.Storage.Sqlite;

namespace Pepa.Engine.Tests;

/// <summary>Резервные копии базы: согласованная копия одним файлом и копия перед обновлением схемы.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pepa-backup-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static StoredScenario Scenario(string id) => new()
    {
        Id = id, Name = id, Description = "", DefinitionJson = "{}", Version = 1,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task SaveAsync(SqliteStore store, string id)
    {
        await using var write = await store.BeginWriteAsync(TestContext.Current.CancellationToken);
        await write.SaveScenarioAsync(Scenario(id), null);
        await write.CommitAsync();
    }

    private static async Task<(long Version, long Scenarios, string Journal)> ReadAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        async Task<object?> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        return ((long)(await Scalar("PRAGMA user_version;"))!, (long)(await Scalar("SELECT COUNT(*) FROM scenarios;"))!,
            (string)(await Scalar("PRAGMA journal_mode;"))!);
    }

    [Fact]
    public async Task Backup_is_a_complete_single_file_copy_and_never_overwrites()
    {
        var store = new SqliteStore(Path.Combine(_dir, "pepa.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SaveAsync(store, "a");
        await SaveAsync(store, "b");
        var target = Path.Combine(store.BackupDirectory, "copy.db");

        await store.BackupAsync(target, TestContext.Current.CancellationToken);
        var (_, scenarios, journal) = await ReadAsync(target);
        Assert.Equal(2, scenarios);
        // Одним файлом: без журнала WAL рядом, копию можно просто положить на место базы.
        Assert.Equal("delete", journal);
        Assert.False(File.Exists(target + "-wal"));
        Assert.Empty(Directory.GetFiles(store.BackupDirectory, "*.tmp"));

        await Assert.ThrowsAsync<IOException>(() => store.BackupAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Database_is_copied_before_its_schema_is_updated()
    {
        var path = Path.Combine(_dir, "pepa.db");
        var old = new SqliteStore(path);
        await old.InitializeAsync(1, TestContext.Current.CancellationToken);
        await SaveAsync(old, "старый");
        SqliteConnection.ClearAllPools();

        var store = new SqliteStore(path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var copy = Assert.Single(Directory.GetFiles(store.BackupDirectory, "pepa-migration1-*.db"));
        var (version, scenarios, _) = await ReadAsync(copy);
        Assert.Equal(1, version);
        Assert.Equal(1, scenarios);

        // Уже обновлённая база при следующем запуске не копируется.
        SqliteConnection.ClearAllPools();
        await new SqliteStore(path).InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetFiles(store.BackupDirectory));
    }

    [Fact]
    public async Task After_restoring_a_copy_unfinished_processes_wait_for_the_users_decision()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("a", TestActionNode.TypeName, new { key = "a" })
            .Node("w", "wait", new { mode = "duration", amount = 10, unit = "minutes" })
            .Node("b", TestActionNode.TypeName, new { key = "b" })
            .Node("end", "end")
            .Chain("start", "a", "w", "b", "end")
            .Build();
        var id = await h.RunAsync(scenario);
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var copy = Path.Combine(h.Directory, "copy.db");
        await h.Store.BackupAsync(copy, TestContext.Current.CancellationToken);

        // После копии процесс дошёл до конца: действие «b» уже сделано во внешнем мире.
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromMinutes(11));
        Assert.Equal(1, h.World.Executions("b"));

        // Пользователь восстановил базу из копии: в ней процесс ещё ждёт и «b» не сделано.
        await h.RestartAsync(whileClosed: () =>
        {
            SqliteConnection.ClearAllPools();
            var db = h.Store.DatabasePath;
            File.Copy(copy, db, overwrite: true);
            File.Delete(db + "-wal");
            File.Delete(db + "-shm");
            h.Store.InitializeAsync().GetAwaiter().GetResult();
        }, beforeStart: engine => engine.RestoredFromBackupAt = h.Store.RestoredFromBackupAt);

        Assert.NotNull(h.Store.RestoredFromBackupAt);
        var restored = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.NeedsAttention, restored.Status);
        Assert.Equal(AttentionKind.RestoredFromBackup, restored.Attention);
        Assert.Contains("восстановлена из резервной копии", restored.StatusMessage);
        Assert.Equal(1, h.LastRecovery!.Restored);
        Assert.Equal([ProcessAction.Continue, ProcessAction.Cancel],
            h.Engine.GetAllowedActions(restored, await h.Store.GetTokensAsync(id)));

        // Сам он ничего не делает, даже когда срок ожидания прошёл.
        await h.IdleAsync(TimeSpan.FromHours(1));
        Assert.Equal(1, h.World.Executions("b"));
        Assert.Equal(ProcessStatus.NeedsAttention, (await h.ProcessAsync(id)).Status);

        // Отметка «восстановлено» снимается — при следующем запуске это обычная база.
        await h.Store.ClearRestoreMarkerAsync();
        await h.Store.InitializeAsync();
        Assert.Null(h.Store.RestoredFromBackupAt);
    }

    [Fact]
    public async Task Copy_made_by_an_older_version_can_be_restored_into_a_newer_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "pepa.db");
        // Старая версия Pepa (схема 1) сделала ежедневную копию.
        var old = new SqliteStore(path);
        await old.InitializeAsync(1, ct);
        await SaveAsync(old, "старый");
        var copy = Path.Combine(_dir, "copy.db");
        await old.BackupAsync(copy, ct);
        SqliteConnection.ClearAllPools();

        // Pepa обновили, а базу заменили этой копией: перед обновлением схемы делается новая копия — уже из копии.
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        File.Copy(copy, path);
        var store = new SqliteStore(path);
        await store.InitializeAsync(ct);
        Assert.NotNull(store.RestoredFromBackupAt);
        var migration = Assert.Single(Directory.GetFiles(store.BackupDirectory, "pepa-migration1-*.db"));
        Assert.Equal(1, (await ReadAsync(migration)).Scenarios);
    }
}
