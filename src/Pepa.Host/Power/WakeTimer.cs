using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Pepa.Host.Power;

/// <summary>
/// Будит спящий компьютер к запуску по расписанию и к сроку ожидания (D-087): автозапуск «каждый день в 3:00» не должен
/// ждать, пока человек утром включит компьютер. Pepa заводит таймер пробуждения Windows на ближайший срок (за пару минут,
/// чтобы успели подключиться сеть, Telegram и почта), а проснувшись, недолго не даёт компьютеру уснуть снова — дальше
/// работу держит запрет сна (<see cref="PowerKeeper"/>), пока процессы выполняются или ждут.
/// </summary>
/// <remarks>
/// Таймеры пробуждения работают, если они разрешены в схеме питания Windows («Разрешить таймеры пробуждения»; на батарее
/// их часто запрещают — это решает Windows). Pepa должна быть запущена (свёрнута в область уведомлений). Выключенный
/// компьютер таймер не включит.
/// </remarks>
public sealed class WakeTimer : IDisposable
{
    /// <summary>За сколько до срока будить.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(2);

    /// <summary>Сколько не давать уснуть после пробуждения (Windows усыпляет «разбуженный таймером» компьютер через пару минут).</summary>
    public static readonly TimeSpan HoldAfterWake = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _nextDue;
    private readonly Func<bool> _enabled;
    private readonly IWakePlatform _platform;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread? _thread;
    private DateTimeOffset? _armedAt;
    private long _armedTicks; // то же, для чтения из других потоков: 0 — не заведено
    private DateTimeOffset? _holdUntil;
    private int _disposed;

    /// <param name="nextDue">Ближайший момент, когда Pepa должна что-то сделать (запуск по расписанию, срок ожидания).</param>
    /// <param name="enabled">Включена ли настройка «Будить компьютер» (может меняться на ходу).</param>
    /// <param name="platform">Таймер пробуждения; null — Windows (на других системах не поддерживается).</param>
    public WakeTimer(Func<CancellationToken, Task<DateTimeOffset?>> nextDue, Func<bool> enabled, ILogger logger,
        IWakePlatform? platform = null, TimeProvider? time = null)
    {
        _nextDue = nextDue;
        _enabled = enabled;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _platform = platform ?? (OperatingSystem.IsWindows() ? WindowsWakePlatform.TryCreate(logger) : null) ?? (IWakePlatform)UnsupportedWakePlatform.Instance;
        Supported = _platform.Supported;
        if (Supported)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Pepa: пробуждение" };
            _thread.Start();
        }
    }

    /// <summary>Можно ли на этой системе будить компьютер.</summary>
    public bool Supported { get; }

    /// <summary>На когда заведено пробуждение; null — не заведено.</summary>
    public DateTimeOffset? ArmedAt => Interlocked.Read(ref _armedTicks) is var ticks and not 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>Пробуждение заведено, отменено или сработало.</summary>
    public event Action? Changed;

    /// <summary>Сроки или настройка изменились — пересчитать пробуждение.</summary>
    public void Refresh()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _signal.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Loop()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Update(token);

                // Без сигнала — раз в 5 минут: срок мог появиться без уведомления (например, изменили автозапуск).
                var wait = TimeSpan.FromMinutes(5);
                if (_holdUntil is { } hold && hold - _time.GetUtcNow() is var left && left < wait)
                {
                    wait = left < TimeSpan.Zero ? TimeSpan.Zero : left;
                }

                var fired = WaitHandle.WaitAny([_signal, _platform.Fired, token.WaitHandle], wait);
                if (fired == 1)
                {
                    Woke();
                }

                if (_holdUntil is { } until && _time.GetUtcNow() >= until)
                {
                    _holdUntil = null;
                    _platform.HoldAwake(false);
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // Pepa закрывается.
        }
        finally
        {
            _platform.Cancel();
            _platform.HoldAwake(false);
        }
    }

    private void Update(CancellationToken token)
    {
        DateTimeOffset? due;
        try
        {
            due = _enabled() ? _nextDue(token).GetAwaiter().GetResult() : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Не знаем срока — оставить как было.
            _logger.LogWarning(ex, "Не удалось узнать ближайший срок для пробуждения компьютера.");
            return;
        }

        var now = _time.GetUtcNow();
        DateTimeOffset? at = due is { } d && d > now ? (d - Lead > now ? d - Lead : d) : null;
        if (at == _armedAt)
        {
            return;
        }

        if (at is { } wakeAt)
        {
            if (!_platform.Arm(wakeAt))
            {
                _logger.LogWarning("Windows не дала завести пробуждение компьютера на {At}.", wakeAt.ToLocalTime());
                return;
            }

            _logger.LogInformation("Pepa разбудит компьютер {At} (ближайший запуск по расписанию или срок ожидания).", wakeAt.ToLocalTime());
        }
        else
        {
            _platform.Cancel();
        }

        SetArmed(at);
        OnChanged();
    }

    /// <summary>Таймер сработал (компьютер, возможно, проснулся от него): не дать уснуть, пока не наступит срок и чуть после.</summary>
    private void Woke()
    {
        _logger.LogInformation("Сработало пробуждение компьютера, заведённое Pepa.");
        SetArmed(null);
        _holdUntil = _time.GetUtcNow() + HoldAfterWake;
        _platform.HoldAwake(true);
        OnChanged();
    }

    private void SetArmed(DateTimeOffset? at)
    {
        _armedAt = at;
        Interlocked.Exchange(ref _armedTicks, at?.UtcTicks ?? 0);
    }

    private void OnChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Обработчик пробуждения компьютера завершился с ошибкой.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Поток пробуждения компьютера не остановился за 5 секунд.");
        }

        _stop.Dispose();
        _signal.Dispose();
        _platform.Dispose();
    }
}

