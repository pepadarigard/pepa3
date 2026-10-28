using System.Text.Json.Nodes;
using MimeKit;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Email.Tests;

/// <summary>«Отправить письмо»: отказ сервера — ошибка, обрыв после передачи — поиск в «Отправленных», без второй отправки.</summary>
public sealed partial class EmailScenarioTests
{
    private static ScenarioDefinition Send(object config, int retries = 0) => new ScenarioBuilder("Отправить отчёт")
        .Variable("sent", VariableType.Any)
        .Node("start", "start")
        .Node("send", "email.send", config, new NodeSettings { RetryCount = retries, RetryDelaySeconds = 0 })
        .Node("end", "end", new { message = "отправлено {{ vars.sent.messageId }}" })
        .Chain("start", "send", "end")
        .Build();

    private static MimeMessage Parse(string raw) => MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)));

    [Fact]
    public async Task Mail_is_sent_from_the_mailbox_with_text_and_an_attachment()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await CreateAsync(smtp: smtp);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "отчёт.txt"), "итоги дня", TestContext.Current.CancellationToken);

        var id = await h.RunAsync(Send(new
        {
            to = "client@site.ru, Начальник <boss@site.ru>", cc = "copy@site.ru", subject = "Отчёт за день", text = "Добрый день!\nОтчёт во вложении.",
            files = "отчёт.txt", saveTo = "sent",
        }));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        var (raw, recipients) = Assert.Single(smtp.Messages);
        var message = Parse(raw);
        Assert.Equal(["client@site.ru", "boss@site.ru", "copy@site.ru"], recipients);
        Assert.Equal("anna@example.com", message.From.Mailboxes.Single().Address);
        Assert.Equal("Отчёт за день", message.Subject);
        Assert.Contains("Отчёт во вложении.", message.TextBody ?? "");
        Assert.Equal("отчёт.txt", message.Attachments.Single().ContentDisposition?.FileName);
        Assert.Equal($"отправлено {message.MessageId}", done.StatusMessage);
    }

    [Fact]
    public async Task Refused_recipient_or_letter_is_an_error_and_is_not_repeated()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await CreateAsync(smtp: smtp);
        smtp.RejectedRecipients["nobody@site.ru"] = true;

        var refused = await h.RunAsync(Send(new { to = "nobody@site.ru", subject = "Привет" }, retries: 2));
        var stopped = await h.WaitForStatusAsync(refused, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, stopped.Attention);
        Assert.Contains("не принял адрес получателя «nobody@site.ru»", stopped.StatusMessage);
        Assert.Single(await h.StepsAsync(refused), s => s.NodeId == "send");

        smtp.Plan(SmtpBehavior.RejectMessage);
        var spam = await h.RunAsync(Send(new { to = "client@site.ru", subject = "Выгодно" }, retries: 2));
        Assert.Contains("не принял письмо", (await h.WaitForStatusAsync(spam, ProcessStatus.NeedsAttention)).StatusMessage);
        Assert.Empty(smtp.Messages);
        await h.Engine.CancelAsync(refused);
        await h.Engine.CancelAsync(spam);
    }

    [Fact]
    public async Task Temporary_failure_and_a_drop_before_the_letter_are_retried_with_the_same_message_id()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await CreateAsync(smtp: smtp);
        smtp.Plan(SmtpBehavior.TemporaryFailure, SmtpBehavior.DropBeforeData);

        var id = await h.RunAsync(Send(new { to = "client@site.ru", subject = "Счёт", saveTo = "sent" }, retries: 2));
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        var steps = (await h.StepsAsync(id)).Where(s => s.NodeId == "send").OrderBy(s => s.Attempt).ToList();
        Assert.Equal(3, steps.Count);
        Assert.Equal(StepState.Failed, steps[0].State);
        Assert.Equal(StepState.Failed, steps[1].State);
        var sent = Parse(Assert.Single(smtp.Messages).Raw);
        Assert.All(steps, s => Assert.Equal(sent.MessageId, s.Checkpoint!["messageId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Drop_after_the_letter_is_resolved_by_finding_it_in_Sent_and_it_is_not_sent_again()
    {
        await using var smtp = new FakeSmtpServer();
        _server.AddFolder("Sent", "\\Sent");
        // Как Gmail: сервер сам кладёт отправленное в «Отправленные».
        smtp.Accepted = raw => _server.Deliver(raw, folder: "Sent");
        await using var h = await CreateAsync(smtp: smtp);
        smtp.Plan(SmtpBehavior.DropAfterData);

        var id = await h.RunAsync(Send(new { to = "client@site.ru", subject = "Договор", saveTo = "sent" }, retries: 2));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        var sent = Parse(Assert.Single(smtp.Messages).Raw);
        Assert.Equal($"отправлено {sent.MessageId}", done.StatusMessage);
        var step = (await h.StepsAsync(id)).Single(s => s.NodeId == "send");
        Assert.Contains("«Отправленных»", step.Resolution);
    }

    [Fact]
    public async Task Drop_after_the_letter_without_a_copy_in_Sent_waits_for_the_user()
    {
        await using var smtp = new FakeSmtpServer();
        _server.AddFolder("Отправленные");
        await using var h = await CreateAsync(smtp: smtp);
        smtp.Plan(SmtpBehavior.DropAfterData);

        var id = await h.RunAsync(Send(new { to = "client@site.ru", subject = "Договор" }, retries: 2));
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Equal(AttentionKind.UnknownStep, stopped.Attention);
        Assert.Contains("неизвестно, ушло ли оно", stopped.StatusMessage);
        Assert.Contains("в «Отправленных» этого письма нет", stopped.StatusMessage);
        Assert.Single(smtp.Messages);
        Assert.Contains(_server.Commands, c => c.Contains("HEADER", StringComparison.OrdinalIgnoreCase));
        await h.Engine.CancelAsync(id);
    }

    [Fact]
    public async Task Without_sending_settings_or_with_a_bad_address_nothing_is_sent()
    {
        await using var h = await CreateAsync();
        var off = await h.RunAsync(Send(new { to = "client@site.ru", subject = "Привет" }));
        Assert.Contains("Отправка писем не настроена", (await h.WaitForStatusAsync(off, ProcessStatus.NeedsAttention)).StatusMessage);

        var bad = await h.RunAsync(Send(new { to = "client@site.ru, просто текст", subject = "Привет" }));
        Assert.Contains("«просто текст» — не адрес почты", (await h.WaitForStatusAsync(bad, ProcessStatus.NeedsAttention)).StatusMessage);

        var missing = await h.RunAsync(Send(new { to = "client@site.ru", subject = "Привет", files = "нет-такого.pdf" }));
        Assert.Contains("нет в рабочей папке", (await h.WaitForStatusAsync(missing, ProcessStatus.NeedsAttention)).StatusMessage);
        foreach (var id in new[] { off, bad, missing })
        {
            await h.Engine.CancelAsync(id);
        }
    }

    [Fact]
    public async Task Sending_settings_are_checked_before_saving_and_kept_after_restart()
    {
        await using var smtp = new FakeSmtpServer { Password = "другой" };
        await using var h = await CreateAsync(configure: false);
        var account = new EmailAccount
        {
            Server = "127.0.0.1", Port = _server.Port, Security = EmailSecurity.None, Login = _server.Login,
            SmtpServer = "127.0.0.1", SmtpPort = smtp.Port, SmtpSecurity = EmailSecurity.None, SentFolder = "Отправленные",
        };
        var refused = await Assert.ThrowsAsync<EmailUnavailableException>(() => _email!.ConfigureAsync(account, _server.Password, CancellationToken.None));
        Assert.Contains("Сервер отправки не принял логин или пароль", refused.Message);
        Assert.Null(_email!.Account);

        smtp.Password = _server.Password;
        await _email.ConfigureAsync(account, _server.Password, CancellationToken.None);
        await using var reopened = new EmailService(Path.Combine(_root, "data"), _ => Task.CompletedTask);
        Assert.Equal((smtp.Port, EmailSecurity.None, "Отправленные"),
            (reopened.Account!.SmtpPort, reopened.Account.SmtpSecurity, reopened.Account.SentFolder));
        Assert.Null(reopened.SendProblem);
    }
}
