using Microsoft.Extensions.Logging;
using Pepa.Storage.Sqlite;

namespace Pepa.Host.Backups;

/// <summary>
/// История не растёт бесконечно: завершённые процессы старше срока из «Настроек» удаляются (раз в шесть часов). Незавершённые
/// не удаляются никогда, как и завершённые, связанные с ещё работающими (см. <see cref="SqliteStore.DeleteFinishedProcessesAsync"/>).
/// </summary>
public sealed class HistoryRetention(SqliteStore store, Func<int> days, TimeProvider time, ILogger logger) : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

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

        var deleted = await store.DeleteFinishedProcessesAsync(time.GetUtcNow().AddDays(-keep), cancellationToken: cancellationToken);
        if (deleted > 0)
        {
            logger.LogInformation("Из истории удалены завершённые процессы старше {Days} дн.: {Count}.", keep, deleted);
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
                catch (Microsoft.Data.Sqlite.SqliteException ex)
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
        await _loop;
        _stop.Dispose();
    }
}
