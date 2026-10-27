namespace Pepa.Host.Tests;

/// <summary>Подключение Telegram через API (без настоящего Telegram: адрес сервера заведомо недоступен).</summary>
public sealed class TelegramApiTests
{
    private const string ApiHash = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Not_configured_by_default_and_blocks_are_described()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var status = await f.GetAsync("/api/integrations/telegram");
        Assert.Equal("notConfigured", status["state"]!.GetValue<string>());
        Assert.Null(status["apiId"]);

        var types = await f.GetAsync("/api/node-types");
        Assert.Contains(types["categories"]!.AsArray(), c => c!["id"]!.GetValue<string>() == "telegram");
        var send = types["nodeTypes"]!.AsArray().Single(t => t!["type"]!.GetValue<string>() == "telegram.send")!;
        Assert.Equal("external", send["effect"]!.GetValue<string>());
        Assert.True(send["supportsVerification"]!.GetValue<bool>());
        Assert.Contains(types["nodeTypes"]!.AsArray(), t => t!["type"]!.GetValue<string>() == "telegram.wait");
    }

    [Fact]
    public async Task Login_rejects_incomplete_data_without_connecting()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, error) = await f.SendAsync(HttpMethod.Post, "/api/integrations/telegram/login",
            new { apiId = 0, apiHash = ApiHash, phone = "+79001234567" });
        Assert.Equal(400, status);
        Assert.Contains("api_id", error!["error"]!.GetValue<string>());

        (status, error) = await f.SendAsync(HttpMethod.Post, "/api/integrations/telegram/login",
            new { apiId = 12345, apiHash = "not-a-hash", phone = "+79001234567" });
        Assert.Equal(400, status);
        Assert.Contains("32 символа", error!["error"]!.GetValue<string>());

        (status, error) = await f.SendAsync(HttpMethod.Post, "/api/integrations/telegram/login/continue", new { value = "12345" });
        Assert.Equal(400, status);
        Assert.Contains("Вход не начат", error!["error"]!.GetValue<string>());
        Assert.Equal("notConfigured", (await f.GetAsync("/api/integrations/telegram"))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_connection_returns_to_login_form_with_reason_and_saved_number()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, error) = await f.SendAsync(HttpMethod.Post, "/api/integrations/telegram/login",
            new { apiId = 12345, apiHash = ApiHash, phone = "+7 (900) 123-45-67" });
        Assert.Equal(400, status);
        Assert.Contains("Не удалось связаться с Telegram", error!["error"]!.GetValue<string>());

        var telegram = await f.GetAsync("/api/integrations/telegram");
        Assert.Equal("loginRequired", telegram["state"]!.GetValue<string>());
        Assert.Equal(12345, telegram["apiId"]!.GetValue<int>());
        Assert.Equal("+79001234567", telegram["phone"]!.GetValue<string>());
        Assert.DoesNotContain(ApiHash, telegram.ToJsonString());

        // Неудавшийся вход не оставляет сессии: после перезапуска Pepa не пытается подключаться сама.
        await f.RestartAsync();
        telegram = await f.GetAsync("/api/integrations/telegram");
        Assert.Equal("loginRequired", telegram["state"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(f.DataDirectory, "telegram", "session.dat")));

        // Повторный вход с тем же api_id — без api_hash (берётся сохранённый).
        (status, error) = await f.SendAsync(HttpMethod.Post, "/api/integrations/telegram/login",
            new { apiId = 12345, phone = "+79001234567" });
        Assert.Equal(400, status);
        Assert.Contains("Не удалось связаться с Telegram", error!["error"]!.GetValue<string>());

        var (logoutStatus, loggedOut) = await f.SendAsync(HttpMethod.Post, "/api/integrations/telegram/logout");
        Assert.Equal(200, logoutStatus);
        Assert.Equal("notConfigured", loggedOut!["state"]!.GetValue<string>());
        Assert.Null(loggedOut["apiId"]);
        Assert.False(File.Exists(Path.Combine(f.DataDirectory, "telegram", "account.json")));
    }
}

/// <summary>Прокси Telegram через API: ссылка или поля, пароль и секрет не возвращаются.</summary>
public sealed class TelegramProxyApiTests
{
    [Fact]
    public async Task Proxy_is_set_by_link_or_fields_shown_without_secrets_and_checked()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        Assert.Equal("none", (await f.GetAsync("/api/integrations/telegram"))["proxy"]!["kind"]!.GetValue<string>());

