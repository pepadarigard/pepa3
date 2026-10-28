using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Pepa.Core;

namespace Pepa.Adapters.Email.Tests;

/// <summary>Слежение за ящиком на настоящем клиенте IMAP (MailKit) против имитации сервера.</summary>
public sealed class EmailServiceTests : IAsyncDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pepa-email-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeImapServer _server = new();
    private readonly ConcurrentQueue<EventInput> _events = new();
    private readonly List<EmailService> _services = [];

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

    private EmailAccount Account(string folder = "INBOX") => new()
    {
        Server = "127.0.0.1", Port = _server.Port, Security = EmailSecurity.None, Login = _server.Login, Folder = folder,
    };

    private async Task<EmailService> ConnectedAsync(EmailServiceOptions? options = null)
    {
        var service = Create(options);
        await service.ConfigureAsync(Account(), _server.Password, CancellationToken.None);
        service.Start(DateTimeOffset.UtcNow, runLoop: false);
        await service.CheckAsync(CancellationToken.None);
        return service;
    }

    [Fact]
    public async Task Mail_already_in_the_box_is_not_an_event_new_mail_is()
    {
        _server.Deliver(FakeImapServer.Message("old@site.ru", "Старое", "было до подключения"));
        var service = await ConnectedAsync();
        Assert.Empty(_events);
        Assert.Equal(EmailState.Connected, service.Status.State);
        Assert.Equal(1, service.Status.Messages);

        _server.Deliver(FakeImapServer.Message("Сайт <noreply@site.ru>", "Код подтверждения", "Ваш код: 482913"));
        await service.CheckAsync(CancellationToken.None);

        var e = Assert.Single(_events);
        Assert.Equal(EmailEvents.Source, e.Source);
        var payload = e.Payload!.AsObject();
        Assert.Equal("noreply@site.ru", payload["from"]!.GetValue<string>());
        Assert.Equal("Сайт", payload["fromName"]!.GetValue<string>());
        Assert.Equal("Код подтверждения", payload["subject"]!.GetValue<string>());
        Assert.Contains("482913", payload["text"]!.GetValue<string>());
        Assert.Equal("anna@example.com", payload["to"]![0]!.GetValue<string>());
        Assert.EndsWith(":1:2", e.ExternalId);
        // Только чтение: папка открыта через EXAMINE, флаги писем не менялись.
        Assert.Contains(_server.Commands, c => c.StartsWith("EXAMINE", StringComparison.Ordinal));
        Assert.DoesNotContain(_server.Commands, c => c.StartsWith("SELECT", StringComparison.Ordinal) || c.Contains("STORE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Html_mail_gives_plain_text_and_the_confirmation_link()
    {
        var service = await ConnectedAsync();
        _server.Deliver(FakeImapServer.Message("noreply@site.ru", "Подтвердите почту",
            "<html><head><style>p{color:red}</style></head><body><p>Здравствуйте!</p><p>Нажмите&nbsp;кнопку:</p>" +
            "<a href=\"https://site.ru/confirm?token=abc&amp;u=1\">Подтвердить</a><script>x()</script></body></html>", html: true));

        await service.CheckAsync(CancellationToken.None);

        var payload = Assert.Single(_events).Payload!;
        var text = payload["text"]!.GetValue<string>();
        Assert.Contains("Здравствуйте!", text);
        Assert.Contains("Нажмите кнопку:", text);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("x()", text);
        Assert.Equal("https://site.ru/confirm?token=abc&u=1", payload["links"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Mail_that_arrived_while_pepa_was_closed_is_delivered_after_start()
    {
        var first = await ConnectedAsync();
        var closedAt = DateTimeOffset.UtcNow;
        await first.DisposeAsync();
        _services.Remove(first);

        _server.Deliver(FakeImapServer.Message("noreply@site.ru", "Пока закрыта", "код 1111"));
        // Время прихода неизвестно (сервер его не сообщает) — письмо всё равно «пришло» до запуска, а не сейчас.
        _server.Deliver(FakeImapServer.Message("noreply@site.ru", "Без даты", "код 2222"));
        var second = Create();
        Assert.True(second.WillCatchUp);
        var caughtUp = false;
        second.CaughtUp += () => caughtUp = true;
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        second.Start(startedAt, runLoop: false);
        _server.WithoutInternalDate = true;
        await second.CheckAsync(CancellationToken.None);

        Assert.Equal(["Пока закрыта", "Без даты"], _events.Select(e => e.Payload!["subject"]!.GetValue<string>()));
        Assert.All(_events, e => Assert.InRange(e.OccurredAt!.Value, closedAt.AddSeconds(-2), startedAt.AddMilliseconds(-1)));
        Assert.True(caughtUp);
    }

    [Fact]
    public async Task Mail_moved_in_with_an_old_date_still_counts_as_new_for_waiting_processes()
    {
        var service = await ConnectedAsync();
        var checkedAt = DateTimeOffset.UtcNow;
        // Правило почты перенесло письмо из другой папки: у него дата прихода неделю назад.
        _server.Deliver(FakeImapServer.Message("noreply@site.ru", "Перенесено", "код"), DateTimeOffset.UtcNow.AddDays(-7));
        _server.Deliver(FakeImapServer.Message("noreply@site.ru", "Часы спешат", "код"), DateTimeOffset.UtcNow.AddHours(3));
        await service.CheckAsync(CancellationToken.None);

        var events = _events.ToList();
        Assert.True(events[0].OccurredAt >= checkedAt.AddSeconds(-2));
        Assert.True(events[1].OccurredAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Each_message_is_passed_once_even_across_checks_and_restarts()
    {
        var service = await ConnectedAsync();
        _server.Deliver(FakeImapServer.Message("a@site.ru", "Один", "1"));
        _server.Deliver(FakeImapServer.Message("b@site.ru", "Два", "2"));
        await service.CheckAsync(CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);
        await service.DisposeAsync();
        _services.Remove(service);

        var again = Create();
        again.Start(DateTimeOffset.UtcNow, runLoop: false);
        await again.CheckAsync(CancellationToken.None);

        Assert.Equal(["Один", "Два"], _events.Select(e => e.Payload!["subject"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Many_new_messages_are_taken_in_batches()
    {
        var service = await ConnectedAsync(new EmailServiceOptions { BatchSize = 2, OperationTimeout = TimeSpan.FromSeconds(10) });
        for (var i = 0; i < 5; i++)
        {
            _server.Deliver(FakeImapServer.Message("a@site.ru", $"№{i}", "x"));
        }

        Assert.True(await service.CheckAsync(CancellationToken.None));
        Assert.Equal(2, _events.Count);
        Assert.True(await service.CheckAsync(CancellationToken.None));
        Assert.False(await service.CheckAsync(CancellationToken.None));
        Assert.Equal(5, _events.Count);
    }

    [Fact]
    public async Task Large_message_gives_only_headers()
    {
        var service = await ConnectedAsync(new EmailServiceOptions { MaxMessageBytes = 300, OperationTimeout = TimeSpan.FromSeconds(10) });
        _server.Deliver(FakeImapServer.Message("a@site.ru", "Большое", new string('я', 2000)));

        await service.CheckAsync(CancellationToken.None);

        var payload = Assert.Single(_events).Payload!;
        Assert.Equal("Большое", payload["subject"]!.GetValue<string>());
        Assert.Equal("", payload["text"]!.GetValue<string>());
        Assert.True(payload["tooLarge"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Renumbered_box_starts_again_from_what_is_there_with_a_note()
    {
        var service = await ConnectedAsync();
        _server.Deliver(FakeImapServer.Message("a@site.ru", "Один", "1"));
        await service.CheckAsync(CancellationToken.None);
        _server.Renumber();
        await service.DisposeAsync();
        _services.Remove(service);

        var again = Create();
        again.Start(DateTimeOffset.UtcNow, runLoop: false);
        await again.CheckAsync(CancellationToken.None);

        Assert.Single(_events);
        Assert.Contains("заново пронумеровал", again.Status.Note);
    }

    [Fact]
    public async Task Wrong_password_or_folder_is_explained_and_nothing_is_saved()
    {
        var service = Create();
        var wrong = await Assert.ThrowsAsync<EmailUnavailableException>(() =>
            service.ConfigureAsync(Account(), "не тот", CancellationToken.None));
        Assert.Contains("пароль приложения", wrong.Message);
        Assert.Null(service.Account);

        var folder = await Assert.ThrowsAsync<EmailUnavailableException>(() =>
            service.ConfigureAsync(Account("Спам"), _server.Password, CancellationToken.None));
        Assert.Contains("нет папки «Спам»", folder.Message);
        Assert.False(File.Exists(Path.Combine(_data, "email", "account.json")));
    }

    [Fact]
    public async Task Other_folder_is_watched_by_name_and_the_password_is_kept_between_edits()
    {
        _server.AddFolder("Спам");
        var service = Create();
        await service.ConfigureAsync(Account("Спам"), _server.Password, CancellationToken.None);
        service.Start(DateTimeOffset.UtcNow, runLoop: false);
        await service.CheckAsync(CancellationToken.None);
        _server.Deliver(FakeImapServer.Message("a@site.ru", "В спаме", "код 5"), folder: "Спам");
        await service.CheckAsync(CancellationToken.None);
        Assert.Equal("Спам", Assert.Single(_events).Payload!["folder"]!.GetValue<string>());

        // Пароль не вводится заново, если сервер и логин те же.
        await service.ConfigureAsync(Account(), password: null, CancellationToken.None);
        Assert.Equal("INBOX", service.Account!.Folder);
    }

    [Fact]
    public async Task Unencrypted_connection_is_only_allowed_to_this_computer()
    {
        var service = Create();
        var error = await Assert.ThrowsAsync<EmailUnavailableException>(() => service.ConfigureAsync(
            Account() with { Server = "imap.example.com" }, "x", CancellationToken.None));
        Assert.Contains("только к серверу на этом компьютере", error.Message);
    }

    [Fact]
    public async Task Dropped_connection_is_reopened_at_once_and_a_dead_server_is_an_error()
    {
        var service = await ConnectedAsync();
        var logins = _server.Logins;
        _server.Deliver(FakeImapServer.Message("a@site.ru", "Во время обрыва", "1"));
        // Сервер закрыл долгое соединение — обычное дело: Pepa подключается снова в ту же проверку, без «нет связи».
        _server.DropConnections();
        await service.CheckAsync(CancellationToken.None);
        Assert.Equal(EmailState.Connected, service.Status.State);
        Assert.Equal("Во время обрыва", Assert.Single(_events).Payload!["subject"]!.GetValue<string>());
        Assert.Equal(logins + 1, _server.Logins);

        // Сервер не принимает пароль (его отозвали) — ошибка, и ждать письма с ней бессмысленно.
        _server.Password = "отозван";
        _server.DropConnections();
        await Assert.ThrowsAsync<EmailUnavailableException>(() => service.CheckAsync(CancellationToken.None));
        Assert.Equal(EmailState.Error, service.Status.State);
        Assert.Contains("пароль приложения", service.Problem);

        await service.ClearAsync(CancellationToken.None);
        Assert.Equal(EmailState.NotConfigured, service.Status.State);
        Assert.False(await service.CheckAsync(CancellationToken.None));
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
