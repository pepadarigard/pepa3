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
    private static NodeExecutionContext Context(object input, JsonObject? checkpoint = null, JsonObject? previous = null,
        int retries = 0, int attempt = 1) => new()
    {
        ProcessId = "p",
        Node = new NodeDefinition { Id = "n", Type = "test", Settings = new NodeSettings { RetryCount = retries } },
        Input = (JsonObject)JsonSerializer.SerializeToNode(input, PepaJson.Options)!,
        Checkpoint = checkpoint,
        PreviousCheckpoint = previous,
        Variables = new JsonObject(),
        Attempt = attempt,
        Time = TimeProvider.System,
    };

    private static async Task<NodeResult> Run(INodeExecutor node, object input, JsonObject? previous = null, int retries = 0, int attempt = 1)
    {
        var context = Context(input, previous: previous, retries: retries, attempt: attempt);
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
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(Client(fake)), new
        {
            url = "https://a.example/old",
            headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret", ["X-Api-Secret"] = "s-1", ["Accept-Language"] = "ru" },
            saveTo = "x",
        }));

        Assert.Equal("данные", ok.VariableUpdates["x"]!.GetValue<string>());
        Assert.Equal(["/old", "/new", "/file.txt"], fake.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal("Bearer secret", fake.Requests[1].Headers["Authorization"]);
        Assert.Equal("s-1", fake.Requests[1].Headers["X-Api-Secret"]);
        // На другой сайт — только безобидные заголовки: ключ мог быть под любым именем.
        Assert.False(fake.Requests[2].Headers.ContainsKey("Authorization"));
        Assert.False(fake.Requests[2].Headers.ContainsKey("X-Api-Secret"));
        Assert.Equal("ru", fake.Requests[2].Headers["Accept-Language"]);
    }

    [Fact]
    public async Task Get_does_not_follow_a_redirect_from_https_to_http()
    {
        var fake = new FakeHttp((request, _) => request.RequestUri!.Scheme == "https"
            ? FakeHttp.Redirect(HttpStatusCode.Found, "http://a.example/plain")
            : FakeHttp.Text("открыто"));
        var fail = Assert.IsType<NodeResult.Failure>(await Run(new HttpGetNode(Client(fake)), new { url = "https://a.example/secure", saveTo = "x" }));
        Assert.Contains("незащищённый", fail.Error);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Saved_address_has_no_query_and_messages_have_no_user_name()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("{}"));
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(Client(fake)),
            new { url = "https://api.example/me", query = new { access_token = "Ab+c/d=XYZ123" }, save = "full", saveTo = "x" }));
        Assert.Equal("https://api.example/me", ok.VariableUpdates["x"]!["url"]!.GetValue<string>());
        Assert.DoesNotContain("XYZ123", ok.Output!.ToJsonString());

        var error = await Assert.ThrowsAsync<NodeInputException>(() =>
            Run(new HttpGetNode(Client(fake)), new { url = "https://user:p%40ss@host.example/path", saveTo = "x" }));
        Assert.DoesNotContain("p%40ss", error.Message);
    }

    [Fact]
    public async Task Page_charset_from_meta_and_invalid_json_warning()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var page = new FakeHttp((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.GetEncoding(1251).GetBytes("<html><head><meta charset=\"windows-1251\"></head><body>Привет</body></html>"))
            {
                Headers = { ContentType = new("text/html") },
            },
        });
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(Client(page)), new { url = "https://a.example/", saveTo = "x" }));
        Assert.Contains("<body>Привет</body>", ok.VariableUpdates["x"]!.GetValue<string>());

        var broken = new FakeHttp((_, _) => FakeHttp.Json("{\"a\":"));
        ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(Client(broken)), new { url = "https://a.example/api", saveTo = "x" }));
        Assert.Equal(MessageLevel.Warning, ok.Level);
        Assert.Contains("не разбирается", ok.Message);
        Assert.Equal("{\"a\":", ok.VariableUpdates["x"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connect_timeout_and_non_ascii_header_mean_nothing_was_sent()
    {
        var slow = new FakeHttp((_, _) => throw new TaskCanceledException("The operation was canceled.", new TimeoutException("connect")));
        var fail = Assert.IsType<NodeResult.Failure>(await Run(new HttpSendNode(Client(slow)), new { url = "https://a.example/orders", body = new { a = 1 } }));
        Assert.True(fail.Retryable);
        Assert.Contains("не отправлен", fail.Error);
        Assert.IsType<NodeResult.Failure>(await Run(new HttpGetNode(Client(slow)), new { url = "https://a.example/list", saveTo = "x" }));

        var fake = new FakeHttp((_, _) => FakeHttp.Json("{}"));
        var error = await Assert.ThrowsAsync<NodeInputException>(() =>
            Run(new HttpSendNode(Client(fake)), new { url = "https://a.example/orders", headers = new { X_Name = "Пепа" } }));
        Assert.Contains("кириллицу", error.Message);
        Assert.Empty(fake.Requests);
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

        // С ключом от повтора — повтор («могло выполниться»), пока он остался по настройкам блока; последняя попытка — снова
        // «итог неизвестен»: обычная ошибка увела бы процесс по выходу «Ошибка», будто запрос точно не выполнен.
        var safe = Assert.IsType<NodeResult.Failure>(await Run(node, new { url = "https://a.example/orders", body = new { a = 1 }, idempotent = true }, retries: 1));
        Assert.True(safe.Retryable);
        Assert.True(safe.MaybeExecuted);
        Assert.Contains("Повтор безопасен", safe.Error);
        var last = Assert.IsType<NodeResult.Unknown>(await Run(node, new { url = "https://a.example/orders", body = new { a = 1 }, idempotent = true }, retries: 1, attempt: 2));
        Assert.Contains("Повтор безопасен", last.Reason);
        Assert.IsType<NodeResult.Unknown>(await Run(node, new { url = "https://a.example/orders", body = new { a = 1 }, idempotent = true }));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task With_an_idempotency_key_409_and_422_are_unknown_not_rejected(HttpStatusCode status)
    {
        var node = new HttpSendNode(Client(new FakeHttp((_, _) => FakeHttp.Json("""{"error":"key in use"}""", status))));
        var unknown = Assert.IsType<NodeResult.Unknown>(await Run(node, new { url = "https://pay.example/charges", body = new { a = 1 }, idempotent = true }));
        Assert.Contains("ключ", unknown.Reason);
        Assert.False(Assert.IsType<NodeResult.Failure>(await Run(node, new { url = "https://pay.example/charges", body = new { a = 1 } })).Retryable);
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
        // Данные посчитались иначе (время, случайное число) — ключ тот же: новый сервер принял бы за новый запрос.
        var changed = new { url = "https://pay.example/charges", body = new { amount = 100, at = DateTime.UtcNow.Ticks }, idempotent = true };
        var third = await node.PrepareAsync(Context(changed, previous: second), CancellationToken.None);

        var keys = fake.Requests.Select(r => r.Headers[HttpSendNode.IdempotencyHeader]).ToList();
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(keys[0], third!.GetString("key"));
        // Новый заход в блок (не повтор) — новый ключ.
        Assert.NotEqual(keys[0], (await node.PrepareAsync(Context(input), CancellationToken.None))!.GetString("key"));
    }

    [Fact]
    public async Task Cookies_of_one_request_do_not_go_with_the_next()
    {
        using var server = await LocalServer.StartAsync(request => request.Contains("GET /login", StringComparison.Ordinal)
            ? "HTTP/1.1 200 OK\r\nSet-Cookie: sid=USER-A\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"
            : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok");
        var client = HttpNodes.CreateClient();
        await Run(new HttpGetNode(client), new { url = $"{server.Url}/login", saveTo = "x" });
        await Run(new HttpGetNode(client), new { url = $"{server.Url}/me", saveTo = "x" });

        Assert.Equal(2, server.Requests.Count);
        Assert.DoesNotContain("sid=USER-A", server.Requests[1]);
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

    [Fact]
    public async Task Cookie_set_during_redirects_is_carried_to_the_next_hop()
    {
        using var server = await LocalServer.StartAsync(request => request.StartsWith("GET /login", StringComparison.Ordinal)
            ? "HTTP/1.1 302 Found\r\nLocation: /home\r\nSet-Cookie: sid=S-1; Path=/\r\nContent-Length: 0\r\n\r\n"
            : request.Contains("sid=S-1", StringComparison.Ordinal)
                ? "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 4\r\n\r\nhome"
                : "HTTP/1.1 302 Found\r\nLocation: /login\r\nContent-Length: 0\r\n\r\n");
        var ok = Assert.IsType<NodeResult.Success>(await Run(new HttpGetNode(HttpNodes.CreateClient()), new { url = $"{server.Url}/login", saveTo = "x" }));
        Assert.Equal("home", ok.VariableUpdates["x"]!.GetValue<string>());
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Cookie_with_a_domain_stays_with_its_site_and_a_renewed_cookie_replaces_the_old_one()
    {
        using var server = await LocalServer.StartAsync(request => request.StartsWith("GET /login", StringComparison.Ordinal)
            ? "HTTP/1.1 302 Found\r\nLocation: /home\r\nSet-Cookie: wide=W; Domain=example.com; Path=/\r\nSet-Cookie: session=new; Path=/\r\nContent-Length: 0\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok");
        await Run(new HttpGetNode(HttpNodes.CreateClient()), new { url = $"{server.Url}/login", headers = new { Cookie = "session=old; theme=dark" }, saveTo = "x" });

        // Кука с «Domain=» — только тому сайту, что её поставил (здесь — тому же).
        var home = server.Requests[1];
        Assert.Contains("wide=W", home);
        Assert.DoesNotContain("session=old", home);
        Assert.Contains("session=new", home);
        Assert.Contains("theme=dark", home);
    }
}

