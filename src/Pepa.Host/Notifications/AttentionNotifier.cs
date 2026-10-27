using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pepa.Adapters.Telegram;
using Pepa.Engine;
using Pepa.Engine.Persistence;

namespace Pepa.Host.Notifications;

/// <summary>Окно Pepa на Windows: привлечь внимание (мигание на панели задач), если окно сейчас не перед пользователем.</summary>
public interface IDesktopAlerts
{
    void RequestAttention(string text);
}

/// <summary>
/// Сообщает, что процесс остановился и ждёт решения: окну Pepa (мигание на панели задач) и, если включено в «Настройках»,
/// в Telegram — в «Избранное» своего аккаунта (полезно, когда вас нет у компьютера). Сообщения собираются в пачку
/// (несколько процессов — одно сообщение), о каждой остановке — один раз. Уведомление — не действие сценария: оно не
/// повторяется и его сбой ни на что не влияет (только запись в журнал Pepa).
/// </summary>
public sealed class AttentionNotifier : IAsyncDisposable
{
    /// <summary>Сколько ждать сообщения о запуске, пока Telegram подключается.</summary>
    private static readonly TimeSpan StartupMessageWait = TimeSpan.FromMinutes(5);

    private const int MaxListed = 8;

    private readonly Func<string, CancellationToken, Task<ProcessRecord?>> _load;
    private readonly ITelegramGateway _telegram;
    private readonly Func<bool> _telegramEnabled;
    private readonly IDesktopAlerts? _desktop;
    private readonly ILogger _logger;
    private readonly TimeSpan _batchDelay;
    private readonly Channel<string> _changes = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, string> _notified = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _background = [];
    private int _disposed;

    /// <param name="load">Процесс по номеру (null — его нет).</param>
    /// <param name="telegramEnabled">Включено ли уведомление в Telegram (настройка может меняться на ходу).</param>
    /// <param name="batchDelay">Сколько собирать изменения в одну пачку.</param>
    public AttentionNotifier(Func<string, CancellationToken, Task<ProcessRecord?>> load, ITelegramGateway telegram, Func<bool> telegramEnabled,
        IDesktopAlerts? desktop, ILogger logger, TimeSpan? batchDelay = null)
    {
        _load = load;
        _telegram = telegram;
        _telegramEnabled = telegramEnabled;
        _desktop = desktop;
        _logger = logger;
        _batchDelay = batchDelay ?? TimeSpan.FromSeconds(5);
    }

    private int _sent;

    /// <summary>Сколько сообщений отправлено в Telegram (для проверок).</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <param name="waitingAtStart">Процессы, которые ждали решения ещё до запуска: о них отдельно не сообщается.</param>
    /// <param name="recovery">Итог восстановления: если после перезапуска процессы ждут решения — одно сообщение об этом.</param>
    public void Start(IEnumerable<ProcessRecord> waitingAtStart, RecoveryReport? recovery)
    {
        foreach (var process in waitingAtStart)
        {
            _notified[process.Id] = Signature(process);
        }

        _background.Add(Task.Run(() => LoopAsync(_stop.Token)));
        if (recovery is not null && StartupMessage(recovery) is { } message)
        {
            _background.Add(Task.Run(() => SendWhenConnectedAsync(message, _stop.Token)));
        }
    }

    /// <summary>Процесс изменился (вызывается на каждое изменение — дёшево, разбор в фоне).</summary>
    public void OnProcessChanged(string processId) => _changes.Writer.TryWrite(processId);

