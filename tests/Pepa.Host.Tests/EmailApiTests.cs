using System.Net;
using System.Text.Json.Nodes;
using Pepa.Adapters.Email.Tests;

namespace Pepa.Host.Tests;

/// <summary>Почта в собранном приложении: подключение в «Интеграциях» и «Ждать письмо» в процессе.</summary>
public sealed class EmailApiTests
{
    [Fact]
    public async Task Mailbox_is_connected_from_integrations_and_a_process_gets_the_code_from_a_new_mail()
    {
        await using var imap = new FakeImapServer();
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var account = new { server = "127.0.0.1", port = imap.Port, security = "none", login = imap.Login, folder = "INBOX" };

        var (_, wrong) = await f.SendAsync(HttpMethod.Post, "/api/integrations/email/check", new { account.server, account.port, account.security, account.login, password = "x" });
        Assert.False(wrong!["ok"]!.GetValue<bool>());
        Assert.Contains("пароль приложения", wrong["message"]!.GetValue<string>());
        var (status, saved) = await f.SendAsync(HttpMethod.Put, "/api/integrations/email",
            new { account.server, account.port, account.security, account.login, account.folder, password = imap.Password });
        Assert.Equal((int)HttpStatusCode.OK, status);
        Assert.Equal(imap.Login, saved!["account"]!["login"]!.GetValue<string>());
        Assert.DoesNotContain(imap.Password, saved.ToJsonString());

        JsonNode email = saved;
        for (var i = 0; i < 100 && email["state"]!.GetValue<string>() != "connected"; i++)
        {
            await Task.Delay(100);
            email = await f.GetAsync("/api/integrations/email");
        }

        Assert.Equal("connected", email["state"]!.GetValue<string>());
        var types = (await f.GetAsync("/api/node-types"))["nodeTypes"]!.AsArray();
        Assert.Contains(types, t => t!["type"]!.GetValue<string>() == "email.wait");

        var scenario = new
        {
            name = "Код из письма",
            variables = new object[]
            {
                new { name = "mail", type = "any" },
                new { name = "code", type = "text" },
            },
            nodes = new object[]
            {
                new { id = "start", type = "start", position = new { x = 0, y = 0 }, config = new { } },
                new { id = "wait", type = "email.wait", position = new { x = 200, y = 0 }, config = new { subject = "код", saveTo = "mail", timeoutSeconds = 600 } },
                new { id = "find", type = "text.find", position = new { x = 400, y = 0 }, config = new { text = "{{ vars.mail.payload.text }}", saveTo = "code" } },
                new { id = "end", type = "end", position = new { x = 600, y = 0 }, config = new { message = "код {{ vars.code }}" } },
            },
            edges = new object[]
            {
                new { id = "e1", source = "start", sourcePort = "out", target = "wait" },
                new { id = "e2", source = "wait", sourcePort = "event", target = "find" },
                new { id = "e3", source = "find", sourcePort = "true", target = "end" },
            },
        };
        var (_, created) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", scenario);
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{created!["id"]!.GetValue<string>()}/run", new { });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");

        imap.Deliver(FakeImapServer.Message("noreply@site.ru", "Ваш код", "Код подтверждения: 739104"));
        var done = (await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "completed"))["process"]!;
        Assert.Equal("код 739104", done["statusMessage"]!.GetValue<string>());

        var (_, cleared) = await f.SendAsync(HttpMethod.Delete, "/api/integrations/email");
        Assert.Equal("notConfigured", cleared!["state"]!.GetValue<string>());
    }
}
