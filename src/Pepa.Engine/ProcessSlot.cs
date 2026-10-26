namespace Pepa.Engine;

internal enum PendingRequest
{
    None,
    Pause,
    Cancel,
}

/// <summary>
/// Право на изменение состояния одного процесса. Им владеет либо исполнитель (пока выполняет шаги),
/// либо управляющая операция (пауза, остановка, возобновление). Если процесс сейчас выполняется,
/// пауза и остановка откладываются до ближайшей границы между шагами.
/// </summary>
internal sealed class ProcessSlot
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pending;
    private CancellationTokenSource? _stepCancellation;

    /// <summary>Хранилище отказало при работе с процессом — до перезапуска процесс не трогаем.</summary>
    public volatile bool Faulted;

    public PendingRequest Pending => (PendingRequest)Volatile.Read(ref _pending);

    public void Request(PendingRequest request)
    {
        // Остановка важнее паузы.
        int current;
        do
        {
            current = Volatile.Read(ref _pending);
            if ((PendingRequest)current == PendingRequest.Cancel)
            {
                break;
            }
        } while (Interlocked.CompareExchange(ref _pending, (int)request, current) != current);

        if (request == PendingRequest.Cancel)
        {
            try
            {
                Volatile.Read(ref _stepCancellation)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Шаг уже завершился.
            }
        }
    }

    public PendingRequest TakePending() => (PendingRequest)Interlocked.Exchange(ref _pending, (int)PendingRequest.None);

    public bool TryEnter() => _gate.Wait(0);

    public Task<bool> EnterAsync(TimeSpan timeout) => _gate.WaitAsync(timeout);

    public void Exit() => _gate.Release();

    public CancellationTokenSource BeginStep()
    {
        var cts = new CancellationTokenSource();
        Volatile.Write(ref _stepCancellation, cts);
        if (Pending == PendingRequest.Cancel)
        {
            cts.Cancel();
        }

        return cts;
    }

    public void EndStep(CancellationTokenSource cts)
    {
        Interlocked.CompareExchange(ref _stepCancellation, null, cts);
        cts.Dispose();
    }
}
