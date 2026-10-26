using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;
using TL;

namespace Pepa.Adapters.Telegram.Tests;

/// <summary>Кнопки ботов, отправка файла и сохранение вложений — на имитации Telegram и настоящем движке.</summary>
public sealed class TelegramButtonsTests
{
    private static readonly TelegramPeer Bot = new(TelegramPeerKind.User, 2002, "Магазин", "shop_bot");

    private static readonly TelegramButton[] Buttons =
    [
        new(1, 1, "Подтвердить заказ", TelegramButtonKind.Callback),
        new(1, 2, "Отменить заказ", TelegramButtonKind.Callback),
        new(2, 1, "Сайт", TelegramButtonKind.Url, "https://shop.example/order/1"),
    ];

    private static async Task<(EngineHarness H, FakeTelegram T)> SetupAsync()
    {
        var telegram = new FakeTelegram();
        EngineHarness? harness = null;
        var h = harness = await EngineHarness.CreateAsync(
            extraNodes: r => r.RegisterTelegramNodes(telegram, new FileSystem.Workspace(() => Path.Combine(harness!.Directory, "workspace"))));
        Directory.CreateDirectory(Path.Combine(h.Directory, "workspace"));
        return (h, telegram);
    }

    private static ScenarioDefinition One(string type, object config) => new ScenarioBuilder("Telegram")
        .Variable("result", VariableType.Any)
        .Node("start", "start").Node("act", type, config).Node("end", "end", new { message = "готово" })
        .Chain("start", "act", "end").Build();

    [Fact]
    public async Task Button_under_the_latest_bot_message_is_pressed_by_text_and_answer_is_saved()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        t.AddBotMessage(Bot, "Старое", [new TelegramButton(1, 1, "Подтвердить заказ", TelegramButtonKind.Callback)]);
        var latest = t.AddBotMessage(Bot, "Заказ №1 собран", Buttons);
        t.BotAnswer = "Заказ подтверждён";

