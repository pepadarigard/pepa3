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
public sealed class AdsPowerException(string message, bool notRunning = false, bool timedOut = false) : Exception(message)
{
    /// <summary>AdsPower не ответил вовремя: запрос мог выполниться (например, профиль начал открываться).</summary>
    public bool TimedOut { get; } = timedOut;

    /// <summary>Приложение AdsPower не запущено или Local API выключен — запрос точно не выполнен.</summary>
    public bool NotRunning { get; } = notRunning;

    /// <summary>
    /// Повтор по настройкам блока может помочь (AdsPower занят, ограничил частоту, прокси временно недоступен). Нет — нужен
    /// человек: профиля нет, AdsPower не вошёл в аккаунт, ключ API неверный.
    /// </summary>
    public bool Retryable { get; init; } = true;
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
            () => SendAsync(HttpMethod.Post, "/api/v2/browser-profile/start", new JsonObject { ["profile_id"] = profileId }, cancellationToken, profileId),
            () => SendAsync(HttpMethod.Get, $"/api/v1/browser/start?user_id={Uri.EscapeDataString(profileId)}", null, cancellationToken, profileId));
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
            () => SendAsync(HttpMethod.Get, $"/api/v2/browser-profile/active?profile_id={Uri.EscapeDataString(profileId)}", null, cancellationToken, profileId),
            () => SendAsync(HttpMethod.Get, $"/api/v1/browser/active?user_id={Uri.EscapeDataString(profileId)}", null, cancellationToken, profileId));
        var data = body["data"] as JsonObject;
        var status = data?.GetString("status");
        var active = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase);
        return new AdsPowerBrowserState(active, active ? WsOf(data) : null);
    }

    public async Task StopAsync(string profileId, CancellationToken cancellationToken)
    {
        await SendWithFallbackAsync(
            () => SendAsync(HttpMethod.Post, "/api/v2/browser-profile/stop", new JsonObject { ["profile_id"] = profileId }, cancellationToken, profileId),
            () => SendAsync(HttpMethod.Get, $"/api/v1/browser/stop?user_id={Uri.EscapeDataString(profileId)}", null, cancellationToken, profileId));
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

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken,
        string? profileId = null)
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
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AdsPowerException($"AdsPower не ответил за {(int)_http.Timeout.TotalSeconds} с.", timedOut: true);
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
                    throw response.StatusCode == HttpStatusCode.Unauthorized
                        ? new AdsPowerException($"AdsPower отклонил запрос: {message}. Проверьте ключ API в настройках Pepa.") { Retryable = false }
                        : Explain(message, profileId);
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

    /// <summary>
    /// Отказ Local API — понятными словами, что делать. Ответ AdsPower остаётся в тексте как есть (его формулировки
    /// меняются от версии к версии и бывают на английском или китайском), поэтому он узнаётся по ключевым словам; не
    /// узнан — показывается как есть. Отказ значит, что AdsPower запрос не выполнил.
    /// </summary>
    internal static AdsPowerException Explain(string message, string? profileId)
    {
        var said = $"(AdsPower: «{message}»)";
        var profile = profileId is { Length: > 0 } ? $"профиль {profileId}" : "профиль";
        bool Has(params string[] words) => words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (Has("api key", "api-key", "apikey", "unauthorized"))
        {
            return new AdsPowerException($"AdsPower отклонил запрос: неверный или отсутствующий ключ API {said}. Проверьте ключ API в " +
                                         "настройках Pepa (раздел «Браузер и AdsPower»).") { Retryable = false };
        }

        if (Has("kernel", "内核"))
        {
            return new AdsPowerException($"AdsPower скачивает или обновляет браузер (ядро), нужный профилю {said}. Подождите, пока " +
                                         "он закончит, и выполните шаг ещё раз.");
        }

        if (Has("proxy", "代理"))
        {
            return new AdsPowerException($"AdsPower не открыл {profile}: прокси профиля не прошёл проверку {said}. Проверьте прокси в " +
                                         "настройках профиля в AdsPower (адрес, логин и пароль, срок действия) — или он временно недоступен.");
        }

        if (Has("too many", "frequen", "rate limit", "per second", "频繁"))
        {
            return new AdsPowerException($"AdsPower ограничил частоту запросов {said}. Pepa делает паузы между своими запросами; если " +
                                         "к AdsPower обращается ещё одна программа, уменьшите её частоту или увеличьте паузу.");
        }

        if (Has("log in", "login", "logged", "sign in", "登录"))
        {
            return new AdsPowerException($"AdsPower не вошёл в аккаунт {said}. Откройте AdsPower и войдите в аккаунт.") { Retryable = false };
        }

        if (Has("not exist", "does not exist", "not found", "no such", "invalid user", "invalid profile", "user_id is invalid",
                "profile_id is invalid", "不存在"))
        {
            return new AdsPowerException($"AdsPower не нашёл {profile} {said}. Проверьте ID профиля в блоке: профиль могли удалить, или " +
                                         "он в группе, к которой у этого аккаунта AdsPower нет доступа.") { Retryable = false };
        }

        if (Has("limit", "exceed", "maximum", "上限"))
        {
            return new AdsPowerException($"AdsPower не открывает больше профилей одновременно {said} — предел тарифа или настроек. " +
                                         "Закройте другие профили или дождитесь, пока процессы их закроют.");
        }

        if (Has("used by", "in use", "occupied", "another device", "使用中"))
        {
            return new AdsPowerException($"AdsPower не открыл {profile}: он сейчас открыт у другого участника команды или на другом " +
                                         $"компьютере {said}. Дождитесь, пока его закроют, и выполните шаг ещё раз.");
        }

        if (Has("opening", "starting", "closing", "in progress", "being", "正在"))
        {
            return new AdsPowerException($"AdsPower ещё открывает или закрывает {profile} {said}. Подождите и выполните шаг ещё раз.");
        }

        return new AdsPowerException($"AdsPower отказал: «{message}». Запрос не выполнен.");
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
