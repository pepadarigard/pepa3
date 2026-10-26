using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Validation;

namespace Pepa.Assistant;

/// <summary>
/// ИИ-помощник конструктора: по описанию словами составляет черновик блоков через Claude API. Черновик проверяется той
/// же проверкой, что сценарий в конструкторе; найденные ошибки отправляются модели на исправление (не больше
/// <see cref="MaxRepairs"/> раз). Помощник только предлагает блоки — запускает сценарий пользователь.
/// </summary>
/// <remarks>
/// Модели уходят описание пользователя, имена и описания переменных сценария и каталог блоков Pepa; при правке и
/// объяснении — ещё блоки сценария с их полями (тексты, адреса, имена чатов). Значения переменных, данные процессов,
/// файлы и история — нет.
/// </remarks>
public sealed class ScenarioAssistant
{
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Длиннее описание — скорее всего, вставлен посторонний текст; попросим сократить.</summary>
    public const int MaxDescriptionLength = 8000;

    /// <summary>Сценарий больше этого (в формате ответа) помощнику не отправляем: ответ не уместился бы.</summary>
    public const int MaxScenarioLength = 60_000;

    private readonly ScenarioPrompt _prompt;
    private readonly DraftConverter _converter;

    public ScenarioAssistant(NodeRegistry registry, ScenarioValidator validator, IReadOnlyList<ScenarioDefinition>? examples = null)
    {
        _prompt = new ScenarioPrompt(registry, examples ?? []);
        _converter = new DraftConverter(registry, validator);
    }

    public string Model { get; init; } = DefaultModel;

    /// <summary>Адрес API (для проверок — имитация сервера). null — Anthropic.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Сколько раз просить модель исправить ошибки проверки.</summary>
    public int MaxRepairs { get; init; } = 2;

    /// <summary>Сколько ждать один ответ модели (она думает перед ответом).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Повторы запроса, когда сервис перегружен или вернул внутреннюю ошибку (ответа не было — ничего не потрачено). Тайм-аут
    /// не повторяется: модель могла дописать ответ, и повтор оплачивался бы ещё раз.
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Пауза перед повтором (растёт: 1×, 3×, 9×…).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<AssistantDraft> DraftAsync(string apiKey, AssistantRequest request, CancellationToken cancellationToken)
    {
        var description = request.Description?.Trim() ?? "";
        if (description.Length == 0)
        {
            throw new AssistantException(AssistantErrorKind.Input, "Опишите, что должен делать сценарий.");
        }

        if (description.Length > MaxDescriptionLength)
        {
            throw new AssistantException(AssistantErrorKind.Input,
                $"Описание слишком длинное ({description.Length} знаков, можно до {MaxDescriptionLength}). Опишите задачу короче.");
        }

        if (request.Current is { } current)
        {
            CheckScenario(current);
        }

        using var client = CreateClient(apiKey);
        var usage = new Usage();
        var answer = await AskAsync(client, ScenarioPrompt.FirstMessage(request), _prompt.Schema, usage, cancellationToken);
        var requests = 1;
        while (true)
        {
            var draft = Parse<DraftJson>(answer);
            var converted = _converter.Convert(draft, request.ExistingVariables, request.Current);
            if (converted.Problems.Count == 0 || requests > MaxRepairs)
            {
                return new AssistantDraft
                {
                    Summary = draft.Summary.Trim(),
                    Questions = draft.Questions.Select(q => q.Trim()).Where(q => q.Length > 0).ToList(),
                    Variables = converted.Variables,
                    Nodes = converted.Nodes,
                    Edges = converted.Edges,
                    EntryNodeId = converted.EntryNodeId,
                    Problems = converted.Problems,
                    Requests = requests,
                    Usage = usage.Total,
                };
            }

            answer = await AskAsync(client, ScenarioPrompt.RepairMessage(request, answer, converted.Problems), _prompt.Schema, usage, cancellationToken);
            requests++;
        }
    }

