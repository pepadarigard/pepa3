using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Pepa.Adapters.Browser.Tests;

/// <summary>
/// Имитация Local API AdsPower: «профиль» — настоящий браузер, запущенный с портом отладки (CDP), как это делает AdsPower.
/// </summary>
internal sealed class FakeAdsPower : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (Process Process, string Ws, string Dir)> _open = new();
    private readonly Lock _lock = new();

    public FakeAdsPower(string browserPath, string? apiKey = null, bool v1Only = false)
    {
        BrowserPath = browserPath;
        ApiKey = apiKey;
        V1Only = v1Only;
        Port = TestSite.FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BrowserPath { get; }
    public string? ApiKey { get; }
    public bool V1Only { get; }
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public List<string> Requests { get; } = [];
    public int Starts { get; private set; }

    /// <summary>Отказ AdsPower при открытии профиля: профиль → текст ответа («code»: -1).</summary>
    public Dictionary<string, string> StartErrors { get; } = new();

    public bool IsOpen(string profileId)
    {
        lock (_lock)
        {
            return _open.TryGetValue(profileId, out var entry) && !entry.Process.HasExited;
        }
    }

    /// <summary>Пользователь закрыл профиль сам.</summary>
    public void CloseExternally(string profileId) => Stop(profileId);

    /// <summary>Владелец открыл профиль сам, в AdsPower (не через Pepa).</summary>
    public Task OpenByOwnerAsync(string profileId) => StartAsync(profileId);

    /// <summary>Владелец открыл в профиле ещё одну вкладку.</summary>
    public async Task OpenTabAsync(string profileId, string url)
    {
        using var http = DevToolsClient();
        using var response = await http.PutAsync($"{DevTools(profileId)}/json/new?{url}", null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Владелец закрыл вкладки профиля, адрес которых содержит текст.</summary>
    public async Task CloseTabsAsync(string profileId, string urlContains)
    {
        using var http = DevToolsClient();
        var root = DevTools(profileId);
        var tabs = JsonNode.Parse(await http.GetStringAsync($"{root}/json/list"))!.AsArray();
        foreach (var tab in tabs)
        {
            if (tab?["type"]?.GetValue<string>() == "page" && tab["url"]?.GetValue<string>() is { } address &&
                address.Contains(urlContains, StringComparison.Ordinal))
            {
                (await http.GetAsync($"{root}/json/close/{tab["id"]!.GetValue<string>()}")).EnsureSuccessStatusCode();
            }
        }
    }

    private static HttpClient DevToolsClient() => new(new HttpClientHandler { UseProxy = false });

    private string DevTools(string profileId)
    {
        lock (_lock)
        {
            return $"http://127.0.0.1:{new Uri(_open[profileId].Ws).Port}";
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            JsonObject response;
            try
            {
                response = await HandleAsync(context.Request);
            }
            catch (Exception ex)
            {
                response = new JsonObject { ["code"] = -1, ["msg"] = ex.Message };
            }

            if (response.ContainsKey("__status"))
            {
                context.Response.StatusCode = (int)response["__status"]!;
                response.Remove("__status");
            }

            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private async Task<JsonObject> HandleAsync(HttpListenerRequest request)
    {
        var path = request.Url!.AbsolutePath;
        lock (_lock)
        {
            Requests.Add($"{request.HttpMethod} {path}");
        }

        if (ApiKey is not null && request.Headers["Authorization"] != $"Bearer {ApiKey}")
        {
            return new JsonObject { ["code"] = -1, ["msg"] = "api key is invalid" };
        }

        if (V1Only && path.StartsWith("/api/v2/", StringComparison.Ordinal))
        {
            return new JsonObject { ["__status"] = 404 };
        }

        string? profile = request.QueryString["profile_id"] ?? request.QueryString["user_id"];
        if (request.HttpMethod == "POST" && request.HasEntityBody)
        {
            var body = await JsonNode.ParseAsync(request.InputStream) as JsonObject;
            profile ??= body?["profile_id"]?.GetValue<string>();
        }

        switch (path)
        {
            case "/status":
                return Ok(null);
            case "/api/v2/browser-profile/start" or "/api/v1/browser/start":
            {
                if (StartErrors.TryGetValue(profile!, out var refusal))
                {
                    return new JsonObject { ["code"] = -1, ["msg"] = refusal, ["data"] = new JsonObject() };
                }

                var ws = await StartAsync(profile!);
                return Ok(new JsonObject { ["ws"] = new JsonObject { ["puppeteer"] = ws, ["selenium"] = "127.0.0.1:1" }, ["debug_port"] = "1" });
            }

            case "/api/v2/browser-profile/active" or "/api/v1/browser/active":
            {
                string? ws;
                lock (_lock)
                {
                    ws = _open.TryGetValue(profile!, out var entry) && !entry.Process.HasExited ? entry.Ws : null;
                }

                return Ok(ws is null
                    ? new JsonObject { ["status"] = "Inactive" }
                    : new JsonObject { ["status"] = "Active", ["ws"] = new JsonObject { ["puppeteer"] = ws } });
            }

            case "/api/v2/browser-profile/stop" or "/api/v1/browser/stop":
                Stop(profile!);
                return Ok(null);
            case "/api/v2/browser-profile/list" or "/api/v1/user/list":
                return Ok(new JsonObject
                {
                    ["list"] = new JsonArray(
                        new JsonObject { ["profile_id"] = "k1", ["user_id"] = "k1", ["name"] = "Первый", ["profile_no"] = 1, ["serial_number"] = "1" },
                        new JsonObject { ["profile_id"] = "k2", ["user_id"] = "k2", ["name"] = "Второй", ["profile_no"] = 2, ["serial_number"] = "2" }),
                });
            default:
                return new JsonObject { ["__status"] = 404 };
        }
    }

    private static JsonObject Ok(JsonObject? data) => new() { ["code"] = 0, ["msg"] = "success", ["data"] = data };

    private async Task<string> StartAsync(string profile)
    {
        lock (_lock)
        {
            if (_open.TryGetValue(profile, out var running) && !running.Process.HasExited)
            {
                return running.Ws;
            }
        }

        // Порт выбирает сам браузер (0) и пишет его в DevToolsActivePort: заранее найденный свободный порт успевал занять
        // другой параллельный тест. Ждём с запасом: под нагрузкой (полный прогон, CI) браузер запускается долго.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var dir = Path.Combine(Path.GetTempPath(), "pepa-fake-adspower", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var start = new ProcessStartInfo(BrowserPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[]
                     {
                         "--remote-debugging-port=0", $"--user-data-dir={dir}", "--headless=new", "--no-first-run",
                         "--no-default-browser-check", "--no-sandbox", "about:blank",
                     })
            {
                start.ArgumentList.Add(arg);
            }

            var process = Process.Start(start)!;
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            var portFile = Path.Combine(dir, "DevToolsActivePort");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                if (ReadEndpoint(portFile) is { } ws)
                {
                    lock (_lock)
                    {
                        _open[profile] = (process, ws, dir);
                        Starts++;
                    }

                    return ws;
                }

                await Task.Delay(100);
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Уже завершился.
            }
        }

        throw new InvalidOperationException("Браузер профиля не запустился.");
    }

    /// <summary>Адрес DevTools из файла браузера (порт и путь) или null, пока файл не записан целиком.</summary>
    private static string? ReadEndpoint(string portFile)
    {
        try
        {
            return File.Exists(portFile) && File.ReadAllLines(portFile) is [var port, var path, ..] &&
                   int.TryParse(port, out var number) && number > 0 && path.StartsWith("/devtools/browser/", StringComparison.Ordinal)
                ? $"ws://127.0.0.1:{number}{path}"
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Stop(string profile)
    {
        (Process Process, string Ws, string Dir) entry;
        lock (_lock)
        {
            if (!_open.Remove(profile, out entry))
            {
                return;
            }
        }

        try
        {
            entry.Process.Kill(entireProcessTree: true);
            entry.Process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            Directory.Delete(entry.Dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        foreach (var profile in _open.Keys.ToList())
        {
            Stop(profile);
        }

        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}