        var (status, error) = await f.SendAsync(HttpMethod.Put, "/api/integrations/telegram/proxy", new { link = "https://example.com/x" });
        Assert.Equal(400, status);
        Assert.Contains("не похожа", error!["error"]!.GetValue<string>());

        const string secret = "dd0123456789abcdef0123456789abcdef";
        var (ok, saved) = await f.SendAsync(HttpMethod.Put, "/api/integrations/telegram/proxy",
            new { link = $"tg://proxy?server=127.0.0.1&port=1&secret={secret}" });
        Assert.Equal(200, ok);
        Assert.Equal("mtproxy", saved!["proxy"]!["kind"]!.GetValue<string>());
        Assert.True(saved["proxy"]!["hasSecret"]!.GetValue<bool>());
        Assert.DoesNotContain(secret, saved.ToJsonString());

        (ok, saved) = await f.SendAsync(HttpMethod.Put, "/api/integrations/telegram/proxy",
            new { kind = "socks5", host = "127.0.0.1", port = 1, username = "ivan", password = "очень-секретно" });
        Assert.Equal(200, ok);
        Assert.Equal("ivan", saved!["proxy"]!["username"]!.GetValue<string>());
        Assert.True(saved["proxy"]!["hasPassword"]!.GetValue<bool>());
        Assert.DoesNotContain("очень-секретно", saved.ToJsonString());
        Assert.DoesNotContain("очень-секретно", (await f.GetAsync("/api/integrations/telegram")).ToJsonString());

        // Проверка: на порту 1 прокси нет — понятный ответ, а не ошибка сервера.
        var (checkStatus, check) = await f.SendAsync(HttpMethod.Post, "/api/integrations/telegram/proxy/check",
            new { kind = "socks5", host = "127.0.0.1", port = 1, username = "ivan" });
        Assert.Equal(200, checkStatus);
        Assert.False(check!["ok"]!.GetValue<bool>());
        Assert.Contains("недоступен", check["message"]!.GetValue<string>());

        (ok, saved) = await f.SendAsync(HttpMethod.Put, "/api/integrations/telegram/proxy", new { kind = "none" });
        Assert.Equal(200, ok);
        Assert.Equal("none", saved!["proxy"]!["kind"]!.GetValue<string>());
    }
}

/// <summary>Блоки окон Windows в сервере без окна: описаны для конструктора, но честно недоступны.</summary>
public sealed class DesktopApiTests
{
    [Fact]
    public async Task Desktop_blocks_are_described_and_windows_list_explains_why_it_is_unavailable()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var types = await f.GetAsync("/api/node-types");
        Assert.Contains(types["categories"]!.AsArray(), c => c!["id"]!.GetValue<string>() == "desktop");
        var click = types["nodeTypes"]!.AsArray().Single(t => t!["type"]!.GetValue<string>() == "desktop.click")!;
        Assert.Contains("мышь и клавиатуру", click["resourceHint"]!.GetValue<string>());
        Assert.Equal("desktop-windows", click["fields"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "window")!["lookup"]!.GetValue<string>());

        var (status, error) = await f.SendAsync(HttpMethod.Get, "/api/integrations/desktop/windows");
        Assert.Equal(409, status);
        Assert.Contains("Pepa.exe", error!["error"]!.GetValue<string>());
        Assert.Equal(409, (await f.SendAsync(HttpMethod.Get, "/api/integrations/desktop/elements?window=Блокнот")).Status);

