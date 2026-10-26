using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pepa.Core;

namespace Pepa.Adapters.Browser;

/// <summary>Профиль AdsPower из списка профилей.</summary>
public sealed record AdsPowerProfile(string Id, string? Number, string Name, string? Group);

/// <summary>Состояние профиля: открыт ли браузер и адрес для подключения (CDP).</summary>
public sealed record AdsPowerBrowserState(bool Active, string? WsEndpoint);

/// <summary>Ошибка Local API AdsPower с понятным текстом.</summary>
public sealed class AdsPowerException(string message, bool notRunning = false) : Exception(message)
{
    /// <summary>Приложение AdsPower не запущено или Local API выключен — запрос точно не выполнен.</summary>
    public bool NotRunning { get; } = notRunning;
}

/// <summary>
/// Клиент Local API AdsPower (запущенное приложение AdsPower на этом компьютере). Используются запросы v2
/// (<c>/api/v2/browser-profile/*</c>), для старых версий AdsPower — v1. Ключ API передаётся заголовком
/// <c>Authorization: Bearer</c>. Запросы идут по одному, с паузой: Local API ограничивает частоту.
/// </summary>
public sealed class AdsPowerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<AdsPowerSettings> _settings;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public AdsPowerClient(Func<AdsPowerSettings> settings, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(90); // запуск профиля может занимать десятки секунд
    }

    /// <summary>Проверка связи с Local API.</summary>
    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        var body = await SendAsync(HttpMethod.Get, "/status", null, cancellationToken);
        return body.GetString("msg") ?? "ok";
    }

    /// <summary>Открыть браузер профиля (если уже открыт — AdsPower вернёт адрес подключения к нему).</summary>
    public async Task<AdsPowerBrowserState> StartAsync(string profileId, CancellationToken cancellationToken)
    {
        var body = await SendWithFallbackAsync(
            () => SendAsync(HttpMethod.Post, "/api/v2/browser-profile/start", new JsonObject { ["profile_id"] = profileId }, cancellationToken),
            () => SendAsync(HttpMethod.Get, $"/api/v1/browser/start?user_id={Uri.EscapeDataString(profileId)}", null, cancellationToken));
        var ws = WsOf(body["data"] as JsonObject);
        if (ws is null)
        {
            throw new AdsPowerException("AdsPower открыл профиль, но не сообщил адрес для подключения (ws).");
        }

        return new AdsPowerBrowserState(true, ws);
    }

    /// <summary>Открыт ли браузер профиля (только чтение — ничего не запускает).</summary>
    public async Task<AdsPowerBrowserState> GetStateAsync(string profileId, CancellationToken cancellationToken)
    {
        var body = await SendWithFallbackAsync(
            () => SendAsync(HttpMethod.Get, $"/api/v2/browser-profile/active?profile_id={Uri.EscapeDataString(profileId)}", null, cancellationToken),
            () => SendAsync(HttpMethod.Get, $"/api/v1/browser/active?user_id={Uri.EscapeDataString(profileId)}", null, cancellationToken));
        var data = body["data"] as JsonObject;
        var status = data?.GetString("status");
        var active = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase);
        return new AdsPowerBrowserState(active, active ? WsOf(data) : null);
    }

    public async Task StopAsync(string profileId, CancellationToken cancellationToken)
    {
        await SendWithFallbackAsync(
            () => SendAsync(HttpMethod.Post, "/api/v2/browser-profile/stop", new JsonObject { ["profile_id"] = profileId }, cancellationToken),
            () => SendAsync(HttpMethod.Get, $"/api/v1/browser/stop?user_id={Uri.EscapeDataString(profileId)}", null, cancellationToken));
    }

    public async Task<IReadOnlyList<AdsPowerProfile>> ListProfilesAsync(int page, int limit, CancellationToken cancellationToken)
    {
        var body = await SendWithFallbackAsync(
            () => SendAsync(HttpMethod.Post, "/api/v2/browser-profile/list", new JsonObject { ["page"] = page, ["limit"] = limit }, cancellationToken),
            () => SendAsync(HttpMethod.Get, $"/api/v1/user/list?page={page}&page_size={limit}", null, cancellationToken));
        var list = (body["data"] as JsonObject)?["list"] as JsonArray ?? [];
        return list.OfType<JsonObject>()
            .Select(p => new AdsPowerProfile(
                p.GetString("profile_id") ?? p.GetString("user_id") ?? "",
                (p["profile_no"] ?? p["serial_number"])?.ToString(),
                p.GetString("name") ?? "",
                p.GetString("group_name")))
            .Where(p => p.Id.Length > 0)
            .ToList();
    }

    private static string? WsOf(JsonObject? data)
    {
        if (data?["ws"] is JsonObject ws && ws.GetString("puppeteer") is { Length: > 0 } puppeteer)
        {
            return puppeteer;
        }

        // Запасной вариант: известен только порт отладки.
        var port = data?["debug_port"]?.ToString();
        return string.IsNullOrWhiteSpace(port) ? null : $"http://127.0.0.1:{port}";
    }

    private static async Task<JsonObject> SendWithFallbackAsync(Func<Task<JsonObject>> v2, Func<Task<JsonObject>> v1)
    {
        try
        {
            return await v2();
        }
        catch (EndpointMissingException)
        {
            return await v1();
        }
    }

    private sealed class EndpointMissingException : Exception;

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        var settings = _settings();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pause = _lastRequest + settings.MinInterval - _time.GetUtcNow();
            if (pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, _time, cancellationToken);
            }

            _lastRequest = _time.GetUtcNow();
            using var request = new HttpRequestMessage(method, new Uri(new Uri(settings.BaseUrl), path));
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new AdsPowerException(
                    $"Нет связи с AdsPower по адресу {settings.BaseUrl}. Запустите AdsPower и проверьте, что Local API включён " +
                    $"(в AdsPower: Настройки → Local API). ({ex.Message})", notRunning: true);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new EndpointMissingException();
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonObject? json;
                try
                {
                    json = JsonNode.Parse(text) as JsonObject;
                }
                catch (System.Text.Json.JsonException)
                {
                    json = null;
                }

                if (json is null)
                {
                    throw new AdsPowerException($"AdsPower ответил неожиданно (HTTP {(int)response.StatusCode}).");
                }

                if (json["code"]?.ToString() is { } code && code != "0")
                {
                    var message = json.GetString("msg") ?? "без описания";
                    throw new AdsPowerException(response.StatusCode == HttpStatusCode.Unauthorized || message.Contains("api key", StringComparison.OrdinalIgnoreCase)
                        ? $"AdsPower отклонил запрос: {message}. Проверьте ключ API в настройках Pepa."
                        : $"AdsPower: {message}");
                }

                return json;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Подключение к AdsPower.</summary>
public sealed record AdsPowerSettings
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:50325";
    public string? ApiKey { get; init; }

    /// <summary>Пауза между запросами к Local API.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(1100);
}
