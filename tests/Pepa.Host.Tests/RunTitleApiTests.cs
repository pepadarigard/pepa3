namespace Pepa.Host.Tests;

/// <summary>Название запуска и входные данные для «Запустить снова» в API (D-081).</summary>
public sealed class RunTitleApiTests
{
    [Fact]
    public async Task Lists_and_details_show_the_run_title_and_details_give_inputs_without_secrets()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Вход в аккаунт",
            runTitle = "{{ vars.login }} · {{ vars.profile }}",
            variables = new object[]
            {
                new { name = "login", type = "text", isInput = true },
                new { name = "password", type = "text", isInput = true, secret = true },
                new { name = "profile", type = "text", isInput = true, lookup = "adspower-profiles" },
            },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "wait", type = "event.wait", config = new { source = "manual", eventType = "go" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "wait" },
                new { id = "b", source = "wait", sourcePort = "event", target = "end" },
            },
        });
        Assert.Equal(200, status);
        var scenarioId = scenario!["id"]!.GetValue<string>();
        var saved = await f.GetAsync($"/api/scenarios/{scenarioId}");
        Assert.Equal("adspower-profiles", saved["definition"]!["variables"]![2]!["lookup"]!.GetValue<string>());

        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenarioId}/run",
            new { inputs = new { login = "anna", password = "pw-9f8e7d6c", profile = "k1a2" } });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");

        var list = await f.GetAsync("/api/processes?view=active");
        Assert.Equal("anna · k1a2", list.AsArray().Single(p => p!["id"]!.GetValue<string>() == id)!["title"]!.GetValue<string>());
        var overview = await f.GetAsync("/api/overview");
        Assert.Equal("anna · k1a2", overview["active"]!.AsArray().Single()!["title"]!.GetValue<string>());

        var details = await f.GetAsync($"/api/processes/{id}");
        Assert.Equal("anna · k1a2", details["process"]!["title"]!.GetValue<string>());
        var inputs = details["runInputs"]!.AsObject();
        Assert.Equal("anna", inputs["login"]!.GetValue<string>());
        Assert.Equal("k1a2", inputs["profile"]!.GetValue<string>());
        Assert.False(inputs.ContainsKey("password"));
        Assert.DoesNotContain("pw-9f8e7d6c", await f.Client.GetStringAsync($"/api/processes/{id}"));
        await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/cancel");
    }
}

/// <summary>Сбой автозапуска виден на «Главной» и в пояснении события.</summary>
public sealed class FailedTriggerApiTests
{
    [Fact]
    public async Task Home_lists_an_autostart_that_failed_and_the_event_says_why()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Заказ",
            variables = new object[] { new { name = "order", type = "text", isInput = true } },
            nodes = new object[] { new { id = "start", type = "start" }, new { id = "end", type = "end" } },
            edges = new object[] { new { id = "a", source = "start", sourcePort = "out", target = "end" } },
        });
        var (status, _) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId = scenario!["id"]!.GetValue<string>(),
            kind = "event",
            enabled = true,
            name = "Новый заказ",
            config = new { source = "shop", eventType = "order" },
            inputs = new { order = "event.payload.missing.id" },
        });
        Assert.Equal(200, status);
        Assert.Empty((await f.GetAsync("/api/overview"))["failedTriggers"]!.AsArray());

        var (_, published) = await f.SendAsync(HttpMethod.Post, "/api/events", new { source = "shop", type = "order", payload = new { id = 5 } });
        var eventId = published!["eventId"]!.GetValue<long>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while ((await f.GetAsync("/api/overview"))["failedTriggers"]!.AsArray().Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "сбой автозапуска не появился на «Главной»");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var failed = (await f.GetAsync("/api/overview"))["failedTriggers"]!.AsArray().Single()!;
        Assert.Equal("Новый заказ", failed["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(failed["lastError"]!.GetValue<string>()));

        // Сбой автозапуска записывается раньше, чем пометка у события (её ставит разбор события после запусков).
        string? note;
        while ((note = (await f.GetAsync("/api/events?view=all")).AsArray()
                   .Single(e => e!["id"]!.GetValue<long>() == eventId)!["note"]?.GetValue<string>()) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "у события нет пометки о сбое автозапуска");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.StartsWith("Автозапуск не запустил процесс", note);
    }
}