        var id = await h.RunAsync(One("telegram.button", new { chat = "@shop_bot", button = "подтвердить заказ", saveTo = "result" }));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal([(latest.MessageId, "Подтвердить заказ")], t.Pressed);
        Assert.Equal("Заказ подтверждён", done.Variables["result"]!["answer"]!["message"]!.GetValue<string>());
        Assert.Equal(latest.MessageId, done.Variables["result"]!["messageId"]!.GetValue<int>());
    }

    [Fact]
    public async Task Ambiguous_or_missing_button_is_not_pressed()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        t.AddBotMessage(Bot, "Заказ №1", Buttons);

        var ambiguous = await h.RunAsync(One("telegram.button", new { chat = "@shop_bot", button = "заказ", match = "contains" }));
        Assert.Contains("подходит несколько кнопок", (await h.WaitForStatusAsync(ambiguous, ProcessStatus.NeedsAttention)).StatusMessage);
        await h.Engine.CancelAsync(ambiguous);

        var missing = await h.RunAsync(One("telegram.button", new { chat = "@shop_bot", button = "Оплатить" }));
        var failed = await h.WaitForStatusAsync(missing, ProcessStatus.NeedsAttention);
        Assert.Contains("нет кнопки «Оплатить»", failed.StatusMessage);
        Assert.Contains("«Отменить заказ»", failed.StatusMessage);
        Assert.Empty(t.Pressed);
    }

    [Fact]
    public async Task Link_button_gives_its_address_and_keyboard_button_sends_its_text_once()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        t.AddBotMessage(Bot, "Ссылка", Buttons);
        var link = await h.RunAsync(One("telegram.button", new { chat = "@shop_bot", button = "Сайт", saveTo = "result" }));
        var linked = await h.WaitForStatusAsync(link, ProcessStatus.Completed);
        Assert.Equal("https://shop.example/order/1", linked.Variables["result"]!["url"]!.GetValue<string>());
        Assert.Empty(t.Pressed);

        // Кнопка клавиатуры отправляет свой текст; потерянное подтверждение проверяется по истории — второй раз не отправляется.
        t.AddBotMessage(Bot, "Выберите", [new TelegramButton(1, 1, "Да", TelegramButtonKind.Text, Keyboard: true)]);
        t.LoseConfirmation = true;
        var keyboard = await h.RunAsync(One("telegram.button", new { chat = "@shop_bot", button = "Да" }));
        await h.WaitForStatusAsync(keyboard, ProcessStatus.Completed);
        Assert.Single(t.Sent, s => s.Message.Text == "Да");
        Assert.Contains("истории чата", (await h.StepsAsync(keyboard)).Single(s => s.NodeId == "act").Resolution);
    }

    [Fact]
    public async Task Press_with_lost_answer_is_not_repeated_by_itself()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        t.AddBotMessage(Bot, "Заказ", Buttons);
        t.PressLosesAnswer = true;
        var id = await h.RunAsync(One("telegram.button", new { chat = "@shop_bot", button = "Подтвердить заказ" }));
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.UnknownStep, process.Attention);
        Assert.Contains("посмотрите на ответ бота", process.StatusMessage);
        Assert.Single(t.Pressed);
    }

    [Fact]
    public async Task File_is_sent_with_caption_and_lost_confirmation_is_checked_by_file_name()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        Directory.CreateDirectory(Path.Combine(h.Directory, "workspace", "отчёты"));
        await File.WriteAllTextAsync(Path.Combine(h.Directory, "workspace", "отчёты", "заказ.pdf"), "pdf", TestContext.Current.CancellationToken);
        t.LoseConfirmation = true;
        var id = await h.RunAsync(One("telegram.send", new { chat = "@ivan", file = "отчёты/заказ.pdf", text = "Ваш заказ", saveTo = "result" }));
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal([(new TelegramPeer(TelegramPeerKind.User, 1001, "Иван", "ivan"), "заказ.pdf", (string?)"Ваш заказ")], t.Files);

        var missing = await h.RunAsync(One("telegram.send", new { chat = "@ivan", file = "нет.pdf" }));
        Assert.Contains("нет в рабочей папке", (await h.WaitForStatusAsync(missing, ProcessStatus.NeedsAttention)).StatusMessage);
        Assert.Contains(h.Engine.Validator.Validate(One("telegram.send", new { chat = "@ivan" })).Issues,
            i => i.Message.Contains("текст сообщения или файл", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attachment_is_saved_into_the_workspace()
    {
        var (h, t) = await SetupAsync();
        await using var _ = h;
        var message = t.AddBotMessage(Bot, "Ваш чек", media: new TelegramMedia("document", "чек.pdf", 3, "application/pdf"), content: [1, 2, 3]);
        var photo = t.AddBotMessage(Bot, "Фото", media: new TelegramMedia("photo", null, null, "image/jpeg"), content: [9]);

        var id = await h.RunAsync(One("telegram.download", new { chat = "@shop_bot", messageId = message.MessageId.ToString(), saveTo = "result" }));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("telegram/чек.pdf", done.Variables["result"]!["path"]!.GetValue<string>());
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(h.Directory, "workspace", "telegram", "чек.pdf"), TestContext.Current.CancellationToken));

        var named = await h.RunAsync(One("telegram.download", new { chat = "@shop_bot", messageId = photo.MessageId.ToString(), path = "фото/" }));
        await h.WaitForStatusAsync(named, ProcessStatus.Completed);
        Assert.True(File.Exists(Path.Combine(h.Directory, "workspace", "фото", $"telegram-{photo.MessageId}.jpg")));

        // Имя от собеседника не выводит из папки и не ломается на символах, недопустимых в Windows.
        var hostile = t.AddBotMessage(Bot, "Отчёт", media: new TelegramMedia("document", "../../отчёт: Q1.pdf", 1, "application/pdf"), content: [7]);
        var safe = await h.RunAsync(One("telegram.download", new { chat = "@shop_bot", messageId = hostile.MessageId.ToString(), saveTo = "result" }));
        Assert.Equal("telegram/отчёт_ Q1.pdf", (await h.WaitForStatusAsync(safe, ProcessStatus.Completed)).Variables["result"]!["path"]!.GetValue<string>());

        var plain = t.AddBotMessage(Bot, "Без файла");
        var nothing = await h.RunAsync(One("telegram.download", new { chat = "@shop_bot", messageId = plain.MessageId.ToString() }));
        Assert.Contains("нет файла или фото", (await h.WaitForStatusAsync(nothing, ProcessStatus.NeedsAttention)).StatusMessage);
    }

    [Fact]
    public void Incoming_buttons_and_media_are_read_from_Telegram_messages_and_put_into_the_event()
    {
        var markup = new ReplyInlineMarkup
        {
            rows =
            [
                new KeyboardInlineButtonRow
                {
                    buttons =
                    [
                        new KeyboardInlineButton { text = "Да", type = new InlineButtonTypeCallback { data = [1] } },
                        new KeyboardInlineButton { text = "Сайт", type = new InlineButtonTypeUrl { url = "https://a.example" } },
                    ],
                },
            ],
        };
        Assert.Equal(
            [new TelegramButton(1, 1, "Да", TelegramButtonKind.Callback), new TelegramButton(1, 2, "Сайт", TelegramButtonKind.Url, "https://a.example")],
            TelegramService.ButtonsOf(markup));
        var keyboard = new ReplyKeyboardMarkup { rows = [new KeyboardButtonRow { buttons = [new KeyboardButton { text = "Меню" }] }] };
        Assert.Equal([new TelegramButton(1, 1, "Меню", TelegramButtonKind.Text, Keyboard: true)], TelegramService.ButtonsOf(keyboard));

        var media = TelegramService.MediaOf(new MessageMediaDocument
        {
            flags = MessageMediaDocument.Flags.has_document,
            document = new Document { mime_type = "application/pdf", size = 42, attributes = [new DocumentAttributeFilename { file_name = "чек.pdf" }] },
        });
        Assert.Equal(new TelegramMedia("document", "чек.pdf", 42, "application/pdf"), media);

        var input = TelegramEvents.ToEvent(FakeTelegram.Incoming(Bot, "Чек", 5) with { Buttons = TelegramService.ButtonsOf(markup), Media = media });
        Assert.Equal("callback", input.Payload!["buttons"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("чек.pdf", input.Payload!["media"]!["fileName"]!.GetValue<string>());
    }
}
