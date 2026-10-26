using System.Text.Json.Nodes;
using Pepa.Assistant.Tests;

namespace Pepa.Host.Tests;

/// <summary>ИИ-помощник через API: ключ хранится и не отдаётся, черновик собирается из каталога блоков Pepa.</summary>
public sealed class AssistantApiTests
{
    [Fact]
    public async Task Key_is_stored_without_being_returned_and_draft_comes_back_as_blocks()
    {
        using var api = new FakeAnthropic();
        await using var f = await HostFixture.StartAsync(seedSamples: false, assistantUrl: api.Url);

        // Без ключа помощник честно говорит, что делать.
        Assert.False((await f.GetAsync("/api/assistant"))["configured"]!.GetValue<bool>());
        var (status, error) = await f.SendAsync(HttpMethod.Post, "/api/assistant/draft", new { description = "Написать в журнал «Привет»" });
        Assert.Equal(409, status);
        Assert.Contains("Ключ API Anthropic не задан", error!["error"]!.GetValue<string>());
        Assert.Empty(api.Requests);

        Assert.Equal(400, (await f.SendAsync(HttpMethod.Put, "/api/assistant/key", new { apiKey = "не ключ" })).Status);
        var (saved, state) = await f.SendAsync(HttpMethod.Put, "/api/assistant/key", new { apiKey = "sk-ant-api03-abcdefghijklmnop-WXYZ" });
        Assert.Equal(200, saved);
        Assert.Equal("…WXYZ", state!["keyHint"]!.GetValue<string>());
        Assert.DoesNotContain("abcdefghijklmnop", state.ToJsonString());
        // В базе ключ не лежит открытым текстом там, где Windows шифрует (DPAPI); наружу — только подсказка.
        Assert.DoesNotContain("abcdefghijklmnop", (await f.GetAsync("/api/assistant")).ToJsonString());

        api.ReplyDraft(new JsonObject
        {
            ["summary"] = "Пишет приветствие в журнал.",
            ["questions"] = new JsonArray(),
            ["variables"] = new JsonArray(),
            ["blocks"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "hello", ["type"] = "log", ["label"] = "Привет", ["note"] = "",
                    ["settings"] = new JsonArray(new JsonObject { ["field"] = "message", ["value"] = "Привет" }),
                },
                new JsonObject { ["id"] = "end", ["type"] = "end", ["label"] = "", ["note"] = "", ["settings"] = new JsonArray() }),
            ["links"] = new JsonArray(new JsonObject { ["from"] = "hello", ["port"] = "out", ["to"] = "end" }),
        });
        var (ok, draft) = await f.SendAsync(HttpMethod.Post, "/api/assistant/draft", new
        {
            description = "Написать в журнал «Привет»",
            variables = new[] { new { name = "name", type = "text", description = "Имя", isInput = true } },
        });
        Assert.Equal(200, ok);
        Assert.Equal("hello", draft!["entryNodeId"]!.GetValue<string>());
        Assert.Equal(2, draft["nodes"]!.AsArray().Count);
        Assert.Equal("Привет", draft["nodes"]![0]!["config"]!["message"]!.GetValue<string>());
        Assert.Empty(draft["problems"]!.AsArray());
        var request = api.Requests.Single();
        Assert.Equal("Bearer sk-ant-api03-abcdefghijklmnop-WXYZ", request.Headers["authorization"]);
        // Каталог — все блоки этого Pepa, включая браузер, Telegram и окна.
        var system = request.Body!["system"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("### telegram.send", system);
        Assert.Contains("### adspower.open", system);
        Assert.Contains("### desktop.click", system);

        Assert.False((await f.SendAsync(HttpMethod.Delete, "/api/assistant/key")).Body!["configured"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Scenario_is_explained_and_its_variable_values_are_not_sent()
    {
        using var api = new FakeAnthropic();
        await using var f = await HostFixture.StartAsync(seedSamples: false, assistantUrl: api.Url);
        await f.SendAsync(HttpMethod.Put, "/api/assistant/key", new { apiKey = "sk-ant-api03-abcdefghijklmnop-WXYZ" });
        Assert.Equal(400, (await f.SendAsync(HttpMethod.Post, "/api/assistant/explain", new { })).Status);

        var scenario = new
        {
            name = "Привет",
            variables = new[] { new { name = "token", type = "text", description = "Ключ сервиса", isInput = false, @default = "очень-секретное-значение" } },
            nodes = new object[]
            {
                new { id = "start", type = "start", position = new { x = 0, y = 0 } },
                new { id = "hello", type = "log", position = new { x = 300, y = 0 }, config = new { message = "Привет" } },
                new { id = "end", type = "end", position = new { x = 600, y = 0 } },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "hello" },
                new { id = "b", source = "hello", sourcePort = "out", target = "end" },
            },
        };
        api.ReplyDraft(new JsonObject
        {
            ["summary"] = "Пишет «Привет» в журнал.",
            ["steps"] = new JsonArray(new JsonObject { ["block"] = "hello", ["text"] = "Пишет приветствие." }),
            ["cautions"] = new JsonArray(),
        });
        var (ok, explanation) = await f.SendAsync(HttpMethod.Post, "/api/assistant/explain", new { scenario });
        Assert.Equal(200, ok);
        Assert.Equal("hello", explanation!["steps"]![0]!["blockId"]!.GetValue<string>());
        // Текст сообщения модели — разобранный (в JSON кириллица экранирована, и простой поиск по нему ничего не доказал бы).
        var content = api.Requests.Single().Body!["messages"]![0]!["content"]!;
        var sent = content is JsonValue value ? value.GetValue<string>() : string.Concat(content.AsArray().Select(b => b!["text"]?.GetValue<string>()));
        Assert.Contains("Ключ сервиса", sent);
        Assert.Contains("Привет", sent);
        Assert.DoesNotContain("очень-секретное-значение", sent);
    }
}
