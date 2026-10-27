using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Http;

/// <summary>
/// Получить страницу или данные API (GET). Только читает: при сбое повтор безопасен. Перенаправления проходит сам:
/// на другой сайт — без заголовков (кроме безобидных), с https на http — не переходит.
/// </summary>
public sealed class HttpGetNode(HttpClient client) : INodeExecutor
{
    public const string TypeName = "http.get";
    private const int MaxRedirects = 10;

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = HttpNodes.Category.Id,
        Title = "Получить по адресу",
        Description = "Загружает данные по адресу (GET) — ответ API, страницу — и сохраняет в переменную. Только читает: " +
                      "при сбое запрос безопасно повторяется.",
        Icon = "cloud-download",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "url",
        Outputs = [HttpNodes.Next, HttpNodes.Error],
        Fields =
        [
            HttpNodes.UrlField,
            HttpNodes.QueryField,
            HttpNodes.HeadersField,
            HttpNodes.AcceptField,
            HttpNodes.SaveField,
            HttpNodes.SaveToField(required: true),
        ],
    };

    public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node) => HttpNodes.ValidateCommon(node);

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (!StatusSet.TryParse(context.GetString("accept"), out var accept, out var problem))
        {
            throw new NodeInputException(problem!);
        }

        var saveTo = context.RequireString("saveTo");
        using var first = HttpNodes.BuildRequest(context, HttpMethod.Get, null);
        var request = first;
        var origin = first.RequestUri!;
        // Куки, которые сайт ставит по дороге (вход с перенаправлениями), — только на время этого запроса.
        var cookies = new System.Net.CookieContainer();
        HttpResponseMessage? response = null;
        try
        {
            for (var hop = 0; ; hop++)
            {
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    return NodeResult.Fail($"Не удалось получить «{HttpNodes.Display(request.RequestUri!)}»: {HttpNodes.Describe(ex)}.",
                        retryable: ex.HttpRequestError is not (HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError));
                }
                catch (OperationCanceledException ex) when (HttpNodes.ConnectTimedOut(ex, cancellationToken))
                {
                    return NodeResult.Fail($"Не удалось получить «{HttpNodes.Display(request.RequestUri!)}»: {HttpNodes.ConnectTimeoutText}.");
                }

                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var header in setCookies)
                    {
                        try
                        {
                            cookies.SetCookies(request.RequestUri!, HostOnly(header));
                        }
                        catch (System.Net.CookieException)
                        {
                            // Неправильная кука — без неё (как у браузера).
                        }
                    }
                }

                if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                {
                    break;
                }

                if (hop == MaxRedirects)
                {
                    return NodeResult.Fail($"«{HttpNodes.Display(origin)}» перенаправляет больше {MaxRedirects} раз подряд.", retryable: false);
                }

                var next = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
                if (next.Scheme is not ("http" or "https"))
                {
                    return NodeResult.Fail($"«{HttpNodes.Display(request.RequestUri!)}» перенаправляет не на адрес сайта (схема «{next.Scheme}»).",
                        retryable: false);
                }

                if (request.RequestUri!.Scheme == "https" && next.Scheme == "http")
                {
                    return NodeResult.Fail($"«{HttpNodes.Display(request.RequestUri)}» перенаправляет на незащищённый адрес «{HttpNodes.Display(next)}» " +
                                           "— Pepa туда не переходит: заголовки и ответ можно было бы подсмотреть.", retryable: false);
                }

                var redirected = new HttpRequestMessage(HttpMethod.Get, next);
                foreach (var (name, values) in first.Headers)
                {
                    // Заголовки (а в них бывают ключи доступа под любым именем) — только тому сайту, которому их дали; другому —
                    // только безобидные.
                    if (!SameOrigin(origin, next) && !IsHarmless(name))
                    {
                        continue;
                    }

                    if (!name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        redirected.Headers.TryAddWithoutValidation(name, values);
                    }
                }

                // Свои куки — тому же сайту (кроме тех, что сайт по дороге заменил), полученные по дороге — по правилам самих
                // кук (сайт и путь).
                var renewed = cookies.GetCookies(next).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
                var cookie = string.Join("; ", new[]
                {
                    SameOrigin(origin, next) && first.Headers.TryGetValues("Cookie", out var own)
                        ? string.Join("; ", own.SelectMany(v => v.Split(';')).Select(p => p.Trim())
                            .Where(p => p.Length > 0 && !renewed.Contains(p.Split('=')[0].Trim())))
                        : "",
                    cookies.GetCookieHeader(next),
                }.Where(c => c.Length > 0));
                if (cookie.Length > 0)
                {
                    redirected.Headers.TryAddWithoutValidation("Cookie", cookie);
                }

                response.Dispose();
                response = null;
                if (!ReferenceEquals(request, first))
                {
                    request.Dispose();
                }

                request = redirected;
            }

            var read = await HttpNodes.ReadAsync(response, request.RequestUri!, cancellationToken);
            var address = HttpNodes.Display(read.Url);
            if (!accept.Contains(read.Status))
            {
                var retryable = read.Status is >= 500 or 408 or 429;
                return NodeResult.Fail($"«{address}» ответил {HttpNodes.StatusText(read)}{HttpNodes.Excerpt(read)}", retryable);
            }

            if (read.TooLarge || read.Binary)
            {
                return NodeResult.Fail($"«{address}» ответил {HttpNodes.StatusText(read)}.{HttpNodes.BodyNote(read)}", retryable: false);
            }

            var note = HttpNodes.BodyNote(read);
            return new NodeResult.Success(Ports.Out, new Dictionary<string, JsonNode?> { [saveTo] = HttpNodes.Saved(context, read) },
                HttpNodes.Output(HttpMethod.Get, read), $"GET {address} → {HttpNodes.StatusText(read)}{(note.Length > 0 ? "." + note : "")}",
                note.Length > 0 ? MessageLevel.Warning : MessageLevel.Info);
        }
        finally
        {
            response?.Dispose();
            if (!ReferenceEquals(request, first))
            {
                request.Dispose();
            }
        }
    }

    /// <summary>
    /// Кука — только для сайта, который её поставил: «Domain=» убирается (у Pepa нет списка общих доменов вроде co.uk или
    /// github.io, как у браузера). Кука www.example.com с «Domain=example.com» остаётся куке www.example.com.
    /// </summary>
    private static string HostOnly(string header) =>
        string.Join(";", header.Split(';').Where((part, index) =>
            index == 0 || !part.Split('=', 2)[0].Trim().Equals("domain", StringComparison.OrdinalIgnoreCase)));

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
        HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static readonly HashSet<string> HarmlessHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Accept-Encoding", "User-Agent", "Cache-Control", "Pragma",
    };

    private static bool IsHarmless(string header) => HarmlessHeaders.Contains(header);
}

