using System.Net;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Http.Tests;

/// <summary>Запросы внутри настоящего движка: повтор только там, где он безопасен, и секреты не в истории.</summary>
public sealed class HttpScenarioTests
{
    private static Task<EngineHarness> CreateAsync(FakeHttp fake, EngineOptions? options = null) =>
        EngineHarness.CreateAsync(options, extraNodes: registry => registry.RegisterHttpNodes(HttpNodes.CreateClient(fake)));

    [Fact]
    public async Task Get_then_send_with_data_from_the_answer()
    {
        var fake = new FakeHttp((request, _) => request.Method == HttpMethod.Get
            ? FakeHttp.Json("""{"rate":91.5}""")
            : FakeHttp.Json("""{"id":"ord-1"}""", HttpStatusCode.Created));
        await using var h = await CreateAsync(fake);
        var id = await h.RunAsync(new ScenarioBuilder("Заказ")
            .Variable("rate", VariableType.Any)
            .Variable("order", VariableType.Any)
            .Node("start", "start")
            .Node("get", "http.get", new { url = "https://rates.example/usd", saveTo = "rate" })
            .Node("send", "http.send", new { url = "https://shop.example/orders", body = "({ price: vars.rate.rate * 2 })", saveTo = "order" })
            .Node("end", "end", new { message = "заказ {{ vars.order.id }}" })
            .Chain("start", "get", "send", "end").Build());
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("заказ ord-1", done.StatusMessage);
        Assert.Equal(183, JsonNode.Parse(fake.Requests[1].Body!)!["price"]!.GetValue<double>());
    }

