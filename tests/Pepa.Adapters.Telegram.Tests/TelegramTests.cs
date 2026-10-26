using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Telegram.Tests;

/// <summary>Блоки Telegram и входящие сообщения как события — на имитации Telegram и настоящем движке.</summary>
public sealed class TelegramTests
{
    private static readonly TelegramPeer Ivan = new(TelegramPeerKind.User, 1001, "Иван", "ivan");
    private static readonly TelegramPeer Bot = new(TelegramPeerKind.User, 2002, "Магазин", "shop_bot");

    private static async Task<(EngineHarness H, FakeTelegram T)> SetupAsync(bool withFaults = false)
    {
        var telegram = new FakeTelegram();
        var h = await EngineHarness.CreateAsync(withFaults: withFaults, extraNodes: r => r.RegisterTelegramNodes(telegram));
        return (h, telegram);
    }

    private static ScenarioDefinition AskAndWait(string chat = "@ivan", string? contains = null, int? timeout = null) => new ScenarioBuilder("Спросить и ждать")
        .Variable("sent", VariableType.Any).Variable("reply", VariableType.Any)
        .Node("start", "start")
        .Node("send", "telegram.send", new { chat, text = "Заказ готов?", saveTo = "sent" })
        .Node("wait", "telegram.wait", new { chat, contains, timeoutSeconds = timeout, saveTo = "reply" })
        .Node("end", "end", new { message = "Ответ: {{ vars.reply.payload.text }}" })
        .Node("late", "end", new { outcome = "failure", message = "Ответа нет" })
        .Edge("start", "send").Edge("send", "wait").Edge("wait", "end", Ports.Event).Edge("wait", "late", Ports.Timeout)
        .Build();

    private static async Task<long> ReceiveAsync(EngineHarness h, TelegramIncomingMessage message) =>
        (await h.Engine.PublishEventAsync(TelegramEvents.ToEvent(message))).EventId;

    [Fact]
    public void Incoming_message_becomes_event_keyed_by_chat()
    {
        var input = TelegramEvents.ToEvent(FakeTelegram.Incoming(Ivan, "Привет", 55, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
        Assert.Equal("telegram", input.Source);
        Assert.Equal("message", input.Type);
        Assert.Equal("user:1001", input.CorrelationKey);
        Assert.Equal("user:1001:55", input.ExternalId);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), input.OccurredAt);
        Assert.Equal("Привет", input.Payload!["text"]!.GetValue<string>());
        Assert.Equal("ivan", input.Payload!["chat"]!["username"]!.GetValue<string>());

        Assert.True(TelegramEvents.TryParseKey("channel:42", out var kind, out var id));
        Assert.Equal((TelegramPeerKind.Channel, 42L), (kind, id));
        Assert.False(TelegramEvents.TryParseKey("@ivan", out _, out _));
        Assert.Equal("+79001234567", TelegramService.NormalizePhone("+7 (900) 123-45-67"));
    }

    [Fact]
    public async Task Process_sends_message_and_gets_reply_only_from_that_chat()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        var id = await h.RunAsync(AskAndWait());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        Assert.Single(t.Sent);
        Assert.Equal("user:1001", (await h.ProcessAsync(id)).Variables["sent"]!["chat"]!["key"]!.GetValue<string>());