    private async Task LoopAsync(CancellationToken stop)
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(stop))
            {
                // Несколько процессов, остановившихся почти одновременно, — одно сообщение.
                await Task.Delay(_batchDelay, stop);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                while (_changes.Reader.TryRead(out var id))
                {
                    ids.Add(id);
                }

                var fresh = new List<ProcessRecord>();
                foreach (var id in ids)
                {
                    var process = await _load(id, stop);
                    if (process is not { Status: ProcessStatus.NeedsAttention })
                    {
                        // Решение принято (или процесс удалён) — следующая остановка снова будет новостью.
                        _notified.Remove(id);
                        continue;
                    }

                    var signature = Signature(process);
                    if (_notified.GetValueOrDefault(id) != signature)
                    {
                        _notified[id] = signature;
                        fresh.Add(process);
                    }
                }

                if (fresh.Count > 0)
                {
                    await NotifyAsync(fresh, stop);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Уведомления не должны мешать работе — но и молча пропадать тоже.
            _logger.LogError(ex, "Уведомления о процессах остановлены из-за ошибки.");
        }
    }

    private async Task NotifyAsync(IReadOnlyList<ProcessRecord> processes, CancellationToken stop)
    {
        var text = Message(processes);
        try
        {
            _desktop?.RequestAttention(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось привлечь внимание к окну Pepa.");
        }

        await TrySendAsync(text, stop);
    }

    /// <summary>Отправить в «Избранное», если включено и Telegram подключён. Один раз, без повторов.</summary>
    private async Task<bool> TrySendAsync(string text, CancellationToken stop)
    {
        if (!_telegramEnabled())
        {
            return false;
        }

        if (_telegram.Status.State != TelegramState.Connected)
        {
            _logger.LogInformation("Уведомление в Telegram не отправлено: Telegram не подключён.");
            return false;
        }

        try
        {
            var me = await _telegram.ResolveAsync("me", stop);
            await _telegram.SendAsync(me, text, Random.Shared.NextInt64(1, long.MaxValue), stop);
            Interlocked.Increment(ref _sent);
            return true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Уведомление в Telegram не отправлено.");
            return false;
        }
    }

    /// <summary>Сообщение о запуске: Telegram подключается в фоне — дождаться его (недолго).</summary>
    private async Task SendWhenConnectedAsync(string text, CancellationToken stop)
    {
        try
        {
            var deadline = DateTime.UtcNow + StartupMessageWait;
            while (DateTime.UtcNow < deadline)
            {
                if (!_telegramEnabled())
                {
                    return;
                }

                if (_telegram.Status.State == TelegramState.Connected)
                {
                    await TrySendAsync(text, stop);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>Пробное уведомление из «Настроек».</summary>
    public async Task SendTestAsync(CancellationToken cancellationToken)
    {
        if (_telegram.Status.State != TelegramState.Connected)
        {
            throw new InvalidOperationException("Telegram не подключён — подключите его в «Интеграциях».");
        }

        var me = await _telegram.ResolveAsync("me", cancellationToken);
        await _telegram.SendAsync(me, "Pepa: пробное уведомление. Так будут приходить сообщения о процессах, которые ждут вашего решения.",
            Random.Shared.NextInt64(1, long.MaxValue), cancellationToken);
    }

    private static string Signature(ProcessRecord process) => $"{(int)process.Attention}|{process.AttentionStepId}|{process.StatusMessage}";

    internal static string Message(IReadOnlyList<ProcessRecord> processes)
    {
        var text = new StringBuilder();
        if (processes.Count == 1)
        {
            var p = processes[0];
            text.AppendLine("Pepa: процесс ждёт вашего решения.");
            text.AppendLine($"«{p.ScenarioName}» (#{Short(p.Id)}) — {Title(p.Attention)}.");
            if (!string.IsNullOrWhiteSpace(p.StatusMessage))
            {
                text.AppendLine(Cut(p.StatusMessage, 600));
            }
        }
        else
        {
            text.AppendLine($"Pepa: процессов ждут вашего решения — {processes.Count}.");
            foreach (var p in processes.Take(MaxListed))
            {
                text.AppendLine($"• «{p.ScenarioName}» (#{Short(p.Id)}) — {Title(p.Attention)}");
            }

            if (processes.Count > MaxListed)
            {
                text.AppendLine($"…и ещё {processes.Count - MaxListed}.");
            }
        }

        text.Append("Подробности — в Pepa на странице «Выполнение».");
        return text.ToString();
    }

    internal static string? StartupMessage(RecoveryReport recovery)
    {
        if (recovery.Paused + recovery.UnknownSteps + recovery.Restored == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (recovery.UnknownSteps > 0)
        {
            parts.Add($"с неизвестным итогом шага — {recovery.UnknownSteps}");
        }

        if (recovery.Paused > 0)
        {
            parts.Add($"приостановлено — {recovery.Paused}");
        }

        if (recovery.Restored > 0)
        {
            parts.Add($"база из резервной копии, ждут решения — {recovery.Restored}");
        }

        return "Pepa запущена снова. Пока она была закрыта, процессы остановились и сами не продолжатся: " +
               string.Join(", ", parts) + ". Подробности — в Pepa на «Главной».";
    }

    private static string Title(AttentionKind kind) => kind switch
    {
        AttentionKind.UnknownStep => "неизвестно, выполнилось ли действие",
        AttentionKind.StepFailed => "шаг не выполнен",
        AttentionKind.StepLimit => "слишком много шагов",
        AttentionKind.RestoredFromBackup => "база восстановлена из копии",
        _ => "нужна проверка",
    };

    private static string Short(string id) => id.Length > 6 ? id[^6..] : id;

    private static string Cut(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync();
        _changes.Writer.TryComplete();
        await Task.WhenAll(_background);
        _stop.Dispose();
    }
}