    /// <summary>Объяснить сценарий простыми словами: что делает, по шагам, что проверить. Сценарий не меняется.</summary>
    public async Task<AssistantExplanation> ExplainAsync(string apiKey, ScenarioDefinition scenario, CancellationToken cancellationToken)
    {
        CheckScenario(scenario);
        using var client = CreateClient(apiKey);
        var usage = new Usage();
        var answer = Parse<ExplanationJson>(await AskAsync(client, ScenarioPrompt.ExplainMessage(scenario), _prompt.ExplainSchema, usage, cancellationToken));
        var ids = scenario.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        return new AssistantExplanation
        {
            Summary = answer.Summary.Trim(),
            Steps = answer.Steps.Where(s => s.Text.Trim().Length > 0)
                .Select(s => new ExplainedBlock(ids.Contains(s.Block.Trim()) ? s.Block.Trim() : null, s.Text.Trim())).ToList(),
            Cautions = answer.Cautions.Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
            Usage = usage.Total,
        };
    }

    /// <summary>
    /// Повторять можно, только когда ответа точно не было: лимит запросов, перегрузка (529), внутренняя ошибка или
    /// недоступность сервиса. Ошибки шлюза (502, 504) — нет: за шлюзом запрос мог выполниться и быть оплачен.
    /// </summary>
    private static bool Retryable(Exception ex) => ex switch
    {
        AnthropicRateLimitException => true,
        Anthropic5xxException server => (int)server.StatusCode is 500 or 503 or 529,
        _ => false,
    };

    private static void CheckScenario(ScenarioDefinition scenario)
    {
        if (!scenario.Nodes.Any(n => n.Type != "start"))
        {
            throw new AssistantException(AssistantErrorKind.Input, "В сценарии пока нет блоков — объяснять и менять нечего.");
        }

        var length = ScenarioPrompt.ScenarioJson(scenario).Length;
        if (length > MaxScenarioLength)
        {
            throw new AssistantException(AssistantErrorKind.Input,
                $"Сценарий слишком большой для помощника ({length} знаков, можно до {MaxScenarioLength}). Разделите его на части (вызов сценария).");
        }
    }

