using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Http;

public static class HttpNodes
{
    public static readonly NodeCategory Category = new("web", "Запросы к сайтам (API)", 15);

    /// <param name="client">
    /// Клиент HTTP (null — общий клиент Pepa). Переходы по перенаправлениям он не делает сам: «Получить» проходит их
    /// сам, а «Отправить» — нет (перенаправление после отправки — признак того, что сервер её уже обработал).
    /// </param>
    public static NodeRegistry RegisterHttpNodes(this NodeRegistry registry, HttpClient? client = null)
    {
        var http = client ?? SharedClient.Value;
        return registry.RegisterCategory(Category).Register(new HttpGetNode(http)).Register(new HttpSendNode(http));
    }

    private static readonly Lazy<HttpClient> SharedClient = new(() => CreateClient());

    /// <summary>Клиент для блоков: без собственных переходов и тайм-аута (время действия ограничивает движок).</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pepa/0.1");
        return client;
    }

    internal static readonly PortDescriptor Next = new(Ports.Out, "Далее");
    internal static readonly PortDescriptor Error = new(Ports.Error, "Ошибка", PortKind.Error);

    internal static FieldDescriptor UrlField => new()
    {
        Name = "url", Label = "Адрес", Kind = FieldKind.Template, Required = true,
        Placeholder = "https://example.com/api/orders/{{ vars.id }}",
        Help = "Полный адрес с https://. Значения для поиска лучше передать в «Параметрах адреса» — они закодируются сами.",
    };

    internal static FieldDescriptor QueryField => new()
    {
        Name = "query", Label = "Параметры адреса", Kind = FieldKind.Expression,
        Placeholder = "({ q: vars.text, page: 1 })",
        Help = "Объект: имена и значения добавятся к адресу после «?» (пробелы и знаки кодируются).",
    };

    internal static FieldDescriptor HeadersField => new()
    {
        Name = "headers", Label = "Заголовки", Kind = FieldKind.Expression,
        Placeholder = "({ Authorization: 'Bearer ' + vars.token })",
        Help = "Объект: имя заголовка — значение. Ключ API держите в секретной переменной — он не попадёт в историю.",
    };

    internal static FieldDescriptor AcceptField => new()
    {
        Name = "accept", Label = "Успешные ответы", Kind = FieldKind.Text, Default = "2xx",
        Placeholder = "2xx, 404",
        Help = "Коды ответа, после которых процесс идёт дальше: 2xx — любой от 200 до 299, через запятую — несколько. " +
               "«*» — любой ответ (код проверьте условием: vars.ответ.status).",
    };

    internal static FieldDescriptor SaveField => new()
    {
        Name = "save", Label = "Что сохранить", Kind = FieldKind.Select, Default = "body",
        Options = [new("body", "Тело ответа"), new("full", "Ответ целиком (код, заголовки, тело)")],
    };

    internal static FieldDescriptor SaveToField(bool required) => new()
    {
        Name = "saveTo", Label = "Сохранить ответ в переменную", Kind = FieldKind.Variable, Required = required,
    };

    internal static IEnumerable<NodeConfigIssue> ValidateCommon(NodeDefinition node)
    {
        if (node.Config.GetString("accept") is { } accept && !StatusSet.TryParse(accept, out _, out var problem))
        {
            yield return new NodeConfigIssue(problem!, "accept");
        }

        if (node.Config.GetString("url") is { Length: > 0 } url && !url.Contains("{{", StringComparison.Ordinal) &&
            !TryParseUrl(url, out _, out var urlProblem))
        {
            yield return new NodeConfigIssue(urlProblem!, "url");
        }
    }

    internal static bool TryParseUrl(string? text, out Uri uri, out string? problem)
    {
        uri = null!;
        problem = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            problem = "Не указан адрес.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            problem = $"«{trimmed}» — не адрес сайта: нужен полный адрес, начинающийся с https:// (или http://).";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>Адрес для сообщений: без параметров (в них бывают ключи доступа).</summary>
    internal static string Display(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    /// <summary>Готовый запрос: адрес с параметрами и заголовки (проверены до отправки).</summary>
    internal static HttpRequestMessage BuildRequest(NodeExecutionContext context, HttpMethod method, HttpContent? content)
    {
        if (!TryParseUrl(context.GetString("url"), out var uri, out var problem))
        {
            throw new NodeInputException(problem!);
        }

        if (context.Input["query"] is { } query)
        {
            uri = WithQuery(uri, query);
        }

        var request = new HttpRequestMessage(method, uri) { Content = content };
        switch (context.Input["headers"])
        {
            case null:
                break;
            case JsonObject headers:
                foreach (var (name, value) in headers)
                {
                    if (value is null)
                    {
                        continue;
                    }

                    var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
                    if (!IsToken(name))
                    {
                        throw new NodeInputException($"«{name}» — недопустимое имя заголовка.");
                    }

                    if (text.Contains('\r') || text.Contains('\n'))
                    {
                        throw new NodeInputException($"Значение заголовка «{name}» содержит перевод строки.");
                    }

                    if (!request.Headers.TryAddWithoutValidation(name, text))
                    {
                        if (content is null)
                        {
                            throw new NodeInputException($"Заголовок «{name}» относится к телу запроса, а тела нет.");
                        }

                        content.Headers.Remove(name);
                        if (!content.Headers.TryAddWithoutValidation(name, text))
                        {
                            throw new NodeInputException($"Заголовок «{name}» нельзя задать.");
                        }
                    }
                }

                break;
            default:
                throw new NodeInputException("«Заголовки» должны быть объектом: ({ Имя: 'значение' }).");
        }

        return request;
    }

    private static bool IsToken(string name) =>
        name.Length > 0 && name.All(c => c < 127 && c > 32 && !"()<>@,;:\\\"/[]?={}".Contains(c));

    internal static Uri WithQuery(Uri uri, JsonNode query)
    {
        if (query is not JsonObject values)
        {
            throw new NodeInputException("«Параметры адреса» должны быть объектом: ({ имя: значение }).");
        }

        var builder = new StringBuilder(uri.Query.TrimStart('?'));
        foreach (var (name, value) in values)
        {
            foreach (var item in value is JsonArray list ? list.ToArray() : [value])
            {
                if (item is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('&');
                }

                builder.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(Scalar(item)));
            }
        }

        return new UriBuilder(uri) { Query = builder.ToString() }.Uri;
    }

    internal static string Scalar(JsonNode node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() is JsonValueKind.True => "true",
        JsonValue v when v.GetValueKind() is JsonValueKind.False => "false",
        _ => node.ToJsonString(),
    };

    /// <summary>Тело ответа не больше этого — его сохраняют в переменную, а переменные хранятся в базе.</summary>
    internal const int MaxResponseBytes = 5 * 1024 * 1024;

    static HttpNodes() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Ответ, прочитанный целиком (или null в Body, если он больше предела или не текст).</summary>
    internal sealed record Response(int Status, string Reason, Uri Url, JsonObject Headers, JsonNode? Body, string? Text,
        long Bytes, bool TooLarge, bool Binary, string? ContentType);

    internal static async Task<Response> ReadAsync(HttpResponseMessage response, Uri url, CancellationToken cancellationToken)
    {
        var headers = new JsonObject();
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            headers[name.ToLowerInvariant()] = string.Join(", ", values);
        }

        var contentType = response.Content.Headers.ContentType;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        var tooLarge = false;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                tooLarge = true;
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
        var media = contentType?.MediaType?.ToLowerInvariant();
        if (tooLarge)
        {
            return new Response(status, reason, url, headers, null, null, total, true, false, media);
        }

        if (total > 0 && media is not null && !IsText(media))
        {
            return new Response(status, reason, url, headers, null, null, total, false, true, media);
        }

        var text = Decode(buffer.ToArray(), contentType?.CharSet);
        JsonNode? body = text;
        if (media is not null && (media.EndsWith("/json", StringComparison.Ordinal) || media.EndsWith("+json", StringComparison.Ordinal)) &&
            text.Length > 0)
        {
            try
            {
                body = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                body = text;
            }
        }

        return new Response(status, reason, url, headers, body, text, total, false, false, media);
    }

    private static bool IsText(string media) =>
        media.StartsWith("text/", StringComparison.Ordinal) || media.EndsWith("/json", StringComparison.Ordinal) ||
        media.EndsWith("+json", StringComparison.Ordinal) || media.EndsWith("/xml", StringComparison.Ordinal) ||
        media.EndsWith("+xml", StringComparison.Ordinal) || media is "application/javascript" or "application/x-www-form-urlencoded";

    private static string Decode(byte[] bytes, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
            }
            catch (ArgumentException)
            {
            }
        }

        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>Что сохранить в переменную: тело или ответ целиком.</summary>
    internal static JsonNode? Saved(NodeExecutionContext context, Response response) =>
        context.GetString("save") == "full"
            ? new JsonObject
            {
                ["status"] = response.Status,
                ["ok"] = response.Status is >= 200 and < 300,
                ["url"] = response.Url.ToString(),
                ["headers"] = response.Headers,
                ["body"] = response.Body?.DeepClone(),
            }
            : response.Body?.DeepClone();

    internal static JsonObject Output(HttpMethod method, Response response) => new()
    {
        ["method"] = method.Method,
        ["url"] = Display(response.Url),
        ["status"] = response.Status,
        ["bytes"] = response.Bytes,
        ["contentType"] = response.ContentType,
    };

    /// <summary>Начало текста ответа для сообщения об ошибке (сервер обычно объясняет, что не так).</summary>
    internal static string Excerpt(Response response)
    {
        var text = response.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = text.ReplaceLineEndings(" ");
        return ": " + (text.Length > 300 ? text[..300] + "…" : text);
    }

    internal static string BodyNote(Response response) => response.TooLarge
        ? $" Тело ответа больше {MaxResponseBytes / 1024 / 1024} МБ — не сохранено."
        : response.Binary
            ? $" Тело ответа — не текст ({response.ContentType}, {response.Bytes} байт) — не сохранено."
            : "";

    internal static string StatusText(Response response) => $"{response.Status} {response.Reason}";

    internal static string Describe(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "адрес сайта не найден (нет такого имени или нет интернета)",
        HttpRequestError.ConnectionError => "не удалось подключиться к серверу",
        HttpRequestError.SecureConnectionError => "не удалось установить защищённое соединение (сертификат сайта или настройки)",
        HttpRequestError.ProxyTunnelError => "не удалось подключиться через прокси",
        _ => ex.InnerException?.Message ?? ex.Message,
    };

    /// <summary>Ошибка до отправки запроса: соединение не установлено — сервер запроса не получал.</summary>
    internal static bool NotSent(HttpRequestException ex) => ex.HttpRequestError is HttpRequestError.NameResolutionError
        or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

