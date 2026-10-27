using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Pepa.Adapters.Telegram;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Host.Notifications;

namespace Pepa.Host.Tests;

/// <summary>Уведомления о процессах, ждущих решения: один раз на остановку, пачкой, только если включено и Telegram подключён.</summary>
public sealed class AttentionNotifierTests
{
    /// <summary>Telegram, который только отправляет в «Избранное».</summary>
    private sealed class SavedMessages : ITelegramGateway
    {
        public TelegramState State { get; set; } = TelegramState.Connected;
        public ConcurrentQueue<string> Messages { get; } = new();
        public bool Fail { get; set; }

        public TelegramStatus Status => new(State, "Я", null);

        public Task<TelegramPeer> ResolveAsync(string chat, CancellationToken cancellationToken) =>
            Task.FromResult(chat == "me" ? new TelegramPeer(TelegramPeerKind.User, 1, "Избранное", null) : throw new InvalidOperationException(chat));

        public Task<TelegramSentMessage> SendAsync(TelegramPeer chat, string text, long randomId, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new TelegramUnavailableException("нет связи");
            }

            Messages.Enqueue(text);
            return Task.FromResult(new TelegramSentMessage(chat, Messages.Count, DateTimeOffset.UtcNow));
        }

        public Task<int> LastMessageIdAsync(TelegramPeer chat, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TelegramHistoryMessage>> MessagesAfterAsync(TelegramPeer chat, int afterId, int max, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TelegramHistoryMessage?> GetMessageAsync(TelegramPeer chat, int messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TelegramCallbackAnswer> PressAsync(TelegramPeer chat, int messageId, TelegramButton button, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TelegramSentMessage> SendFileAsync(TelegramPeer chat, string path, string? caption, long randomId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> DownloadAsync(TelegramPeer chat, int messageId, string targetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Alerts : IDesktopAlerts
    {
        public int Count;
        public void RequestAttention(string text) => Interlocked.Increment(ref Count);
    }

    private static ProcessRecord Process(string id, ProcessStatus status, string message = "Шаг «Отправить» не выполнен", AttentionKind kind = AttentionKind.StepFailed) => new()
    {
        Id = "process-" + id,
        ScenarioId = "s1",
        ScenarioName = "Заказ " + id,
        ScenarioVersion = 1,
        DefinitionJson = "{}",
        Status = status,
        Attention = status == ProcessStatus.NeedsAttention ? kind : AttentionKind.None,
        StatusMessage = message,
        Variables = [],
        StepBudget = 10_000,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Не дождались.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Each_stop_is_reported_once_and_several_at_once_in_one_message()
    {
        var processes = new ConcurrentDictionary<string, ProcessRecord>();
        var telegram = new SavedMessages();
        var alerts = new Alerts();
        var enabled = true;
        await using var notifier = new AttentionNotifier((id, _) => Task.FromResult(processes.GetValueOrDefault(id)), telegram, () => enabled,
            alerts, NullLogger.Instance, TimeSpan.FromMilliseconds(30));
        var old = Process("old", ProcessStatus.NeedsAttention);
        processes[old.Id] = old;
        notifier.Start([old], recovery: null);

        // Уже ждавший решения при запуске — не новость; работающий — тоже.
        processes["process-run"] = Process("run", ProcessStatus.Running);
        notifier.OnProcessChanged(old.Id);
        notifier.OnProcessChanged("process-run");
        await Task.Delay(150);
        Assert.Empty(telegram.Messages);

        var a = Process("a", ProcessStatus.NeedsAttention, kind: AttentionKind.UnknownStep, message: "Неизвестно, ушло ли письмо");
        processes[a.Id] = a;
        notifier.OnProcessChanged(a.Id);
        notifier.OnProcessChanged(a.Id);
        await Eventually(() => telegram.Messages.Count == 1);
        var single = telegram.Messages.Single();
        Assert.Contains("«Заказ a»", single);
        Assert.Contains("неизвестно, выполнилось ли действие", single);
        Assert.Contains("Неизвестно, ушло ли письмо", single);
        Assert.Equal(1, alerts.Count);

        // То же состояние ещё раз — не новость.
        notifier.OnProcessChanged(a.Id);
        await Task.Delay(150);
        Assert.Single(telegram.Messages);

        // Решение принято, потом процесс снова остановился — новая остановка; вместе с другим процессом — одним сообщением.
        processes[a.Id] = a with { Status = ProcessStatus.Running, Attention = AttentionKind.None };
        notifier.OnProcessChanged(a.Id);
        await Task.Delay(150);
        processes[a.Id] = a;
        var b = Process("b", ProcessStatus.NeedsAttention);
        processes[b.Id] = b;
        notifier.OnProcessChanged(a.Id);
        notifier.OnProcessChanged(b.Id);
        await Eventually(() => telegram.Messages.Count == 2);
        Assert.Contains("процессов ждут вашего решения — 2", telegram.Messages.Last());

        // Выключено в «Настройках» — в Telegram не пишется, окно всё равно привлекает внимание.
        enabled = false;
        var c = Process("c", ProcessStatus.NeedsAttention);
        processes[c.Id] = c;
        notifier.OnProcessChanged(c.Id);
        await Eventually(() => alerts.Count == 3);
        Assert.Equal(2, telegram.Messages.Count);
    }

    [Fact]
    public async Task Without_Telegram_or_on_a_send_error_nothing_is_retried_and_nothing_breaks()
    {
        var processes = new ConcurrentDictionary<string, ProcessRecord>();
        var telegram = new SavedMessages { State = TelegramState.Disconnected };
        await using var notifier = new AttentionNotifier((id, _) => Task.FromResult(processes.GetValueOrDefault(id)), telegram, () => true,
            null, NullLogger.Instance, TimeSpan.FromMilliseconds(20));
        notifier.Start([], recovery: null);

        var a = Process("a", ProcessStatus.NeedsAttention);
        processes[a.Id] = a;
        notifier.OnProcessChanged(a.Id);
        await Task.Delay(150);
        Assert.Empty(telegram.Messages);
        await Assert.ThrowsAsync<InvalidOperationException>(() => notifier.SendTestAsync(CancellationToken.None));

        // Ошибка отправки — только запись в журнал; следующая остановка отправляется как обычно.
        telegram.State = TelegramState.Connected;
        telegram.Fail = true;
        var b = Process("b", ProcessStatus.NeedsAttention);
        processes[b.Id] = b;
        notifier.OnProcessChanged(b.Id);
        await Task.Delay(150);
        telegram.Fail = false;
        var c = Process("c", ProcessStatus.NeedsAttention);
        processes[c.Id] = c;
        notifier.OnProcessChanged(c.Id);
        await Eventually(() => telegram.Messages.Count == 1);
        Assert.Contains("«Заказ c»", telegram.Messages.Single());
        Assert.Equal(1, notifier.Sent);
    }

    [Fact]
    public async Task After_a_restart_with_stopped_processes_one_message_is_sent_once_Telegram_connects()
    {
        var telegram = new SavedMessages { State = TelegramState.Connecting };
        await using var notifier = new AttentionNotifier((_, _) => Task.FromResult<ProcessRecord?>(null), telegram, () => true, null,
            NullLogger.Instance, TimeSpan.FromMilliseconds(20));
        notifier.Start([], new RecoveryReport(Paused: 2, UnknownSteps: 1, CancelledPlannedSteps: 0));
        await Task.Delay(100);
        Assert.Empty(telegram.Messages);

        telegram.State = TelegramState.Connected;
        await Eventually(() => telegram.Messages.Count == 1);
        Assert.Contains("с неизвестным итогом шага — 1, приостановлено — 2", telegram.Messages.Single());
        Assert.Null(AttentionNotifier.StartupMessage(new RecoveryReport(0, 0, 3)));
    }

    [Fact]
    public async Task One_failed_read_of_the_database_does_not_stop_later_notifications()
    {
        var telegram = new SavedMessages();
        var alerts = new Alerts();
        var failOnce = 1;
        var processes = new ConcurrentDictionary<string, ProcessRecord>();
        await using var notifier = new AttentionNotifier((id, _) =>
                Interlocked.Exchange(ref failOnce, 0) == 1
                    ? throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5)
                    : Task.FromResult(processes.GetValueOrDefault(id)),
            telegram, () => true, alerts, NullLogger.Instance, TimeSpan.FromMilliseconds(20));
        notifier.Start([], null);
        processes["process-a"] = Process("a", ProcessStatus.NeedsAttention);
        notifier.OnProcessChanged("process-a");
        await Task.Delay(200);

        // Временная ошибка прошла — следующая остановка сообщается как обычно.
        processes["process-b"] = Process("b", ProcessStatus.NeedsAttention);
        notifier.OnProcessChanged("process-b");
        // Окно мигает раньше, чем уходит сообщение в Telegram, — ждём само сообщение.
        await Eventually(() => !telegram.Messages.IsEmpty);
        Assert.Contains("«Заказ b»", telegram.Messages.Single());
    }

    [Fact]
    public async Task Process_whose_read_failed_is_notified_after_a_retry()
    {
        var telegram = new SavedMessages();
        var alerts = new Alerts();
        var failOnce = 1;
        var processes = new ConcurrentDictionary<string, ProcessRecord>();
        await using var notifier = new AttentionNotifier((id, _) =>
                Interlocked.Exchange(ref failOnce, 0) == 1
                    ? throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5)
                    : Task.FromResult(processes.GetValueOrDefault(id)),
            telegram, () => true, alerts, NullLogger.Instance, TimeSpan.FromMilliseconds(20), retryDelay: TimeSpan.FromMilliseconds(100));
        notifier.Start([], null);
        processes["process-a"] = Process("a", ProcessStatus.NeedsAttention);
        // Остановившийся процесс больше не меняется — уведомление о нём приходит после повторного чтения.
        notifier.OnProcessChanged("process-a");
        await Eventually(() => !telegram.Messages.IsEmpty);
        Assert.Contains("«Заказ a»", telegram.Messages.Single());
    }

    [Fact]
    public async Task Test_message_without_Telegram_explains_why()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, error) = await f.SendAsync(HttpMethod.Post, "/api/notifications/test");
        Assert.Equal(409, status);
        Assert.Contains("Telegram не подключён", error!["error"]!.GetValue<string>());

        var (_, saved) = await f.SendAsync(HttpMethod.Put, "/api/settings", new
        {
            workspaceRoot = f.Workspace, maxParallelProcesses = 4, stepBudget = 10_000, actionTimeoutSeconds = 120, notifyTelegram = true,
        });
        Assert.True(saved!["notifyTelegram"]!.GetValue<bool>());
        Assert.True((await f.GetAsync("/api/settings"))["notifyTelegram"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Question_of_a_waiting_process_is_reported_once_with_its_text()
    {
        var processes = new ConcurrentDictionary<string, ProcessRecord>();
        var questions = new ConcurrentDictionary<string, string>();
        var telegram = new SavedMessages();
        await using var notifier = new AttentionNotifier((id, _) => Task.FromResult(processes.GetValueOrDefault(id)), telegram, () => true,
            null, NullLogger.Instance, TimeSpan.FromMilliseconds(30),
            question: (process, _) => Task.FromResult(questions.GetValueOrDefault(process.Id)));
        var old = Process("old", ProcessStatus.Waiting);
        processes[old.Id] = old;
        questions[old.Id] = "Вопрос до запуска";
        notifier.Start([], recovery: null, [KeyValuePair.Create(old.Id, "Вопрос до запуска")]);

        var asking = Process("q", ProcessStatus.Waiting);
        processes[asking.Id] = asking;
        questions[asking.Id] = "Оплатить заказ A-17 на 900 ₽?";
        notifier.OnProcessChanged(old.Id);
        notifier.OnProcessChanged(asking.Id);
        await Eventually(() => telegram.Messages.Count == 1);
        notifier.OnProcessChanged(asking.Id);
        await Task.Delay(150);

        var message = Assert.Single(telegram.Messages);
        Assert.Contains("процесс задаёт вопрос", message);
        Assert.Contains("«Заказ q» (#cess-q): Оплатить заказ A-17 на 900 ₽?", message);
        Assert.Contains("Ответить — в Pepa на «Главной»", message);
        Assert.DoesNotContain("до запуска", message);

        // Ответили — новый вопрос того же процесса снова новость.
        questions.TryRemove(asking.Id, out _);
        notifier.OnProcessChanged(asking.Id);
        await Task.Delay(150);
        questions[asking.Id] = "А теперь — код из SMS?";
        notifier.OnProcessChanged(asking.Id);
        await Eventually(() => telegram.Messages.Count == 2);
    }
}