        // Запись в окнах Windows — тоже только в Pepa.exe: причина видна до начала записи.
        var recorder = await f.GetAsync("/api/recorder/desktop");
        Assert.Contains("Pepa.exe", recorder["unavailableReason"]!.GetValue<string>());
        var (start, why) = await f.SendAsync(HttpMethod.Post, "/api/recorder/desktop/start");
        Assert.Equal(409, start);
        Assert.Contains("Pepa.exe", why!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notepad_sample_starts_the_program_waits_for_its_window_and_types()
    {
        var desktop = new Adapters.Desktop.Tests.FakeDesktop();
        desktop.OpensWindow["notepad"] = "Безымянный – Блокнот";
        await using var f = await HostFixture.StartAsync(desktop: desktop);
        var id = await f.ScenarioIdAsync("Пример 12.");
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/run", new { inputs = new { text = "Проверка" } });
        var done = await f.WaitForProcessAsync(run!["processId"]!.GetValue<string>(), p => HostFixture.Status(p) is "completed" or "needsAttention");
        Assert.Equal("completed", HostFixture.Status(done["process"]!));
        Assert.Equal(["notepad"], desktop.Started);
        Assert.Contains("type Проверка", desktop.Actions);
    }

    [Fact]
    public async Task Actions_in_windows_are_recorded_into_blocks()
    {
        var desktop = new Adapters.Desktop.Tests.FakeDesktop();
        var notepad = desktop.AddWindow("Документ — Блокнот", "notepad");
        var save = notepad.Add("Сохранить", id: "save");
        var monitor = new Adapters.Desktop.Tests.FakeInputMonitor();
        await using var f = await HostFixture.StartAsync(seedSamples: false, desktop: desktop, desktopInput: monitor);

        var (status, started) = await f.SendAsync(HttpMethod.Post, "/api/recorder/desktop/start");
        Assert.Equal(200, status);
        Assert.True(started!["recording"]!.GetValue<bool>());
        Assert.Null(started["unavailableReason"]);
        monitor.Send(new Adapters.Desktop.DesktopClickInput(DateTimeOffset.Now,
            new Adapters.Desktop.DesktopTarget { Window = notepad.Info, Element = save.Info, SameId = 1, SameName = 1 }, 120, 220,
            Adapters.Desktop.MouseButtonKind.Left));
        var live = await f.GetAsync("/api/recorder/desktop");
        Assert.Single(live["steps"]!.AsArray());
        Assert.Equal(409, (await f.SendAsync(HttpMethod.Post, "/api/recorder/desktop/start")).Status);

        var (_, stopped) = await f.SendAsync(HttpMethod.Post, "/api/recorder/desktop/stop");
        Assert.False(stopped!["recording"]!.GetValue<bool>());
        var step = stopped["steps"]!.AsArray().Single()!;
        Assert.Equal("desktop.click", step["type"]!.GetValue<string>());
        Assert.Equal("Нажать «Сохранить»", step["label"]!.GetValue<string>());
        Assert.Equal("save", step["config"]!["automationId"]!.GetValue<string>());
        Assert.Equal("Блокнот", step["config"]!["window"]!.GetValue<string>());
        Assert.False(monitor.Watching);
    }

    [Fact]
    public async Task Window_elements_are_listed_with_uniqueness_and_ambiguous_window_is_not_guessed()
    {
        var desktop = new Adapters.Desktop.Tests.FakeDesktop();
        var notepad = desktop.AddWindow("Документ — Блокнот", "notepad");
        notepad.Add("Сохранить", id: "save");
        notepad.Add("Открыть");
        notepad.Add("Открыть", type: "MenuItem");
        notepad.Add("Текст", type: "Edit", id: "editor", hasValue: true);
        desktop.AddWindow("Отчёт — Excel", "excel");
        desktop.AddWindow("Отчёт — Word", "winword");
        await using var f = await HostFixture.StartAsync(seedSamples: false, desktop: desktop);

        var list = await f.GetAsync("/api/integrations/desktop/elements?window=" + Uri.EscapeDataString("Блокнот"));
        Assert.Equal("notepad", list["process"]!.GetValue<string>());
        var elements = list["elements"]!.AsArray();
        Assert.Equal(4, elements.Count);
        var save = elements.Single(e => e!["name"]!.GetValue<string>() == "Сохранить")!;
        Assert.True(save["uniqueName"]!.GetValue<bool>());
        Assert.True(save["uniqueId"]!.GetValue<bool>());
        Assert.All(elements.Where(e => e!["name"]!.GetValue<string>() == "Открыть"), e => Assert.False(e!["uniqueName"]!.GetValue<bool>()));

        var (ambiguous, why) = await f.SendAsync(HttpMethod.Get, "/api/integrations/desktop/elements?window=" + Uri.EscapeDataString("Отчёт"));
        Assert.Equal(409, ambiguous);
        Assert.Contains("несколько окон", why!["error"]!.GetValue<string>());
        Assert.Equal(200, (await f.SendAsync(HttpMethod.Get, "/api/integrations/desktop/elements?window=" + Uri.EscapeDataString("Отчёт") + "&process=excel")).Status);
        Assert.Equal(404, (await f.SendAsync(HttpMethod.Get, "/api/integrations/desktop/elements?window=" + Uri.EscapeDataString("Калькулятор"))).Status);
        Assert.Equal(400, (await f.SendAsync(HttpMethod.Get, "/api/integrations/desktop/elements")).Status);
    }
}