/// <summary>Набор кодов ответа: «2xx», «200-299», «404», «*» — через запятую.</summary>
internal sealed class StatusSet
{
    private readonly List<(int From, int To)> _ranges;

    private StatusSet(List<(int From, int To)> ranges) => _ranges = ranges;

    public static readonly StatusSet Success = new([(200, 299)]);

    public bool Contains(int status) => _ranges.Any(r => status >= r.From && status <= r.To);

    public static bool TryParse(string? text, out StatusSet set, out string? problem)
    {
        set = Success;
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var ranges = new List<(int, int)>();
        foreach (var raw in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim().ToLowerInvariant();
            if (part is "*" or "любой")
            {
                ranges.Add((100, 599));
            }
            else if (part.Length == 3 && part[0] is >= '1' and <= '5' && part[1..] == "xx")
            {
                var hundred = (part[0] - '0') * 100;
                ranges.Add((hundred, hundred + 99));
            }
            else if (part.Split('-') is [var from, var to] && Code(from) is { } a && Code(to) is { } b && a <= b)
            {
                ranges.Add((a, b));
            }
            else if (Code(part) is { } code)
            {
                ranges.Add((code, code));
            }
            else
            {
                problem = $"«{raw.Trim()}» — не код ответа. Пример: 2xx, 404 или 200-204.";
                return false;
            }
        }

        set = new StatusSet(ranges);
        return true;
    }

    private static int? Code(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 100 and <= 599 ? code : null;
}