/// <summary>«Исправить и повторить» через API (D-085).</summary>
public sealed class FixAndRetryApiTests
{
    [Fact]
    public async Task Failed_step_is_repeated_with_the_fixed_input()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        Directory.CreateDirectory(f.Workspace);
        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "заказ.txt"), "A-17", TestContext.Current.CancellationToken);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Прочитать заказ",
            variables = new object[]
            {
                new { name = "file", type = "text", isInput = true },
                new { name = "text", type = "any" },
                new { name = "key", type = "text", @default = "k-Secret-99", secret = true },
                new { name = "url", type = "text" },
            },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "link", type = "variable.set", config = new { variable = "url", value = "'https://x.test/?k=' + vars.key" } },
                new { id = "read", type = "file.read", config = new { path = "{{ vars.file }}", saveTo = "text" } },
                new { id = "end", type = "end", config = new { message = "{{ vars.text }}" } },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "link" },
                new { id = "l", source = "link", sourcePort = "out", target = "read" },
                new { id = "b", source = "read", sourcePort = "out", target = "end" },
            },
        });
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario!["id"]!.GetValue<string>()}/run",
            new { inputs = new { file = "закз.txt" } });
        var id = run!["processId"]!.GetValue<string>();
        var stopped = await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "needsAttention");
        Assert.Contains("retryWithChanges", stopped["process"]!["allowedActions"]!.AsArray().Select(a => a!.GetValue<string>()));
        // В адресе — секретный ключ: окно не подставит его значение (со скрытой частью) в поле.
        Assert.Equal(["url"], stopped["hiddenVariables"]!.AsArray().Select(v => v!.GetValue<string>()));

        var (status, body) = await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/resume",
            new { action = "retryWithChanges", variables = new { file = "заказ.txt" } });
        Assert.Equal(200, status);
        Assert.StartsWith("Переменные исправлены (1).", body!["message"]!.GetValue<string>());
        var done = await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "completed");
        Assert.Equal("A-17", done["process"]!["statusMessage"]!.GetValue<string>());

        var details = await f.GetAsync($"/api/processes/{id}");
        Assert.Contains(details["journal"]!.AsArray(), j =>
            j!["message"]!.GetValue<string>() == "Пользователь исправил переменную «file»: «закз.txt» → «заказ.txt».");
    }
}

