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

/// <summary>Вопрос процесса для уведомления: номер (новый вопрос — новый номер) и текст без секретов.</summary>
public sealed record AskedQuestion(long Id, string Text);

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
    private readonly Func<ProcessRecord, CancellationToken, Task<IReadOnlyList<AskedQuestion>>>? _questions;
    private readonly ITelegramGateway _telegram;
    private readonly Func<bool> _telegramEnabled;
    private readonly IDesktopAlerts? _desktop;
    private readonly ILogger _logger;
    private readonly TimeSpan _batchDelay;
    private readonly Channel<string> _changes = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Known> _notified = new(StringComparer.Ordinal);

    /// <summary>О чём уже сообщено: остановка (её состояние) и вопросы (номера — повтор того же текста тоже новый вопрос).</summary>
    private sealed record Known(string? Attention, IReadOnlySet<long> Questions);
    private readonly Dictionary<string, int> _failedReads = new(StringComparer.Ordinal);
    private readonly TimeSpan _retryDelay;
    private const int MaxReadRetries = 3;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _background = [];
    private int _disposed;

    /// <param name="load">Процесс по номеру (null — его нет).</param>
    /// <param name="telegramEnabled">Включено ли уведомление в Telegram (настройка может меняться на ходу).</param>
    /// <param name="batchDelay">Сколько собирать изменения в одну пачку.</param>
    /// <param name="retryDelay">Через сколько снова прочитать процесс, если чтение не удалось.</param>
    /// <param name="questions">Вопросы, которых ждёт процесс («Спросить пользователя»), уже без секретов.</param>
    public AttentionNotifier(Func<string, CancellationToken, Task<ProcessRecord?>> load, ITelegramGateway telegram, Func<bool> telegramEnabled,
        IDesktopAlerts? desktop, ILogger logger, TimeSpan? batchDelay = null, TimeSpan? retryDelay = null,
        Func<ProcessRecord, CancellationToken, Task<IReadOnlyList<AskedQuestion>>>? questions = null)
    {
        _questions = questions;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
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
    /// <param name="askedAtStart">Вопросы, заданные ещё до запуска (процесс → номер вопроса): о них тоже не сообщается снова.</param>
    public void Start(IEnumerable<ProcessRecord> waitingAtStart, RecoveryReport? recovery,
        IEnumerable<KeyValuePair<string, long>>? askedAtStart = null)
    {
        foreach (var process in waitingAtStart)
        {
            _notified[process.Id] = new Known(Signature(process), new HashSet<long>());
        }

        foreach (var (processId, question) in askedAtStart ?? [])
        {
            var known = _notified.GetValueOrDefault(processId);
            _notified[processId] = new Known(known?.Attention, new HashSet<long>((IEnumerable<long>?)known?.Questions ?? []) { question });
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

                var fresh = new List<Notice>();
                foreach (var id in ids)
                {
                    ProcessRecord? process;
                    try
                    {
                        process = await _load(id, stop);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // База занята или недоступна — прочитаем позже: остановившийся процесс сам больше не изменится, и
                        // без повтора уведомление пропало бы.
                        var failures = _failedReads.GetValueOrDefault(id) + 1;
                        _failedReads[id] = failures;
                        _logger.LogWarning(ex, "Не удалось прочитать процесс {Id} для уведомления (попытка {Attempt}).", id, failures);
                        if (failures <= MaxReadRetries)
                        {
                            _background.Add(RetryLaterAsync(id, stop));
                        }

                        continue;
                    }

                    _failedReads.Remove(id);

                    var known = _notified.GetValueOrDefault(id);

                    // Процесс задал вопросы — о них тоже сообщается (без ответа процесс дальше не пойдёт), в каком бы
                    // состоянии процесс ни был: другая ветвь может работать или ждать решения. Не прочитались — известные
                    // остаются известными (иначе о них сообщили бы снова), а прочитать попробуем ещё раз.
                    IReadOnlyList<AskedQuestion> questions = [];
                    var questionsRead = true;
                    if (process is { IsTerminal: false } && _questions is not null)
                    {
                        try
                        {
                            questions = await _questions(process, stop);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            questionsRead = false;
                            var failures = _failedReads.GetValueOrDefault(id) + 1;
                            _failedReads[id] = failures;
                            _logger.LogWarning(ex, "Не удалось прочитать вопросы процесса {Id} для уведомления (попытка {Attempt}).", id, failures);
                            if (failures <= MaxReadRetries)
                            {
                                _background.Add(RetryLaterAsync(id, stop));
                            }
                        }
                    }

                    var attention = process is { Status: ProcessStatus.NeedsAttention } ? Signature(process) : null;
                    var asked = questionsRead ? questions.Select(q => q.Id).ToHashSet() : new HashSet<long>((IEnumerable<long>?)known?.Questions ?? []);
                    if (process is null || (attention is null && asked.Count == 0))
                    {
                        // Решение принято, ответ дан (или процесс удалён) — следующая остановка снова будет новостью.
                        _notified.Remove(id);
                        continue;
                    }

                    // Новость — новая остановка и каждый новый вопрос; пауза и продолжение, решение по другой ветви — нет.
                    _notified[id] = new Known(attention, asked);
                    if (attention is not null && attention != known?.Attention)
                    {
                        fresh.Add(new Notice(process, null));
                    }

                    foreach (var question in questions.Where(q => known?.Questions.Contains(q.Id) != true))
                    {
                        fresh.Add(new Notice(process, question.Text));
                    }
                }

                if (fresh.Count > 0)
                {
                    try
                    {
                        await NotifyAsync(fresh, stop);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Уведомление не отправлено.");
                    }
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

    private async Task RetryLaterAsync(string id, CancellationToken stop)
    {
        try
        {
            await Task.Delay(_retryDelay, stop);
            _changes.Writer.TryWrite(id);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task NotifyAsync(IReadOnlyList<Notice> notices, CancellationToken stop)
    {
        var text = Message(notices);
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


    /// <summary>О чём сообщить: процесс ждёт решения или (если есть вопрос) ответа на вопрос.</summary>
    internal sealed record Notice(ProcessRecord Process, string? Question);

    internal static string Message(IReadOnlyList<ProcessRecord> processes) => Message(processes.Select(p => new Notice(p, null)).ToList());

    internal static string Message(IReadOnlyList<Notice> notices)
    {
        var text = new StringBuilder();
        var asks = notices.All(n => n.Question is not null);
        if (notices.Count == 1)
        {
            var (p, question) = notices[0];
            if (question is not null)
            {
                text.AppendLine("Pepa: процесс задаёт вопрос.");
                text.AppendLine($"«{p.ScenarioName}» (#{Short(p.Id)}): {Cut(question, 600)}");
            }
            else
            {
                text.AppendLine("Pepa: процесс ждёт вашего решения.");
                text.AppendLine($"«{p.ScenarioName}» (#{Short(p.Id)}) — {Title(p.Attention)}.");
                if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                {
                    text.AppendLine(Cut(p.StatusMessage, 600));
                }
            }
        }
        else
        {
            text.AppendLine(asks ? $"Pepa: процессы задают вопросы — {notices.Count}." : $"Pepa: процессов ждут вашего решения — {notices.Count}.");
            foreach (var (p, question) in notices.Take(MaxListed))
            {
                text.AppendLine($"• «{p.ScenarioName}» (#{Short(p.Id)}) — " + (question is null ? Title(p.Attention) : "вопрос: " + Cut(question, 120)));
            }

            if (notices.Count > MaxListed)
            {
                text.AppendLine($"…и ещё {notices.Count - MaxListed}.");
            }
        }

        text.Append(notices.Any(n => n.Question is not null) ? "Ответить — в Pepa на «Главной»." : "Подробности — в Pepa на странице «Выполнение».");
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
