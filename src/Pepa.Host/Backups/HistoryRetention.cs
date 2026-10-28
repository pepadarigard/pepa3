using Microsoft.Extensions.Logging;
using Pepa.Host.Snapshots;
using Pepa.Storage.Sqlite;

namespace Pepa.Host.Backups;

/// <summary>
/// История не растёт бесконечно: завершённые процессы старше срока из «Настроек» удаляются (раз в шесть часов). Незавершённые
/// не удаляются никогда, как и завершённые, связанные с ещё работающими (см. <see cref="SqliteStore.DeleteFinishedProcessesAsync"/>).
/// У обработанных событий старше срока удаляются данные, а через год — и сами строки (см. <see cref="SqliteStore.PruneEventsAsync"/>).
/// Снимки экрана удалённых процессов удаляются вслед за ними.
/// </summary>
public sealed class HistoryRetention(SqliteStore store, Func<int> days, TimeProvider time, ILogger logger, SnapshotFolder? snapshots = null)
    : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Сколько дней хранится сама строка события (без данных) — чтобы повторно пришедшее событие узнавалось.</summary>
    private const int EventRowDays = 365;

    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    /// <returns>Сколько процессов удалено.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var keep = days();
        if (keep <= 0)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var deleted = await store.DeleteFinishedProcessesAsync(now.AddDays(-keep), cancellationToken: cancellationToken);
        if (deleted > 0)
        {
            logger.LogInformation("Из истории удалены завершённые процессы старше {Days} дн.: {Count}.", keep, deleted);
        }

        // События: данные — через тот же срок, строки (по ним узнаётся повторно пришедшее) — не раньше чем через год.
        var (cleared, removed) = await store.PruneEventsAsync(now.AddDays(-keep), now.AddDays(-Math.Max(keep, EventRowDays)),
            cancellationToken: cancellationToken);
        if (cleared > 0 || removed > 0)
        {
            logger.LogInformation("События старше {Days} дн.: данные удалены у {Cleared}, удалено совсем {Removed}.", keep, cleared, removed);
        }

        if (snapshots is not null)
        {
            var orphans = await snapshots.DeleteOrphansAsync(async id => await store.GetProcessAsync(id, cancellationToken) is not null,
                cancellationToken);
            if (orphans > 0)
            {
                logger.LogInformation("Удалены снимки экрана процессов, которых больше нет в истории: {Count}.", orphans);
            }
        }

        return deleted;
    }

    public void Start() => _loop = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                try
                {
                    await RunOnceAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // История подождёт до следующего раза — работе процессов это не мешает.
                    logger.LogWarning(ex, "Не удалось очистить историю.");
                }

                await Task.Delay(Interval, time, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

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
            logger.LogWarning(ex, "Очистка истории остановилась с ошибкой.");
        }

        _stop.Dispose();
    }
}