/// <summary>
/// Отправить запрос (POST, PUT, PATCH, DELETE): создать заказ, отправить данные. Меняет что-то на сервере — поэтому
/// без ответа запрос сам не повторяется: решает пользователь. Исключение — сервер с ключом от повтора
/// (Idempotency-Key): повтор с тем же ключом сервер второй раз не выполнит, поэтому потерянный ответ и ошибка сервера
/// — обычная ошибка с повтором по настройкам блока. Проверить сам результат блок не умеет (у сервера не спросить).
/// </summary>
public sealed class HttpSendNode(HttpClient client) : INodeExecutor
{
    public const string TypeName = "http.send";
    public const string IdempotencyHeader = "Idempotency-Key";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = HttpNodes.Category.Id,
        Title = "Отправить запрос",
        Description = "Отправляет запрос к сайту или API (POST, PUT, PATCH, DELETE) — например, создать заказ. Если ответ " +
                      "не получен, запрос сам не повторяется: выполнил ли его сервер, решаете вы.",
        Icon = "cloud-upload",
        Effect = NodeEffect.External,
        SummaryField = "url",
        Outputs = [HttpNodes.Next, HttpNodes.Error],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "method", Label = "Метод", Kind = FieldKind.Select, Default = "POST",
                Options = [new("POST", "POST — создать, отправить"), new("PUT", "PUT — заменить"), new("PATCH", "PATCH — изменить"), new("DELETE", "DELETE — удалить")],
            },
            HttpNodes.UrlField,
            HttpNodes.QueryField,
            HttpNodes.HeadersField,
            new FieldDescriptor
            {
                Name = "bodyType", Label = "Тело запроса", Kind = FieldKind.Select, Default = "json",
                Options = [new("json", "JSON"), new("form", "Форма (как с сайта)"), new("text", "Текст"), new("none", "Без тела")],
            },
            new FieldDescriptor
            {
                Name = "body", Label = "Данные", Kind = FieldKind.Expression,
                Placeholder = "({ name: vars.name, amount: 100 })",
                Help = "Выражение: объект или список. Для формы — объект «имя: значение».",
                VisibleWhen = new FieldCondition("bodyType", ["json", "form"]),
            },
            new FieldDescriptor
            {
                Name = "text", Label = "Текст", Kind = FieldKind.MultilineTemplate,
                VisibleWhen = new FieldCondition("bodyType", ["text"]),
            },
            HttpNodes.AcceptField,
            new FieldDescriptor
            {
                Name = "idempotent", Label = "Сервер принимает ключ от повтора (Idempotency-Key)", Kind = FieldKind.Boolean, Default = false,
                Help = "Включайте, только если сервер это поддерживает (например, платёжные API). Тогда после сбоя Pepa " +
                       "повторит запрос с тем же ключом, и сервер не выполнит его второй раз. Без этого после сбоя решаете вы.",
            },
            HttpNodes.SaveField,
            HttpNodes.SaveToField(required: false),
        ],
    };

    public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node) => HttpNodes.ValidateCommon(node);

    /// <summary>
    /// Ключ от повтора — один на все попытки этого шага, даже если данные запроса посчитались иначе (время, случайное
    /// число): новый ключ сервер принял бы за новый запрос и выполнил его второй раз. Тот же ключ с другими данными сервер
    /// отклонит (это «итог неизвестен» — решаете вы), но дважды не выполнит.
    /// </summary>
    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.GetBoolean("idempotent"))
        {
            return ValueTask.FromResult<JsonObject?>(null);
        }

        var key = context.PreviousCheckpoint?.GetString("key") is { Length: > 0 } kept ? kept : Guid.NewGuid().ToString("D");
        return ValueTask.FromResult<JsonObject?>(new JsonObject { ["key"] = key });
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (!StatusSet.TryParse(context.GetString("accept"), out var accept, out var problem))
        {
            throw new NodeInputException(problem!);
        }

        var method = Method(context);
        var idempotent = context.GetBoolean("idempotent");
        using var request = HttpNodes.BuildRequest(context, method, Content(context));
        if (idempotent && !request.Headers.Contains(IdempotencyHeader))
        {
            var key = context.Checkpoint?.GetString("key") ?? throw new NodeInputException("Нет ключа от повтора.");
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, key);
        }

        var address = HttpNodes.Display(request.RequestUri!);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex) when (HttpNodes.NotSent(ex))
        {
            return NodeResult.Fail($"Запрос к «{address}» не отправлен: {HttpNodes.Describe(ex)}.",
                retryable: ex.HttpRequestError is not (HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError));
        }
        catch (OperationCanceledException ex) when (HttpNodes.ConnectTimedOut(ex, cancellationToken))
        {
            return NodeResult.Fail($"Запрос к «{address}» не отправлен: {HttpNodes.ConnectTimeoutText}.");
        }
        catch (HttpRequestException ex)
        {
            return Unknown($"Запрос к «{address}» отправлен, но ответ не получен ({HttpNodes.Describe(ex)}) — выполнил ли его сервер, неизвестно.");
        }

        using (response)
        {
            HttpNodes.Response read;
            var note = "";
            try
            {
                read = await HttpNodes.ReadAsync(response, request.RequestUri!, cancellationToken);
                note = HttpNodes.BodyNote(read);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // Код ответа уже известен — сервер запрос обработал; не дочитано только тело (оборвалось или вышло время).
                read = new HttpNodes.Response((int)response.StatusCode, response.ReasonPhrase ?? response.StatusCode.ToString(),
                    request.RequestUri!, new JsonObject(), null, null, 0, false, false, response.Content.Headers.ContentType?.MediaType);
                note = ex is OperationCanceledException
                    ? " Тело ответа не дочитано (время шага вышло) — не сохранено."
                    : $" Тело ответа не дочитано ({ex.Message}) — не сохранено.";
            }

            var status = HttpNodes.StatusText(read);
            if (accept.Contains(read.Status))
            {
                var updates = new Dictionary<string, JsonNode?>();
                if (context.GetString("saveTo") is { Length: > 0 } saveTo)
                {
                    updates[saveTo] = HttpNodes.Saved(context, read);
                }

                return new NodeResult.Success(Ports.Out, updates, HttpNodes.Output(method, read), $"{method.Method} {address} → {status}.{note}",
                    note.Length > 0 ? MessageLevel.Warning : MessageLevel.Info);
            }

            var location = response.Headers.Location is { } l ? HttpNodes.Display(l.IsAbsoluteUri ? l : new Uri(request.RequestUri!, l)) : "?";
            return read.Status switch
            {
                307 or 308 => NodeResult.Fail(
                    $"Сервер ответил {status}: запрос нужно отправлять на «{location}». Запрос не выполнен — исправьте адрес.", retryable: false),
                >= 300 and < 400 => new NodeResult.Unknown(
                        $"Сервер ответил перенаправлением {status} на «{location}» — так обычно отвечают после выполненного запроса. " +
                        $"Если это ожидаемый ответ, добавьте {read.Status} в «Успешные ответы».")
                    { AlreadyChecked = true },
                408 or 429 => NodeResult.Fail($"Сервер не принял запрос сейчас: {status}{HttpNodes.Excerpt(read)}"),
                // С ключом от повтора 409 — «запрос с этим ключом ещё выполняется», 422 — «ключ уже был с другими данными».
                409 when idempotent => Unknown($"Сервер ответил {status}{HttpNodes.Excerpt(read)} — запрос с этим ключом от повтора, " +
                                               "похоже, ещё выполняется."),
                422 when idempotent => new NodeResult.Unknown($"Сервер ответил {status}{HttpNodes.Excerpt(read)} — возможно, ключ от " +
                                                              "повтора уже был с другими данными. Проверьте на сайте и решите."),
                < 500 => NodeResult.Fail($"Сервер отклонил запрос: {status}{HttpNodes.Excerpt(read)}", retryable: false),
                _ => Unknown($"Сервер ответил ошибкой {status}{HttpNodes.Excerpt(read)} — выполнил ли он запрос, неизвестно."),
            };
        }

        // Неизвестный итог: без ключа от повтора — решает пользователь. С ключом повтор безопасен (сервер не выполнит
        // запрос второй раз) — ошибка с повтором «могло выполниться», пока по настройкам блока остались повторы: по этой
        // отметке движок и окончательную ошибку следующей попытки («не отправлен», отказ) сочтёт «итогом неизвестен», а не
        // поведёт по выходу «Ошибка». Последняя попытка — сразу «итог неизвестен».
        NodeResult Unknown(string reason) => idempotent && context.Attempt <= context.Node.Settings.RetryCount
            ? new NodeResult.Failure(reason + " Повтор безопасен: запрос уйдёт с тем же ключом от повтора.") { MaybeExecuted = true }
            : new NodeResult.Unknown(reason + (idempotent
                ? " Повтор безопасен (тот же ключ от повтора) — повторите или проверьте на сайте."
                : " Проверьте на сайте и решите: повторить или считать выполненным."));
    }

    private static HttpMethod Method(NodeExecutionContext context) => context.GetString("method")?.ToUpperInvariant() switch
    {
        null or "" or "POST" => HttpMethod.Post,
        "PUT" => HttpMethod.Put,
        "PATCH" => HttpMethod.Patch,
        "DELETE" => HttpMethod.Delete,
        var other => throw new NodeInputException($"Метод «{other}» не поддерживается: POST, PUT, PATCH или DELETE."),
    };

    private static HttpContent? Content(NodeExecutionContext context)
    {
        switch (context.GetString("bodyType") ?? "json")
        {
            case "none":
                return null;
            case "text":
                return new StringContent(context.GetString("text") ?? "", Encoding.UTF8, "text/plain");
            case "form":
                if (context.Input["body"] is not JsonObject form)
                {
                    throw new NodeInputException("Для формы «Данные» должны быть объектом: ({ имя: 'значение' }).");
                }

                return new FormUrlEncodedContent(form.Where(p => p.Value is not null)
                    .SelectMany(p => (p.Value is JsonArray list ? list.ToArray() : [p.Value]).OfType<JsonNode>()
                        .Select(v => new KeyValuePair<string, string>(p.Key, HttpNodes.Scalar(v)))).ToList());
            case "json":
                return context.Input["body"] is { } json
                    ? new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json")
                    : null;
            case var other:
                throw new NodeInputException($"Неизвестный вид тела запроса «{other}».");
        }
    }
}
