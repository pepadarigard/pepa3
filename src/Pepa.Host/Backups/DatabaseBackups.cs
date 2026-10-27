using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pepa.Storage.Sqlite;

namespace Pepa.Host.Backups;

/// <param name="Kind">daily — ежедневная, manual — по кнопке, migration — перед обновлением схемы базы.</param>
public sealed record BackupInfo(string Name, string Kind, DateTimeOffset CreatedAt, long Size);

/// <summary>
/// Резервные копии базы (сценарии, процессы, история, настройки): раз в сутки, по кнопке и перед обновлением схемы.
/// Старые копии удаляются: ежедневных хранится 7, сделанных вручную — 10, перед обновлением — 3. Восстановление — вручную
/// при закрытой Pepa (подменять базу работающей программы нельзя).
/// </summary>
public sealed partial class DatabaseBackups(SqliteStore store, TimeProvider time, ILogger logger) : IAsyncDisposable
{
    public static readonly TimeSpan DailyInterval = TimeSpan.FromHours(24);

    private static readonly Dictionary<string, int> Keep = new(StringComparer.Ordinal) { ["daily"] = 7, ["manual"] = 10, ["migration"] = 3 };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    public string Directory => store.BackupDirectory;

    /// <summary>Чем закончилась последняя автоматическая копия, если неудачей (иначе null).</summary>
    public string? LastError { get; private set; }

    [GeneratedRegex(@"^pepa-(daily|manual|migration)\d*-\d{8}-\d{6}(-\d+)?\.db$")]
    private static partial Regex NamePattern();

    public IReadOnlyList<BackupInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return new DirectoryInfo(Directory).GetFiles("pepa-*.db")
            .Select(f => (File: f, Match: NamePattern().Match(f.Name)))
            .Where(x => x.Match.Success)
            .Select(x => new BackupInfo(x.File.Name, x.Match.Groups[1].Value, new DateTimeOffset(x.File.LastWriteTimeUtc, TimeSpan.Zero), x.File.Length))
            .OrderByDescending(b => b.CreatedAt > time.GetUtcNow() + TimeSpan.FromMinutes(5) ? DateTimeOffset.MinValue : b.CreatedAt)
            .ToList();
    }

    public async Task<BackupInfo> CreateAsync(string kind, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var local = time.GetLocalNow().DateTime;
            var path = Path.Combine(Directory, SqliteStore.BackupFileName(kind, local));
            for (var i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(Directory, SqliteStore.BackupFileName(kind, local)[..^3] + $"-{i}.db");
            }

            await store.BackupAsync(path, cancellationToken);
            Prune();
            var file = new FileInfo(path);
            logger.LogInformation("Резервная копия базы: {Name} ({Size} байт).", file.Name, file.Length);
            return new BackupInfo(file.Name, kind, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), file.Length);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Ежедневная копия, если последней нет или она старше суток. Ошибка — в журнал и <see cref="LastError"/>.</summary>
    public async Task EnsureDailyAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Копия «из будущего» (часы были переведены) не считается: иначе новых копий не было бы до той даты.
            var now = time.GetUtcNow();
            var last = List().FirstOrDefault(b => b.Kind == "daily" && b.CreatedAt <= now + TimeSpan.FromMinutes(5));
            if (last is not null && now - last.CreatedAt < DailyInterval)
            {
                return;
            }

            await CreateAsync("daily", cancellationToken);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            logger.LogWarning(ex, "Не удалось сделать резервную копию базы.");
        }
    }

    /// <summary>Проверять раз в час, не пора ли сделать ежедневную копию (Pepa может работать неделями).</summary>
    public void Start() => _loop = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                await EnsureDailyAsync(_stop.Token);
                await Task.Delay(TimeSpan.FromHours(1), time, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    private void Prune()
    {
        foreach (var group in List().GroupBy(b => b.Kind))
        {
            foreach (var old in group.Skip(Keep.GetValueOrDefault(group.Key, 7)))
            {
                try
                {
                    File.Delete(Path.Combine(Directory, old.Name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Не удалось удалить старую копию {Name}.", old.Name);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (Exception ex)
        {
            // Остановка Pepa не должна прерываться из-за фоновой копии.
            logger.LogWarning(ex, "Резервное копирование остановилось с ошибкой.");
        }

        _stop.Dispose();
    }
}
