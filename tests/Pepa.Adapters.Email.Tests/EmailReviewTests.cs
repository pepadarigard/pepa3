using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using MimeKit;
using Pepa.Core;

namespace Pepa.Adapters.Email.Tests;

/// <summary>Замечания независимой проверки почты: каждое — сценарий, в котором письмо терялось, зависало или доставалось не тому.</summary>
public sealed class EmailReviewTests : IAsyncDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pepa-email-review", Guid.NewGuid().ToString("N"));
    private readonly FakeImapServer _server = new();
    private readonly ConcurrentQueue<EventInput> _events = new();
    private readonly List<EmailService> _services = [];

    private static readonly EmailAccount Mailbox = new() { Server = "x", Login = "anna@example.com" };

    private EmailService Create(EmailServiceOptions? options = null)
    {
        var service = new EmailService(_data, e =>
        {
            _events.Enqueue(e);
            return Task.CompletedTask;
        }, options ?? new EmailServiceOptions { OperationTimeout = TimeSpan.FromSeconds(10) });
        _services.Add(service);
        return service;
    }

    private EmailAccount Account() => new()
    {
        Server = "127.0.0.1", Port = _server.Port, Security = EmailSecurity.None, Login = _server.Login,
    };

    private async Task<EmailService> ConnectedAsync()
    {
        var service = Create();
        await service.ConfigureAsync(Account(), _server.Password, CancellationToken.None);
        service.Start(DateTimeOffset.UtcNow, runLoop: false);
        await service.CheckAsync(CancellationToken.None);
        return service;
    }

    private static MimeMessage Parse(string raw) => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw.ReplaceLineEndings("\r\n"))));

    [Fact]
    public void Hostile_html_is_read_in_linear_time()
    {
        var html = string.Concat(Enumerable.Repeat("<style>x", 20_000)) + string.Concat(Enumerable.Repeat("<", 400_000)) + "<p>код 482913</p>";
        var watch = Stopwatch.StartNew();
        var text = EmailEvents.HtmlToText(html);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"разбор занял {watch.Elapsed}");
        Assert.DoesNotContain("482913", text);

        watch.Restart();
        var cells = EmailEvents.HtmlToText(string.Concat(Enumerable.Repeat("<td>а</td>", 200_000)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"разбор занял {watch.Elapsed}");
        Assert.StartsWith("а а а", cells);
    }

    [Fact]
    public void Table_cells_are_separated_but_styled_digits_stay_together()
    {
        Assert.Equal("Дата 2026 482913", EmailEvents.HtmlToText("<table><tr><td>Дата 2026</td><td>482913</td></tr></table>"));
        Assert.Equal("Код: 4829", EmailEvents.HtmlToText("Код: <span>4</span><span>8</span><b>2</b><i>9</i>"));
    }

    [Fact]
    public void Links_are_taken_only_from_a_tags_also_without_quotes_and_not_from_the_head()
    {
        var e = EmailEvents.ToEvent(Parse(FakeImapServer.Message("a@site.ru", "Ссылки",
            "<html><head><link href=\"https://fonts.example/css\" rel=stylesheet></head><body>" +
            "<a class=btn href=https://site.ru/confirm?t=abc>Подтвердить</a> <img src=\"https://pixel.example/p.gif\"></body></html>", html: true)),
            1, 1, DateTimeOffset.UtcNow, Mailbox);

        var links = e.Payload!["links"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
        Assert.Equal(["https://site.ru/confirm?t=abc"], links);
    }

    [Fact]
    public void Lowercase_delivered_to_and_huge_fields_are_handled()
    {
        var raw = "delivered-to: Anna+Shop@Example.com\r\n" + FakeImapServer.Message("a@site.ru", new string('я', 5000), "текст", to: "other@example.com");
        var payload = EmailEvents.ToEvent(Parse(raw), 1, 1, DateTimeOffset.UtcNow, Mailbox).Payload!;

        Assert.Contains("anna+shop@example.com", payload["to"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(1000, payload["subject"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task Different_mails_with_the_same_message_id_are_different_events()
    {
        var service = await ConnectedAsync();
        _server.Deliver(FakeImapServer.Message("scanner@office.ru", "Скан 1", "1", id: "scan"));
        _server.Deliver(FakeImapServer.Message("scanner@office.ru", "Скан 2", "2", id: "scan"));

        await service.CheckAsync(CancellationToken.None);

        Assert.Equal(2, _events.Select(e => e.ExternalId).Distinct().Count());
    }

    [Fact]
    public async Task Mail_the_server_will_not_return_is_skipped_after_a_few_tries_and_the_next_one_arrives()
    {
        var service = await ConnectedAsync();
        var broken = _server.Deliver(FakeImapServer.Message("a@site.ru", "Повреждено", "x"));
        _server.Unreadable[broken] = true;
        _server.Deliver(FakeImapServer.Message("noreply@site.ru", "Код", "482913"));

        await service.CheckAsync(CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);
        Assert.Empty(_events);
        Assert.Equal(EmailState.Connected, service.Status.State);
        await service.CheckAsync(CancellationToken.None);

        Assert.Equal("Код", Assert.Single(_events).Payload!["subject"]!.GetValue<string>());
        Assert.Contains($"Письмо №{broken}", service.Status.Note);
    }

    [Fact]
    public void The_same_mailbox_on_another_port_or_with_other_protection_is_the_same_mailbox()
    {
        var a = new EmailAccount { Server = "imap.yandex.ru", Port = 993, Login = "Anna@yandex.ru", Folder = "inbox" };
        var b = a with { Server = "IMAP.yandex.ru", Port = 143, Security = EmailSecurity.StartTls, Login = "anna@yandex.ru", Folder = "INBOX" };
        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, (a with { Folder = "Спам" }).Key);
    }

    [Fact]
    public async Task New_password_keeps_mail_that_came_meanwhile_but_turning_mail_back_on_starts_from_now()
    {
        var service = await ConnectedAsync();
        _server.Deliver(FakeImapServer.Message("a@site.ru", "Пока меняли пароль", "1"));
        _server.Password = "новый";
        await service.ConfigureAsync(Account(), "новый", CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);
        Assert.Equal("Пока меняли пароль", Assert.Single(_events).Payload!["subject"]!.GetValue<string>());

        await service.ConfigureAsync(Account() with { Enabled = false }, null, CancellationToken.None);
        _server.Deliver(FakeImapServer.Message("a@site.ru", "Пока была выключена", "2"));
        await service.ConfigureAsync(Account(), null, CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);
        Assert.Single(_events);
    }

    [Fact]
    public async Task Damaged_settings_do_not_stop_pepa_and_the_reason_stays_visible()
    {
        Directory.CreateDirectory(Path.Combine(_data, "email"));
        await File.WriteAllTextAsync(Path.Combine(_data, "email", "account.json"), "{\"server\":null,\"login\":\"a\",\"port\":993}");
        var nullServer = Create();
        Assert.False(nullServer.WillCatchUp);
        nullServer.Start(DateTimeOffset.UtcNow, runLoop: false);
        await nullServer.CheckAsync(CancellationToken.None);
        Assert.Contains("повреждены", nullServer.Status.Error);
        Assert.Contains("повреждены", nullServer.Problem);

        await File.WriteAllTextAsync(Path.Combine(_data, "email", "account.json"), "не json");
        var broken = Create();
        broken.Start(DateTimeOffset.UtcNow, runLoop: false);
        await broken.CheckAsync(CancellationToken.None);
        Assert.Contains("не читаются", broken.Status.Error);
    }

    [Fact]
    public async Task Unreadable_state_is_noted_and_clearing_during_catch_up_ends_the_catch_up()
    {
        var first = await ConnectedAsync();
        await first.DisposeAsync();
        _services.Remove(first);
        await File.WriteAllTextAsync(Path.Combine(_data, "email", "state.json"), "{обрыв");

        var second = Create();
        second.Start(DateTimeOffset.UtcNow, runLoop: false);
        await second.CheckAsync(CancellationToken.None);
        Assert.Contains("не прочиталось", second.Status.Note);

        await second.DisposeAsync();
        _services.Remove(second);
        var third = Create();
        Assert.True(third.WillCatchUp);
        var caughtUp = false;
        third.CaughtUp += () => caughtUp = true;
        third.Start(DateTimeOffset.UtcNow, runLoop: false);
        await third.ClearAsync(CancellationToken.None);
        Assert.True(caughtUp);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
        }

        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
