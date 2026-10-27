using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;

namespace Pepa.Adapters.Http.Tests;

/// <summary>Поведение блоков «Получить по адресу» и «Отправить запрос»: что отправляется и как понимается ответ.</summary>
public sealed class HttpNodeTests
{
    private static NodeExecutionContext Context(object input, JsonObject? checkpoint = null, JsonObject? previous = null) => new()
    {
        ProcessId = "p",
        Node = new NodeDefinition { Id = "n", Type = "test" },
        Input = (JsonObject)JsonSerializer.SerializeToNode(input, PepaJson.Options)!,
        Checkpoint = checkpoint,
        PreviousCheckpoint = previous,
        Variables = new JsonObject(),
        Attempt = 1,
        Time = TimeProvider.System,
    };

    private static async Task<NodeResult> Run(INodeExecutor node, object input, JsonObject? previous = null)
    {
        var context = Context(input, previous: previous);
        var checkpoint = await node.PrepareAsync(context, CancellationToken.None);
        return await node.ExecuteAsync(context with { Checkpoint = checkpoint }, CancellationToken.None);
    }

    private static HttpClient Client(FakeHttp fake) => HttpNodes.CreateClient(fake);

    // ───────────── Получить ─────────────

    [Fact]
    public async Task Get_saves_json_body_and_encodes_query_parameters()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"orders":[{"id":7,"name":"Пепа"}]}"""));
        var result = await Run(new HttpGetNode(Client(fake)), new
        {
            url = "https://shop.example/api/orders?status=new",
            query = new { q = "кот & пёс", tag = new[] { "a", "b" } },
            headers = new { Authorization = "Bearer t-123", X_Count = 5 },
            saveTo = "orders",
        });

        var ok = Assert.IsType<NodeResult.Success>(result);
        Assert.Equal("Пепа", ok.VariableUpdates["orders"]!["orders"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("GET https://shop.example/api/orders → 200 OK", ok.Message);
        var sent = Assert.Single(fake.Requests);
        Assert.Equal("https://shop.example/api/orders?status=new&q=%D0%BA%D0%BE%D1%82%20%26%20%D0%BF%D1%91%D1%81&tag=a&tag=b",
            sent.Uri.AbsoluteUri);
        Assert.Equal("Bearer t-123", sent.Headers["Authorization"]);
        Assert.Equal("5", sent.Headers["X_Count"]);
    }

    [Fact]
    public async Task Get_saves_full_response_when_asked()
    {
        var fake = new FakeHttp((_, _) =>
        {
            var response = FakeHttp.Text("привет");
            response.Headers.Add("X-Request-Id", "r-1");
            return response;
        });
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(Client(fake)),
            new { url = "https://a.example/", save = "full", saveTo = "page" }));

        var saved = ok.VariableUpdates["page"]!;
        Assert.Equal(200, saved["status"]!.GetValue<int>());
        Assert.True(saved["ok"]!.GetValue<bool>());
        Assert.Equal("привет", saved["body"]!.GetValue<string>());
        Assert.Equal("r-1", saved["headers"]!["x-request-id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_follows_redirects_but_keeps_keys_for_the_original_site()
    {
        var fake = new FakeHttp((request, _) => request.RequestUri!.Host switch
        {
            "a.example" when request.RequestUri.AbsolutePath == "/old" => FakeHttp.Redirect(HttpStatusCode.MovedPermanently, "/new"),
            "a.example" => FakeHttp.Redirect(HttpStatusCode.Found, "https://cdn.example/file.txt"),
            _ => FakeHttp.Text("данные"),
        });
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(Client(fake)),
            new { url = "https://a.example/old", headers = new { Authorization = "Bearer secret", Accept_Language = "ru" }, saveTo = "x" }));

        Assert.Equal("данные", ok.VariableUpdates["x"]!.GetValue<string>());
        Assert.Equal(["/old", "/new", "/file.txt"], fake.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal("Bearer secret", fake.Requests[1].Headers["Authorization"]);
        Assert.False(fake.Requests[2].Headers.ContainsKey("Authorization"));
        Assert.Equal("ru", fake.Requests[2].Headers["Accept_Language"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    public async Task Get_fails_on_unexpected_status_and_says_what_the_server_answered(HttpStatusCode status, bool retryable)
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Text("заказ не найден", status));
        var fail = Assert.IsType<NodeResult.Failure>(await Run(new HttpGetNode(Client(fake)), new { url = "https://a.example/x?token=t", saveTo = "x" }));

        Assert.Equal(retryable, fail.Retryable);
        Assert.Contains($"«https://a.example/x» ответил {(int)status}", fail.Error);
        Assert.Contains("заказ не найден", fail.Error);
        Assert.DoesNotContain("token", fail.Error);
    }

    [Fact]
    public async Task Get_accepts_listed_statuses()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"error":"нет"}""", HttpStatusCode.NotFound));
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(Client(fake)),
            new { url = "https://a.example/x", accept = "2xx, 404", save = "full", saveTo = "x" }));
        Assert.Equal(404, ok.VariableUpdates["x"]!["status"]!.GetValue<int>());
        Assert.False(ok.VariableUpdates["x"]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Get_refuses_binary_and_too_large_bodies()
    {
        var binary = new FakeHttp((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("application/pdf") } },
        });
        var fail = Assert.IsType<NodeResult.Failure>(await Run(new HttpGetNode(Client(binary)), new { url = "https://a.example/r.pdf", saveTo = "x" }));
        Assert.Contains("не текст (application/pdf", fail.Error);
        Assert.False(fail.Retryable);

        var large = new FakeHttp((_, _) => FakeHttp.Text(new string('я', HttpNodes.MaxResponseBytes)));
        fail = Assert.IsType<NodeResult.Failure>(await Run(new HttpGetNode(Client(large)), new { url = "https://a.example/big", saveTo = "x" }));
        Assert.Contains("больше 5 МБ", fail.Error);
    }

    [Fact]
    public async Task Get_decodes_the_charset_of_the_page()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fake = new FakeHttp((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.GetEncoding(1251).GetBytes("Привет из 1251"))
            {
                Headers = { ContentType = new("text/html") { CharSet = "windows-1251" } },
            },
        });
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(Client(fake)), new { url = "https://a.example/", saveTo = "x" }));
        Assert.Equal("Привет из 1251", ok.VariableUpdates["x"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ftp://a.example/file", "не адрес сайта")]
    [InlineData("a.example/x", "не адрес сайта")]
    public async Task Get_with_a_wrong_address_does_not_send_anything(string url, string message)
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Text("x"));
        var error = await Assert.ThrowsAsync<NodeInputException>(() => Run(new HttpGetNode(Client(fake)), new { url, saveTo = "x" }));
        Assert.Contains(message, error.Message);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Header_with_a_line_break_is_refused_before_sending()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Text("x"));
        await Assert.ThrowsAsync<NodeInputException>(() =>
            Run(new HttpGetNode(Client(fake)), new { url = "https://a.example/", headers = new { X = "a\r\nInjected: 1" }, saveTo = "x" }));
        Assert.Empty(fake.Requests);
    }

    // ───────────── Отправить ─────────────

    [Fact]
    public async Task Send_posts_json_and_saves_the_answer()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"id":42}""", HttpStatusCode.Created));
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpSendNode(Client(fake)), new
        {
            method = "POST", url = "https://shop.example/api/orders", bodyType = "json",
            body = new { name = "Пепа", amount = 100 }, saveTo = "order",
        }));

        Assert.Equal(42, ok.VariableUpdates["order"]!["id"]!.GetValue<int>());
        Assert.Equal("POST https://shop.example/api/orders → 201 Created.", ok.Message);
        var sent = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("Пепа", JsonNode.Parse(sent.Body!)!["name"]!.GetValue<string>());
        Assert.StartsWith("application/json", sent.ContentType);
        Assert.False(sent.Headers.ContainsKey(HttpSendNode.IdempotencyHeader));
    }

    [Fact]
    public async Task Send_form_and_text_bodies()
    {
        var fake = new FakeHttp((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.IsType<NodeResult.Success>(await Run(new HttpSendNode(Client(fake)),
            new { url = "https://a.example/form", bodyType = "form", body = new { login = "иван", remember = true } }));
        Assert.IsType<NodeResult.Success>(await Run(new HttpSendNode(Client(fake)),
            new { method = "PUT", url = "https://a.example/note", bodyType = "text", text = "строка" }));
        Assert.IsType<NodeResult.Success>(await Run(new HttpSendNode(Client(fake)),
            new { method = "DELETE", url = "https://a.example/note/1", bodyType = "none" }));

        Assert.Equal("login=%D0%B8%D0%B2%D0%B0%D0%BD&remember=true", fake.Requests[0].Body);
        Assert.StartsWith("application/x-www-form-urlencoded", fake.Requests[0].ContentType);
        Assert.Equal("строка", fake.Requests[1].Body);
        Assert.Equal(HttpMethod.Put, fake.Requests[1].Method);
        Assert.Null(fake.Requests[2].Body);
        Assert.Equal(HttpMethod.Delete, fake.Requests[2].Method);
    }

    [Fact]
    public async Task Rejected_request_is_a_failure_and_server_error_is_unknown()
    {
        var node = new HttpSendNode(Client(new FakeHttp((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/bad" => FakeHttp.Json("""{"error":"amount must be positive"}""", HttpStatusCode.UnprocessableEntity),
            "/busy" => FakeHttp.Text("slow down", HttpStatusCode.TooManyRequests),
            "/moved" => FakeHttp.Redirect(HttpStatusCode.PermanentRedirect, "https://a.example/v2/orders"),
            "/done" => FakeHttp.Redirect(HttpStatusCode.SeeOther, "/orders/42"),
            _ => FakeHttp.Text("internal error", HttpStatusCode.InternalServerError),
        })));

        var bad = Assert.IsType<NodeResult.Failure>(await Run(node, new { url = "https://a.example/bad", body = new { amount = -1 } }));
        Assert.False(bad.Retryable);
        Assert.Contains("Сервер отклонил запрос: 422", bad.Error);
        Assert.Contains("amount must be positive", bad.Error);

        Assert.True(Assert.IsType<NodeResult.Failure>(await Run(node, new { url = "https://a.example/busy" })).Retryable);

        var moved = Assert.IsType<NodeResult.Failure>(await Run(node, new { url = "https://a.example/moved" }));
        Assert.False(moved.Retryable);
        Assert.Contains("https://a.example/v2/orders", moved.Error);

        var done = Assert.IsType<NodeResult.Unknown>(await Run(node, new { url = "https://a.example/done" }));
        Assert.True(done.AlreadyChecked);
        Assert.Contains("добавьте 303", done.Reason);

        var error = Assert.IsType<NodeResult.Unknown>(await Run(node, new { url = "https://a.example/orders" }));
        Assert.Contains("500", error.Reason);
        Assert.Contains("неизвестно", error.Reason);
    }

    [Fact]
    public async Task Request_that_never_reached_the_server_is_a_safe_failure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var fail = Assert.IsType<NodeResult.Failure>(await Run(new HttpSendNode(HttpNodes.CreateClient()),
            new { url = $"http://127.0.0.1:{port}/orders", body = new { a = 1 } }));
        Assert.True(fail.Retryable);
        Assert.Contains("не отправлен", fail.Error);
    }

    [Fact]
    public async Task Lost_answer_is_unknown_unless_the_server_takes_an_idempotency_key()
    {
        var node = new HttpSendNode(Client(new FakeHttp((_, _) =>
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."))));
        var unknown = Assert.IsType<NodeResult.Unknown>(await Run(node, new { url = "https://a.example/orders", body = new { a = 1 } }));
        Assert.Contains("ответ не получен", unknown.Reason);
        Assert.False(node.Descriptor.SupportsVerification);

        var safe = Assert.IsType<NodeResult.Failure>(await Run(node, new { url = "https://a.example/orders", body = new { a = 1 }, idempotent = true }));
        Assert.True(safe.Retryable);
        Assert.Contains("Повтор безопасен", safe.Error);
    }

    [Fact]
    public async Task Idempotency_key_is_kept_between_attempts_of_the_same_request()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("{}"));
        var node = new HttpSendNode(Client(fake));
        var input = new { url = "https://pay.example/charges", body = new { amount = 100 }, idempotent = true };
        var first = await node.PrepareAsync(Context(input), CancellationToken.None);
        await node.ExecuteAsync(Context(input, first), CancellationToken.None);
        var second = await node.PrepareAsync(Context(input, previous: first), CancellationToken.None);
        await node.ExecuteAsync(Context(input, second), CancellationToken.None);
        var changed = new { url = "https://pay.example/charges", body = new { amount = 200 }, idempotent = true };
        var third = await node.PrepareAsync(Context(changed, previous: second), CancellationToken.None);

        var keys = fake.Requests.Select(r => r.Headers[HttpSendNode.IdempotencyHeader]).ToList();
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[0], third!.GetString("key"));
    }

    // ───────────── Проверка схемы ─────────────

    [Theory]
    [InlineData("2xx", 204, true)]
    [InlineData("2xx, 404", 404, true)]
    [InlineData("200-204", 205, false)]
    [InlineData("*", 503, true)]
    [InlineData("", 201, true)]
    [InlineData("", 301, false)]
    public void Status_sets(string text, int status, bool contains)
    {
        Assert.True(StatusSet.TryParse(text, out var set, out _));
        Assert.Equal(contains, set.Contains(status));
    }

    [Fact]
    public void Wrong_status_list_and_address_are_reported_in_the_constructor()
    {
        var node = new HttpGetNode(HttpNodes.CreateClient());
        var issues = node.Validate(new NodeDefinition
        {
            Id = "n", Type = HttpGetNode.TypeName,
            Config = new JsonObject { ["url"] = "example.com/api", ["accept"] = "2xx, ok" },
        }).ToList();
        Assert.Contains(issues, i => i.Field == "accept" && i.Message.Contains("«ok»", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Field == "url");
        Assert.Empty(node.Validate(new NodeDefinition
        {
            Id = "n", Type = HttpGetNode.TypeName, Config = new JsonObject { ["url"] = "{{ vars.base }}/api", ["accept"] = "2xx" },
        }));
    }
}
