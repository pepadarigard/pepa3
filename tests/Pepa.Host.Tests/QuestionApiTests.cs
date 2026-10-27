using System.Net;
using System.Text.Json.Nodes;

namespace Pepa.Host.Tests;

/// <summary>Вопросы процессов в API: список без секретов, ответ вариантом или текстом один раз, картинка к вопросу.</summary>
public sealed class QuestionApiTests
{
    private static async Task<(string ProcessId, JsonNode Question)> AskAsync(HostFixture f, object config, object? inputs = null)
    {
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Вопрос",
            variables = new object[]
            {
                new { name = "answer", type = "any" },
                new { name = "token", type = "text", isInput = true, secret = true },
            },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "ask", type = "user.ask", config },
                new { id = "end", type = "end", config = new { message = "ответ {{ vars.answer }}" } },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "ask" },
                new { id = "b", source = "ask", sourcePort = "event", target = "end" },
            },
        });
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario!["id"]!.GetValue<string>()}/run",
            new { inputs = inputs ?? new { token = "tok-S3cr3t-77" } });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");
        var question = (await f.GetAsync($"/api/questions?processId={id}")).AsArray().Single()!;
        return (id, question);
    }

    [Fact]
    public async Task Question_is_listed_without_secrets_and_answered_by_option_once()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (id, question) = await AskAsync(f, new { question = "Войти с ключом {{ vars.token }}?", options = "Да\nНет", saveTo = "answer" });

        Assert.Equal("Войти с ключом ••••?", question["text"]!.GetValue<string>());
        Assert.Equal(["Да", "Нет"], question["options"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.False(question["freeText"]!.GetValue<bool>());
        Assert.Single((await f.GetAsync("/api/questions")).AsArray());
        var questionId = question["id"]!.GetValue<long>();

        var (wrong, error) = await f.SendAsync(HttpMethod.Post, $"/api/questions/{questionId}/answer", new { option = 5 });
        Assert.Equal(409, wrong);
        Assert.Contains("Такого варианта", error!["error"]!.GetValue<string>());
        var (status, first) = await f.SendAsync(HttpMethod.Post, $"/api/questions/{questionId}/answer", new { option = 1 });
        Assert.Equal(200, status);
        Assert.False(first!["duplicate"]!.GetValue<bool>());
        var (_, again) = await f.SendAsync(HttpMethod.Post, $"/api/questions/{questionId}/answer", new { option = 0 });
        Assert.True(again!["duplicate"]!.GetValue<bool>());

        var done = await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "completed");
        Assert.Equal("ответ Нет", done["process"]!["statusMessage"]!.GetValue<string>());
        Assert.Empty((await f.GetAsync("/api/questions")).AsArray());
    }

    [Fact]
    public async Task Question_image_is_served_from_the_workspace_only_as_a_picture()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        Directory.CreateDirectory(Path.Combine(f.Workspace, "снимки"));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        await File.WriteAllBytesAsync(Path.Combine(f.Workspace, "снимки", "капча.png"), png);
        var (_, question) = await AskAsync(f, new { question = "Что на картинке?", image = "снимки/капча.png", saveTo = "answer" });
        Assert.True(question["hasImage"]!.GetValue<bool>());
        Assert.True(question["freeText"]!.GetValue<bool>());

        var response = await f.Client.GetAsync($"/api/questions/{question["id"]!.GetValue<long>()}/image");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());

        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "заметка.txt"), "не картинка");
        var (_, text) = await AskAsync(f, new { question = "А это?", image = "заметка.txt", saveTo = "answer" });
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"/api/questions/{text["id"]!.GetValue<long>()}/image")).StatusCode);
        var (_, outside) = await AskAsync(f, new { question = "А так?", image = "../pepa.db", saveTo = "answer" });
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"/api/questions/{outside["id"]!.GetValue<long>()}/image")).StatusCode);
    }

    [Fact]
    public async Task Ask_and_write_sample_creates_the_report_after_two_answers()
    {
        await using var f = await HostFixture.StartAsync();
        var scenario = (await f.GetAsync("/api/scenarios")).AsArray()
            .Single(s => s!["name"]!.GetValue<string>().StartsWith("Пример 14.", StringComparison.Ordinal))!;
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario["id"]!.GetValue<string>()}/run", new { });
        var id = run!["processId"]!.GetValue<string>();

        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");
        var confirm = (await f.GetAsync($"/api/questions?processId={id}")).AsArray().Single()!;
        Assert.Equal("Создать отчёт?", confirm["text"]!.GetValue<string>());
        await f.SendAsync(HttpMethod.Post, $"/api/questions/{confirm["id"]!.GetValue<long>()}/answer", new { option = 0 });

        JsonNode? name = null;
        for (var i = 0; i < 200 && name is null; i++)
        {
            name = (await f.GetAsync($"/api/questions?processId={id}")).AsArray().FirstOrDefault(q => q!["text"]!.GetValue<string>() == "Как назвать отчёт?");
            await Task.Delay(50);
        }

        Assert.NotNull(name);
        Assert.True(name["freeText"]!.GetValue<bool>());
        await f.SendAsync(HttpMethod.Post, $"/api/questions/{name["id"]!.GetValue<long>()}/answer", new { answer = "Итоги дня" });
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "completed");
        Assert.True(File.Exists(Path.Combine(f.Workspace, "примеры", "отчёты", "Итоги дня.txt")));
    }
}