        await ReceiveAsync(h, FakeTelegram.Incoming(Bot, "Реклама", 1));
        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);

        await ReceiveAsync(h, FakeTelegram.Incoming(Ivan, "Да, готов", 2));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("Ответ: Да, готов", done.StatusMessage);
    }

    [Fact]
    public async Task Text_filter_skips_other_messages_from_the_same_chat()
    {
        var (h, _) = await SetupAsync();
        await using var __ = h;
        var id = await h.RunAsync(AskAndWait("@shop_bot", contains: "КОД"));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        await ReceiveAsync(h, FakeTelegram.Incoming(Bot, "Ваш заказ принят", 10));
        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);

        await ReceiveAsync(h, FakeTelegram.Incoming(Bot, "Ваш код: 4512", 11));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("Ответ: Ваш код: 4512", done.StatusMessage);
    }

    [Fact]
    public async Task Same_message_delivered_twice_is_processed_once()
    {
        var (h, _) = await SetupAsync();
        await using var __ = h;
        var first = await h.RunAsync(AskAndWait());
        await h.WaitForStatusAsync(first, ProcessStatus.Waiting);
        var e1 = await ReceiveAsync(h, FakeTelegram.Incoming(Ivan, "Да", 7));
        await h.WaitForStatusAsync(first, ProcessStatus.Completed);

        var second = await h.RunAsync(AskAndWait());
        await h.WaitForStatusAsync(second, ProcessStatus.Waiting);
        var e2 = await ReceiveAsync(h, FakeTelegram.Incoming(Ivan, "Да", 7)); // догрузка после перезапуска прислала его снова
        Assert.Equal(e1, e2);
        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(second)).Status);
    }

    [Fact]
    public async Task Lost_confirmation_is_checked_in_chat_history_and_message_is_not_sent_twice()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        t.LoseConfirmation = true;
        var id = await h.RunAsync(AskAndWait());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        Assert.Single(t.Sent);
        var send = (await h.StepsAsync(id)).Single(s => s.NodeId == "send");
        Assert.Equal(StepState.Succeeded, send.State);
        Assert.Contains("истории чата", send.Resolution);
    }

    [Fact]
    public async Task Failure_before_sending_is_confirmed_by_history_and_stops_the_process()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        t.FailBeforeSend = true;
        var id = await h.RunAsync(AskAndWait());
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Empty(t.Sent);

        // Повтор безопасен: проверка подтвердила, что сообщение не ушло.
        t.FailBeforeSend = false;
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        Assert.Single(t.Sent);
    }

    [Fact]
    public async Task Not_connected_and_unknown_chat_give_clear_reasons()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        var unknown = await h.RunAsync(AskAndWait("@nobody"));
        Assert.Contains("не найден", (await h.WaitForStatusAsync(unknown, ProcessStatus.NeedsAttention)).StatusMessage);

        t.Status = new TelegramStatus(TelegramState.NotConfigured, null, null);
        var offline = await h.RunAsync(AskAndWait());
        Assert.Contains("не подключён", (await h.WaitForStatusAsync(offline, ProcessStatus.NeedsAttention)).StatusMessage);
        Assert.Empty(t.Sent);
    }

    [Fact]
    public async Task Empty_chat_variable_is_an_error_not_any_chat()
    {
        var (h, _) = await SetupAsync();
        await using var __ = h;
        var id = await h.RunAsync(new ScenarioBuilder("Пустой чат")
            .Variable("who", VariableType.Text, "")
            .Node("start", "start").Node("wait", "telegram.wait", new { chat = "{{ vars.who }}" }).Node("end", "end")
            .Edge("start", "wait").Edge("wait", "end", Ports.Event).Build());
        Assert.Contains("получился пустым", (await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention)).StatusMessage);
    }

    [Fact]
    public async Task Message_received_while_Pepa_was_closed_reaches_waiting_process_but_starts_no_trigger()
    {
        var (h, _) = await SetupAsync();
        await using var __ = h;
        var scenario = await h.CreateScenarioAsync(new ScenarioBuilder("По сообщению")
            .Node("start", "start").Node("a", TestActionNode.TypeName, new { key = "by-message" }).Node("end", "end")
            .Chain("start", "a", "end").Build());
        await h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "", ScenarioId = scenario, Kind = TriggerKind.Event,
            Config = new JsonObject { ["source"] = "telegram", ["eventType"] = "message" }, CreatedAt = default, UpdatedAt = default,
        });

        // Сообщение отправлено час назад (Pepa была закрыта) и догружено сейчас.
        var old = await ReceiveAsync(h, FakeTelegram.Incoming(Bot, "старое", 1, h.Time.GetUtcNow().AddHours(-1)));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await h.Store.GetEventAsync(old))!.State == EventState.Received && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, h.World.Executions("by-message"));
        Assert.Equal(EventState.Unmatched, (await h.Store.GetEventAsync(old))!.State);

        // Новое сообщение — запускает.
        await ReceiveAsync(h, FakeTelegram.Incoming(Bot, "новое", 2, h.Time.GetUtcNow()));
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (h.World.Executions("by-message") == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, h.World.Executions("by-message"));
    }
}
