using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Email.Tests;

/// <summary>«Ждать письмо» в настоящем движке: код из письма, письмо своему процессу, письмо, пришедшее без Pepa.</summary>
public sealed class EmailScenarioTests : IAsyncDisposable
{
    private readonly FakeImapServer _server = new();
    private EmailService? _email;

    private async Task<EngineHarness> CreateAsync(bool configure = true)
    {
        EngineHarness? harness = null;
        harness = await EngineHarness.CreateAsync(extraNodes: registry => registry.RegisterEmailNodes(() => _email is null ? "почта не создана" : _email.Problem));
        _email = new EmailService(harness.Directory, input => harness.Engine.PublishEventAsync(input),
            new EmailServiceOptions { OperationTimeout = TimeSpan.FromSeconds(10) });
        if (configure)
        {
            await _email.ConfigureAsync(new EmailAccount
            {
                Server = "127.0.0.1", Port = _server.Port, Security = EmailSecurity.None, Login = _server.Login,
            }, _server.Password, CancellationToken.None);
            _email.Start(DateTimeOffset.UtcNow, runLoop: false);
            await _email.CheckAsync(CancellationToken.None);
        }

        return harness;
    }

    private static ScenarioDefinition Confirm(object wait) => new ScenarioBuilder("Подтверждение почты")
        .Variable("mail", VariableType.Any)
        .Variable("code", VariableType.Text)
        .Node("start", "start")
        .Node("wait", "email.wait", wait)
        .Node("code", "text.find", new { text = "{{ vars.mail.payload.text }}", saveTo = "code" })
        .Node("end", "end", new { message = "код {{ vars.code }}" })
        .Node("late", "end", new { outcome = "failure", message = "письма нет" })
        .Edge("start", "wait").Edge("wait", "code", Ports.Event).Edge("wait", "late", Ports.Timeout)
        .Edge("code", "end", Ports.True)
        .Build();

    [Fact]
    public async Task Code_from_the_confirmation_mail_continues_the_process()
    {
        await using var h = await CreateAsync();
        var id = await h.RunAsync(Confirm(new { from = "site.ru", subject = "код", timeoutSeconds = 600, saveTo = "mail" }));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        _server.Deliver(FakeImapServer.Message("news@other.ru", "Рассылка: код скидки", "12345"));
        _server.Deliver(FakeImapServer.Message("Сайт <noreply@site.ru>", "Ваш код", "Код для входа: 482913. Никому не сообщайте."));
        await _email!.CheckAsync(CancellationToken.None);

        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("код 482913", done.StatusMessage);
    }

    [Fact]
    public async Task Each_process_gets_the_mail_sent_to_its_own_address()
    {
        await using var h = await CreateAsync();
        var scenario = Confirm(new { to = "{{ vars.address }}", saveTo = "mail" }) with { };
        scenario = scenario with
        {
            Variables = [.. scenario.Variables, new VariableDefinition { Name = "address", Type = VariableType.Text, IsInput = true }],
        };
        var scenarioId = await h.CreateScenarioAsync(scenario);
        var anna = await h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["address"] = "anna+shop@example.com" });
        var boris = await h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["address"] = "boris+shop@example.com" });
        await h.WaitForStatusAsync(anna, ProcessStatus.Waiting);
        await h.WaitForStatusAsync(boris, ProcessStatus.Waiting);

        _server.Deliver(FakeImapServer.Message("noreply@shop.ru", "Код", "код 2222", to: "boris+shop@example.com"));
        _server.Deliver(FakeImapServer.Message("noreply@shop.ru", "Код", "код 1111", to: "Anna+Shop@example.com"));
        await _email!.CheckAsync(CancellationToken.None);

        Assert.Equal("код 1111", (await h.WaitForStatusAsync(anna, ProcessStatus.Completed)).StatusMessage);
        Assert.Equal("код 2222", (await h.WaitForStatusAsync(boris, ProcessStatus.Completed)).StatusMessage);
    }

    [Fact]
    public async Task Without_a_mailbox_the_wait_stops_with_the_reason_and_an_empty_filter_is_an_error()
    {
        await using var h = await CreateAsync(configure: false);
        var id = await h.RunAsync(Confirm(new { from = "site.ru", saveTo = "mail" }));
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("Почта не подключена", stopped.StatusMessage);

        await _email!.ConfigureAsync(new EmailAccount
        {
            Server = "127.0.0.1", Port = _server.Port, Security = EmailSecurity.None, Login = _server.Login,
        }, _server.Password, CancellationToken.None);
        var empty = await h.RunAsync(Confirm(new { to = "{{ vars.nobody }}", saveTo = "mail" }) with
        {
            Variables = [new VariableDefinition { Name = "mail", Type = VariableType.Any }, new VariableDefinition { Name = "code", Type = VariableType.Text },
                new VariableDefinition { Name = "nobody", Type = VariableType.Text }],
        });
        Assert.Contains("получилось пустым", (await h.WaitForStatusAsync(empty, ProcessStatus.NeedsAttention)).StatusMessage);
    }

    [Fact]
    public async Task Sender_address_and_site_are_checked_by_the_address_not_by_the_name_anyone_can_write()
    {
        await using var h = await CreateAsync();
        var byAddress = await h.RunAsync(Confirm(new { from = "noreply@bank.ru", saveTo = "mail" }));
        var bySite = await h.RunAsync(Confirm(new { from = "site.ru", saveTo = "mail" }));
        await h.WaitForStatusAsync(byAddress, ProcessStatus.Waiting);
        await h.WaitForStatusAsync(bySite, ProcessStatus.Waiting);

        _server.Deliver(FakeImapServer.Message("\"noreply@bank.ru\" <x@evil.example>", "Код", "код 6666"));
        _server.Deliver(FakeImapServer.Message("noreply@evil-site.ru", "Код", "код 7777"));
        _server.Deliver(FakeImapServer.Message("noreply@site.ru.evil.com", "Код", "код 8888"));
        await _email!.CheckAsync(CancellationToken.None);
        await h.IdleAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(byAddress)).Status);
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(bySite)).Status);

        _server.Deliver(FakeImapServer.Message("Банк <NoReply@Bank.ru>", "Код", "код 1111"));
        _server.Deliver(FakeImapServer.Message("robot@mail.site.ru", "Код", "код 2222"));
        await _email.CheckAsync(CancellationToken.None);
        Assert.Equal("код 1111", (await h.WaitForStatusAsync(byAddress, ProcessStatus.Completed)).StatusMessage);
        Assert.Equal("код 2222", (await h.WaitForStatusAsync(bySite, ProcessStatus.Completed)).StatusMessage);
    }

    [Fact]
    public async Task Waiting_for_mail_with_a_rejected_password_stops_with_the_reason()
    {
        await using var h = await CreateAsync();
        _server.Password = "отозван";
        _server.DropConnections();
        await Assert.ThrowsAsync<EmailUnavailableException>(() => _email!.CheckAsync(CancellationToken.None));

        var id = await h.RunAsync(Confirm(new { subject = "код", saveTo = "mail" }));
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("пароль приложения", stopped.StatusMessage);
    }

    public async ValueTask DisposeAsync()
    {
        if (_email is not null)
        {
            await _email.DisposeAsync();
        }

        await _server.DisposeAsync();
    }
}