    [Fact]
    public async Task Server_error_after_sending_stops_the_process_without_a_second_request()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Text("oops", HttpStatusCode.InternalServerError));
        await using var h = await CreateAsync(fake);
        var id = await h.RunAsync(new ScenarioBuilder("Оплата")
            .Node("start", "start")
            .Node("pay", "http.send", new { url = "https://pay.example/charges", body = "({ amount: 100 })" }, new NodeSettings { RetryCount = 3 })
            .Node("end", "end")
            .Chain("start", "pay", "end").Build());
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Single(fake.Requests);
        Assert.Contains("500", stopped.StatusMessage);
        Assert.Contains("неизвестно", stopped.StatusMessage);
        Assert.Contains(ProcessAction.MarkSucceeded, h.Engine.GetAllowedActions(stopped, await h.Store.GetTokensAsync([id])));
    }

    [Fact]
    public async Task With_an_idempotency_key_the_same_request_is_repeated_and_the_server_sees_one_key()
    {
        var calls = 0;
        var fake = new FakeHttp((_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.")
            : FakeHttp.Json("""{"id":"ch_1"}"""));
        await using var h = await CreateAsync(fake);
        var id = await h.RunAsync(new ScenarioBuilder("Оплата")
            .Variable("charge", VariableType.Any)
            .Node("start", "start")
            .Node("pay", "http.send", new { url = "https://pay.example/charges", body = "({ amount: 100 })", idempotent = true, saveTo = "charge" },
                new NodeSettings { RetryCount = 1, RetryDelaySeconds = 1 })
            .Node("end", "end")
            .Chain("start", "pay", "end").Build());
        var done = await h.WaitForAsync(id, p => p.Status == ProcessStatus.Completed, advance: TimeSpan.FromSeconds(1));

        Assert.Equal("ch_1", done.Variables["charge"]!["id"]!.GetValue<string>());
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(fake.Requests[0].Headers[HttpSendNode.IdempotencyHeader], fake.Requests[1].Headers[HttpSendNode.IdempotencyHeader]);
    }

    [Fact]
    public async Task Answer_that_takes_too_long_is_unknown_and_not_repeated()
    {
        var fake = FakeHttp.Slow(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeHttp.Json("{}");
        });
        await using var h = await CreateAsync(fake);
        var id = await h.RunAsync(new ScenarioBuilder("Медленно")
            .Node("start", "start")
            .Node("send", "http.send", new { url = "https://slow.example/orders", body = "({ a: 1 })" },
                new NodeSettings { TimeoutSeconds = 1, RetryCount = 2 })
            .Node("end", "end")
            .Chain("start", "send", "end").Build());
        var stopped = await h.WaitForAsync(id, p => p.Status == ProcessStatus.NeedsAttention, advance: TimeSpan.FromSeconds(1));

        Assert.Single(fake.Requests);
        Assert.Contains("неизвестно", stopped.StatusMessage);
    }

    [Fact]
    public async Task Failed_get_is_retried_and_rejected_send_goes_to_the_error_port()
    {
        var gets = 0;
        var fake = new FakeHttp((request, _) => request.Method == HttpMethod.Get
            ? Interlocked.Increment(ref gets) == 1 ? FakeHttp.Text("busy", HttpStatusCode.ServiceUnavailable) : FakeHttp.Json("[]")
            : FakeHttp.Json("""{"error":"bad"}""", HttpStatusCode.BadRequest));
        await using var h = await CreateAsync(fake);
        var id = await h.RunAsync(new ScenarioBuilder("Ошибки")
            .Variable("list", VariableType.Any)
            .Node("start", "start")
            .Node("get", "http.get", new { url = "https://a.example/list", saveTo = "list" }, new NodeSettings { RetryCount = 1, RetryDelaySeconds = 1 })
            .Node("send", "http.send", new { url = "https://a.example/orders", body = "({})" })
            .Node("ok", "end")
            .Node("failed", "end", new { outcome = "failure", message = "отклонено" })
            .Chain("start", "get", "send", "ok").Edge("send", "failed", Ports.Error).Build());
        var done = await h.WaitForAsync(id, p => p.IsTerminal, advance: TimeSpan.FromSeconds(1));

        Assert.Equal(ProcessStatus.Failed, done.Status);
        Assert.Equal(2, gets);
        Assert.Single(fake.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Secret_token_in_headers_and_answer_stays_out_of_history()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"echo":"tok-SECRET-77"}"""));
        await using var h = await CreateAsync(fake);
        var scenario = new ScenarioBuilder("Ключ")
            .Variable("token", VariableType.Text, isInput: true, secret: true)
            .Variable("answer", VariableType.Any)
            .Node("start", "start")
            .Node("get", "http.get", new { url = "https://api.example/me", headers = "({ Authorization: 'Bearer ' + vars.token })", save = "full", saveTo = "answer" })
            .Node("end", "end")
            .Chain("start", "get", "end").Build();
        var id = await h.RunAsync(scenario, new JsonObject { ["token"] = "tok-SECRET-77" });
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("Bearer tok-SECRET-77", Assert.Single(fake.Requests).Headers["Authorization"]);
        var masker = new SecretMasker(done, scenario);
        var shown = string.Join("\n", (await h.StepsAsync(id)).Select(masker.Step).Select(s => $"{s.Input?.ToJsonString()} {s.Output?.ToJsonString()}"));
        Assert.DoesNotContain("tok-SECRET-77", shown);
        Assert.DoesNotContain("SECRET", string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message)));
    }

    [Fact]
    public async Task With_a_key_a_definitive_failure_after_a_lost_answer_is_unknown_but_two_refused_connections_are_an_error()
    {
        var calls = 0;
        var fake = new FakeHttp((_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.")
            : throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        await using var h = await CreateAsync(fake);
        ScenarioDefinition Pay(string name) => new ScenarioBuilder(name)
            .Node("start", "start")
            .Node("pay", "http.send", new { url = "https://pay.example/charges", body = "({ amount: 100 })", idempotent = true },
                new NodeSettings { RetryCount = 1, RetryDelaySeconds = 0 })
            .Node("end", "end")
            .Node("failed", "end", new { outcome = "failure", message = "ветка «Ошибка»" })
            .Chain("start", "pay", "end").Edge("pay", "failed", Ports.Error).Build();

        // Ответ потерялся (могло выполниться), затем «не отправлен» — это не «не выполнено».
        var lost = await h.RunAsync(Pay("Потерян ответ"));
        var stopped = await h.WaitForStatusAsync(lost, ProcessStatus.NeedsAttention);
        Assert.Contains("Прошлая попытка", stopped.StatusMessage);

        // Оба раза соединение не установилось — запрос точно не уходил: ветка «Ошибка».
        calls = 10;
        var refused = await h.RunAsync(Pay("Нет связи"));
        Assert.Equal("ветка «Ошибка»", (await h.WaitForAsync(refused, p => p.IsTerminal)).StatusMessage);
    }
}

