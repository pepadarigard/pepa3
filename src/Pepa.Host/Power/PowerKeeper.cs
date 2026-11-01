using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Pepa.Host.Power;

/// <summary>
/// Не даёт Windows уснуть по бездействию, пока процессы выполняются или ждут (D-080): процесс, который час ждёт ответа в
/// Telegram, не должен проиграть тайм-ауту из-за обычной схемы питания. Экран при этом гаснет как обычно; закрытая крышка
/// ноутбука и «Сон» из меню «Пуск» компьютер всё равно усыпят. Когда работы нет, запрет снимается.
/// </summary>
/// <remarks>
/// Запрет сна в Windows действует, пока жив поток, который его поставил, — поэтому у запрета свой поток. Он же раз в минуту
/// и после изменений процессов (с небольшой задержкой, чтобы не на каждом шаге) смотрит, есть ли работа.
/// </remarks>
public sealed class PowerKeeper : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private readonly Func<CancellationToken, Task<bool>> _hasWork;
    private readonly Func<bool> _enabled;
    private readonly Func<bool, bool> _apply;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread? _thread;
    private volatile bool _active;
    private int _disposed;

    /// <param name="hasWork">Есть ли процессы, которые выполняются или ждут.</param>
    /// <param name="enabled">Включена ли настройка (может меняться на ходу).</param>
    /// <param name="apply">Поставить (true) или снять запрет сна; возвращает, удалось ли. null — Windows, а на других
    /// системах запрет не поддерживается.</param>
    public PowerKeeper(Func<CancellationToken, Task<bool>> hasWork, Func<bool> enabled, ILogger logger, Func<bool, bool>? apply = null,
        TimeSpan? debounce = null)
    {
        _hasWork = hasWork;
        _enabled = enabled;
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromSeconds(1);
        Supported = apply is not null || OperatingSystem.IsWindows();
        _apply = apply ?? ApplyWindows;
        if (Supported)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Pepa: запрет сна" };
            _thread.Start();
        }
    }

    /// <summary>Можно ли на этой системе не давать компьютеру засыпать.</summary>
    public bool Supported { get; }

    /// <summary>Компьютер сейчас не уснёт по бездействию из-за Pepa.</summary>
    public bool Active => _active;

    /// <summary>Запрет сна поставлен или снят.</summary>
    public event Action? Changed;

    /// <summary>Процессы или настройка изменились — проверить, нужен ли запрет.</summary>
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
                // Без сигнала — раз в минуту: ожидание могло закончиться без уведомления.
                if (_signal.WaitOne(TimeSpan.FromMinutes(1)) && _debounce > TimeSpan.Zero)
                {
                    token.WaitHandle.WaitOne(_debounce);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                bool wanted;
                try
                {
                    wanted = _enabled() && _hasWork(token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Не знаем, есть ли работа: оставить как было.
                    _logger.LogWarning(ex, "Не удалось проверить, есть ли работа, для запрета сна.");
                    continue;
                }

                Set(wanted);
            }
        }
        finally
        {
            Set(false);
        }
    }

    private void Set(bool wanted)
    {
        if (wanted == _active)
        {
            return;
        }

        if (!_apply(wanted))
        {
            _logger.LogWarning("Windows не дала {Action} запрет сна.", wanted ? "поставить" : "снять");
            return;
        }

        _active = wanted;
        _logger.LogInformation(wanted
            ? "Компьютер не уснёт по бездействию, пока процессы выполняются или ждут."
            : "Работы нет — компьютер снова может засыпать.");
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Обработчик запрета сна завершился с ошибкой.");
        }
    }

    private static bool ApplyWindows(bool keepAwake) =>
        OperatingSystem.IsWindows() && SetThreadExecutionState(keepAwake ? EsContinuous | EsSystemRequired : EsContinuous) != 0;

    // Простая сигнатура без указателей: DllImport не требует небезопасного кода в проекте (в отличие от LibraryImport).
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _signal.Set();
        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Поток запрета сна не остановился за 5 секунд.");
        }

        _stop.Dispose();
        _signal.Dispose();
    }
}