/// <summary>Таймер пробуждения системы.</summary>
public interface IWakePlatform : IDisposable
{
    bool Supported { get; }

    /// <summary>Срабатывает, когда наступило заведённое время.</summary>
    WaitHandle Fired { get; }

    /// <summary>Завести пробуждение на это время (заменяет прежнее).</summary>
    bool Arm(DateTimeOffset at);

    void Cancel();

    /// <summary>Не давать компьютеру уснуть (из потока таймера) или снять это.</summary>
    void HoldAwake(bool hold);
}

internal sealed class UnsupportedWakePlatform : IWakePlatform
{
    public static readonly UnsupportedWakePlatform Instance = new();

    public bool Supported => false;
    public WaitHandle Fired { get; } = new ManualResetEvent(false);
    public bool Arm(DateTimeOffset at) => false;
    public void Cancel() { }
    public void HoldAwake(bool hold) { }
    public void Dispose() { }
}

/// <summary>Таймер ожидания Windows с пробуждением системы (SetWaitableTimer с fResume).</summary>
internal sealed class WindowsWakePlatform : IWakePlatform
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const int ErrorNotSupported = 50;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly SafeWaitHandle _handle;
    private readonly TimerHandle _wait;
    private readonly ILogger _logger;
    private bool _warnedNotSupported;

    private WindowsWakePlatform(SafeWaitHandle handle, ILogger logger)
    {
        _handle = handle;
        _wait = new TimerHandle(handle);
        _logger = logger;
    }

    public static WindowsWakePlatform? TryCreate(ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        if (handle.IsInvalid)
        {
            logger.LogWarning("Не удалось создать таймер пробуждения Windows (ошибка {Code}).", Marshal.GetLastWin32Error());
            handle.Dispose();
            return null;
        }

        return new WindowsWakePlatform(handle, logger);
    }

    public bool Supported => true;

    public WaitHandle Fired => _wait;

    public bool Arm(DateTimeOffset at)
    {
        // Положительное значение — абсолютное время (FILETIME, UTC).
        var due = at.UtcDateTime.ToFileTimeUtc();
        if (!SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, true))
        {
            return false;
        }

        // Таймер заведён, но система не умеет просыпаться по нему (или это запрещено) — Windows сообщает об этом так.
        if (Marshal.GetLastWin32Error() == ErrorNotSupported && !_warnedNotSupported)
        {
            _warnedNotSupported = true;
            _logger.LogWarning("Windows не поддерживает пробуждение компьютера по таймеру на этом компьютере.");
        }

        return true;
    }

    public void Cancel() => CancelWaitableTimer(_handle);

    public void HoldAwake(bool hold) => SetThreadExecutionState(hold ? EsContinuous | EsSystemRequired : EsContinuous);

    public void Dispose()
    {
        CancelWaitableTimer(_handle);
        _wait.Dispose();
    }

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    // Простые сигнатуры без указателей: DllImport не требует небезопасного кода в проекте (в отличие от LibraryImport).
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine,
        IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