/// <summary>«Несколько запусков» через API (D-088).</summary>
public sealed class RunManyApiTests
{
    [Fact]
    public async Task Each_value_starts_its_own_process_and_the_same_batch_is_not_started_twice()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Привет",
            variables = new object[] { new { name = "who", type = "text", isInput = true } },
            nodes = new object[] { new { id = "start", type = "start" }, new { id = "end", type = "end", config = new { message = "Привет, {{ vars.who }}" } } },
            edges = new object[] { new { id = "a", source = "start", sourcePort = "out", target = "end" } },
        });
        var id = scenario!["id"]!.GetValue<string>();
        var body = new { runs = new object[] { new { who = "Анна" }, new { who = "Борис" } }, batchId = "e2ebatch1" };

        var (status, started) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run-many", body);
        Assert.Equal(200, status);
        var ids = started!["processIds"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal("Привет, Борис", (await f.WaitForProcessAsync(ids[1], p => HostFixture.Status(p) == "completed"))["process"]!["statusMessage"]!.GetValue<string>());

        var (_, again) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run-many", body);
        Assert.Equal(ids, again!["processIds"]!.AsArray().Select(p => p!.GetValue<string>()));

        var (badStatus, _) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run-many", new { runs = new object[] { new { who = "x" } }, batchId = "../../" });
        Assert.Equal(400, badStatus);
    }

    [Fact]
    public async Task Run_sent_twice_with_the_same_key_starts_one_process()
    {
        // Двойной щелчок «Запустить» у сценария без входных данных (или повтор после обрыва связи) — один процесс (D-090).
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Один раз",
            nodes = new object[] { new { id = "start", type = "start" }, new { id = "end", type = "end" } },
            edges = new object[] { new { id = "a", source = "start", sourcePort = "out", target = "end" } },
        });
        var id = scenario!["id"]!.GetValue<string>();

        var (status, first) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run", new { inputs = (object?)null, runId = "run0000001" });
        Assert.Equal(200, status);
        var (_, again) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run", new { inputs = (object?)null, runId = "run0000001" });
        Assert.Equal(first!["processId"]!.GetValue<string>(), again!["processId"]!.GetValue<string>());

        var (_, other) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run", new { inputs = (object?)null, runId = "run0000002" });
        Assert.NotEqual(first["processId"]!.GetValue<string>(), other!["processId"]!.GetValue<string>());

        var (badStatus, _) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run", new { runId = "../x" });
        Assert.Equal(400, badStatus);
    }

    [Fact]
    public async Task Stopped_batch_processes_are_listed_and_counted_apart_and_a_stale_decision_is_refused()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Чтение",
            variables = new object[]
            {
                new { name = "file", type = "text", isInput = true },
                new { name = "text", type = "text" },
            },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "read", type = "file.read", config = new { path = "{{ vars.file }}", saveTo = "text" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "read" },
                new { id = "b", source = "read", sourcePort = "out", target = "end" },
            },
        });
        var id = scenario!["id"]!.GetValue<string>();
        var (_, started) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run-many",
            new { runs = new object[] { new { file = "нет-такого-1.txt" }, new { file = "нет-такого-2.txt" } }, batchId = "stopped01" });
        var ids = started!["processIds"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        foreach (var process in ids)
        {
            await f.WaitForProcessAsync(process, p => HostFixture.Status(p) == "needsAttention");
        }

        // Ждущие решения — своим списком и счётчиком, а не из обрезанного списка незавершённых.
        var attention = (await f.GetAsync("/api/processes?view=attention")).AsArray();
        Assert.Equal(ids.Order(), attention.Select(p => p!["id"]!.GetValue<string>()).Order());
        Assert.Empty((await f.GetAsync("/api/processes?view=working")).AsArray());
        var overview = await f.GetAsync("/api/overview");
        Assert.Equal(2, overview["counts"]!["needsAttention"]!.GetValue<int>());
        Assert.Equal(2, overview["attention"]!.AsArray().Count);

        // Решение по прежнему виду процесса не применяется; по нынешнему — применяется.
        var first = attention.First(p => p!["id"]!.GetValue<string>() == ids[0])!;
        var (stale, error) = await f.SendAsync(HttpMethod.Post, $"/api/processes/{ids[0]}/resume", new { action = "retry", expectedRevision = "0:0:Running:None" });
        Assert.Equal(409, stale);
        Assert.Contains("изменился", error!["error"]!.GetValue<string>());
        var (ok, _) = await f.SendAsync(HttpMethod.Post, $"/api/processes/{ids[0]}/resume",
            new { action = "retry", expectedRevision = first["revision"]!.GetValue<string>() });
        Assert.Equal(200, ok);
    }
}

/// <summary>Сообщение, пришедшее до окна ожидания, видно у ждущего процесса и отдаётся ему вручную (D-089).</summary>
public sealed class UnmatchedRepliesApiTests
{
    [Fact]
    public async Task Earlier_unmatched_event_is_offered_to_the_waiting_process_and_can_be_given_to_it()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Подождать и ждать ответ",
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "pause", type = "wait", config = new { mode = "duration", amount = 2, unit = "seconds" } },
                new { id = "wait", type = "event.wait", config = new { source = "manual", correlationKey = "chat", timeoutSeconds = 600 } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "pause" },
                new { id = "b", source = "pause", sourcePort = "out", target = "wait" },
                new { id = "c", source = "wait", sourcePort = "event", target = "end" },
            },
        });
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario!["id"]!.GetValue<string>()}/run", new { });
        var id = run!["processId"]!.GetValue<string>();

        // Сообщение пришло во время паузы — до ожидания: процесс его сам не берёт.
        var (_, published) = await f.SendAsync(HttpMethod.Post, "/api/events",
            new { source = "manual", type = "reply", correlationKey = "chat", externalId = (string?)null, payload = new { text = "готово" } });
        var eventId = published!["eventId"]!.GetValue<long>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting" && p["currentNodeId"]?.GetValue<string>() == "wait");

        var offered = (await f.GetAsync($"/api/processes/{id}/unmatched-events")).AsArray();
        var item = Assert.Single(offered);
        Assert.Equal(eventId, item!["event"]!["id"]!.GetValue<long>());

        var (status, _) = await f.SendAsync(HttpMethod.Post, $"/api/events/{eventId}/resolve",
            new { subscriptionId = item["subscriptionId"]!.GetValue<long>() });
        Assert.Equal(200, status);
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "completed");
        Assert.Empty((await f.GetAsync($"/api/processes/{id}/unmatched-events")).AsArray());
    }
}