    /// <summary>Проверить ключ, ничего не тратя: запрос сведений о модели.</summary>
    public async Task<string> CheckKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var client = CreateClient(apiKey);
        try
        {
            var model = await client.Models.Retrieve(Model, cancellationToken: cancellationToken);
            return model.DisplayName is { Length: > 0 } name ? name : model.ID;
        }
        catch (Exception ex) when (Translate(ex) is { } error)
        {
            throw error;
        }
    }

    private AnthropicClient CreateClient(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AssistantException(AssistantErrorKind.Key, "Ключ API Anthropic не задан. Укажите его в «Настройках» → «ИИ-помощник».");
        }

        return BaseUrl is { } url
            ? new AnthropicClient { ApiKey = apiKey.Trim(), Timeout = RequestTimeout, MaxRetries = 0, BaseUrl = url }
            : new AnthropicClient { ApiKey = apiKey.Trim(), Timeout = RequestTimeout, MaxRetries = 0 };
    }

    private async Task<string> AskAsync(AnthropicClient client, string message, IReadOnlyDictionary<string, JsonElement> schema, Usage usage,
        CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            // Отказ по правилам безопасности сервер повторяет на подходящей модели сам.
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            // Указания (каталог блоков) одинаковы для всех запросов — кешируются: исправления и следующие запросы дешевле.
            System = new List<BetaTextBlockParam> { new() { Text = _prompt.System, CacheControl = new BetaCacheControlEphemeral() } },
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.High,
                Format = new BetaJsonOutputFormat { Schema = new Dictionary<string, JsonElement>(schema) },
            },
            Messages = [new BetaUserMessageParam { Content = message }],
        };

        BetaMessage response;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                response = await client.Beta.Messages.Create(parameters, cancellationToken);
                break;
            }
            catch (Exception ex) when (Retryable(ex) && attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay * Math.Pow(3, attempt), cancellationToken);
            }
            catch (Exception ex) when (Translate(ex) is { } error)
            {
                throw error;
            }
        }

        usage.Add(response.Usage.InputTokens + (response.Usage.CacheCreationInputTokens ?? 0), response.Usage.OutputTokens,
            response.Usage.CacheReadInputTokens ?? 0);
        var stop = response.StopReason?.Raw();
        if (stop == "refusal")
        {
            throw new AssistantException(AssistantErrorKind.Answer,
                "Модель отказалась составлять этот сценарий. Переформулируйте описание или составьте блоки вручную.");
        }

        if (stop == "max_tokens")
        {
            throw new AssistantException(AssistantErrorKind.Answer,
                "Сценарий получился слишком большим для одного ответа. Опишите его по частям (например, сначала вход на сайт, потом остальное).");
        }

        var text = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var part))
            {
                text.Append(part.Text);
            }
        }

        return text.Length > 0
            ? text.ToString()
            : throw new AssistantException(AssistantErrorKind.Answer, "Модель не прислала черновик. Попробуйте ещё раз.");
    }

    private static T Parse<T>(string answer) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(answer, DraftSerializer.Options)
                   ?? throw new AssistantException(AssistantErrorKind.Answer, "Модель прислала пустой ответ.");
        }
        catch (JsonException ex)
        {
            throw new AssistantException(AssistantErrorKind.Answer, "Ответ модели не удалось разобрать. Попробуйте ещё раз.", ex);
        }
    }

    /// <summary>Ошибки SDK → понятные причины. Остальное (ошибки Pepa) не трогаем.</summary>
    private static AssistantException? Translate(Exception ex) => ex switch
    {
        AssistantException => null,
        AnthropicUnauthorizedException => new AssistantException(AssistantErrorKind.Key,
            "Ключ API Anthropic не подошёл. Проверьте его в «Настройках» → «ИИ-помощник» (ключ выдают на console.anthropic.com).", ex),
        AnthropicForbiddenException => new AssistantException(AssistantErrorKind.Key,
            "Этому ключу API запрещено пользоваться моделью. Проверьте права ключа на console.anthropic.com.", ex),
        AnthropicNotFoundException => new AssistantException(AssistantErrorKind.Key,
            "Модель помощника недоступна для этого ключа. Проверьте ключ на console.anthropic.com.", ex),
        AnthropicRateLimitException => new AssistantException(AssistantErrorKind.Unavailable,
            "Слишком много запросов к Anthropic или исчерпан лимит. Подождите немного и попробуйте снова.", ex),
        Anthropic5xxException => new AssistantException(AssistantErrorKind.Unavailable,
            "Сервис Anthropic сейчас перегружен или недоступен. Попробуйте через несколько минут.", ex),
        AnthropicBadRequestException => new AssistantException(AssistantErrorKind.Answer,
            $"Anthropic отклонил запрос: {ex.Message}", ex),
        AnthropicIOException or HttpRequestException => new AssistantException(AssistantErrorKind.Unavailable,
            "Нет связи с Anthropic. Проверьте интернет.", ex),
        TaskCanceledException or TimeoutException => new AssistantException(AssistantErrorKind.Unavailable,
            "Anthropic не ответил вовремя. Попробуйте ещё раз.", ex),
        AnthropicException => new AssistantException(AssistantErrorKind.Unavailable, $"Ошибка Anthropic: {ex.Message}", ex),
        _ => null,
    };

    private sealed class Usage
    {
        private long _input;
        private long _output;
        private long _cached;

        public AssistantUsage Total => new(_input, _output, _cached);

        public void Add(long input, long output, long cached)
        {
            _input += input;
            _output += output;
            _cached += cached;
        }
    }
}
