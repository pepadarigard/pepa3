using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Assistant;

/// <summary>
/// Указания для модели: что такое Pepa, правила надёжности, каталог блоков (из реестра — тот же, что видит конструктор),
/// примеры и схема ответа. Каталог меняется только вместе с версией Pepa, поэтому указания кешируются.
/// </summary>
internal sealed class ScenarioPrompt
{
    private readonly NodeRegistry _registry;
    private readonly IReadOnlyList<ScenarioDefinition> _examples;

    public ScenarioPrompt(NodeRegistry registry, IReadOnlyList<ScenarioDefinition> examples)
    {
        _registry = registry;
        _examples = examples;
        BlockTypes = registry.Descriptors.Where(d => !d.IsStart).Select(d => d.Type).Order(StringComparer.Ordinal).ToList();
        System = BuildSystem();
        Schema = BuildSchema();
        ExplainSchema = BuildExplainSchema();
    }

    /// <summary>Типы блоков, которые можно ставить (кроме «Начала» — оно в сценарии уже есть).</summary>
    public IReadOnlyList<string> BlockTypes { get; }

    public string System { get; }

    public IReadOnlyDictionary<string, JsonElement> Schema { get; }

    /// <summary>Схема ответа на «объясни сценарий».</summary>
    public IReadOnlyDictionary<string, JsonElement> ExplainSchema { get; }

    public static string FirstMessage(AssistantRequest request)
    {
        if (request.Current is { } current)
        {
            return EditMessage(request, current);
        }

        var text = new StringBuilder();
        text.AppendLine("Составь блоки сценария Pepa по описанию пользователя.");
        AppendVariables(text, request.ExistingVariables);
        text.AppendLine();
        text.AppendLine("<описание>");
        text.AppendLine(request.Description.Trim());
        text.AppendLine("</описание>");
        return text.ToString();
    }

    /// <summary>Правка существующего сценария: сценарий в формате ответа и просьба пользователя.</summary>
    private static string EditMessage(AssistantRequest request, ScenarioDefinition current)
    {
        var text = new StringBuilder();
        text.AppendLine("Измени сценарий Pepa по просьбе пользователя и верни ПОЛНЫЙ сценарий в формате ответа: все блоки (кроме " +
                        "«Начала») и все связи (кроме связи из «Начала»), а не только изменения.");
        text.AppendLine("- Что не просили менять — оставь как есть: те же id, типы, подписи, заметки и поля.");
        text.AppendLine("- У новых блоков — новые id, которых в сценарии нет. Удаляй блоки, только если об этом просят или без этого не обойтись.");
        if (EntryOf(current) is { } entry)
        {
            text.AppendLine($"- Сейчас «Начало» ведёт к блоку «{entry}». Первый блок должен остаться без входящих связей — к нему Pepa подключит «Начало».");
        }

        text.AppendLine("- В summary — что именно изменено. В variables — только новые переменные.");
        AppendVariables(text, request.ExistingVariables);
        text.AppendLine();
        text.AppendLine("<сценарий>");
        text.AppendLine(ScenarioJson(current));
        text.AppendLine("</сценарий>");
        text.AppendLine();
        text.AppendLine("<просьба>");
        text.AppendLine(request.Description.Trim());
        text.AppendLine("</просьба>");
        return text.ToString();
    }

    /// <summary>Объяснить сценарий простыми словами — без правок.</summary>
    public static string ExplainMessage(ScenarioDefinition scenario)
    {
        var text = new StringBuilder();
        text.AppendLine("Объясни пользователю простыми словами, что делает этот сценарий Pepa. Ничего не меняй и не предлагай новых блоков.");
        text.AppendLine("- summary — 2–4 предложения: зачем сценарий и как он идёт в целом.");
        text.AppendLine("- steps — по одному пояснению на блок в порядке выполнения: block — id блока из сценария, text — что он делает и " +
                        "куда процесс идёт дальше (особенно при ошибке, тайм-ауте или ответе «нет»).");
        text.AppendLine("- cautions — что стоит проверить: неподключённые исходы, заглушки и неизвестные значения, рискованные места " +
                        "(например, действие без проверки результата). Может быть пусто.");
        AppendVariables(text, scenario.Variables);
        text.AppendLine();
        text.AppendLine("<сценарий>");
        text.AppendLine(ScenarioJson(scenario));
        text.AppendLine("</сценарий>");
        return text.ToString();
    }

    /// <summary>Сценарий в формате ответа: блоки с полями и связи; значения переменных не передаются.</summary>
    internal static string ScenarioJson(ScenarioDefinition scenario) =>
        JsonSerializer.Serialize(ToDraft(scenario) with { Variables = [] }, DraftSerializer.Options);

    /// <summary>Блок, к которому ведёт «Начало».</summary>
    internal static string? EntryOf(ScenarioDefinition scenario)
    {
        var start = scenario.Nodes.FirstOrDefault(n => n.Type == "start")?.Id;
        return start is null ? null : scenario.Edges.FirstOrDefault(e => e.Source == start)?.Target;
    }

    public static string RepairMessage(AssistantRequest request, string previousJson, IReadOnlyList<string> problems)
    {
        var text = new StringBuilder();
        text.AppendLine(request.Current is null
            ? "Ты составил черновик по описанию пользователя, но проверка Pepa нашла в нём ошибки. Исправь их и верни " +
              "полный исправленный черновик в том же формате (не только изменения). Остальное оставь как было."
            : "Ты изменил сценарий по просьбе пользователя, но проверка Pepa нашла в нём ошибки. Исправь их и верни полный " +
              "исправленный сценарий в том же формате (не только изменения). Остальное оставь как было.");
        AppendVariables(text, request.ExistingVariables);
        text.AppendLine();
        text.AppendLine(request.Current is null ? "<описание>" : "<просьба>");
        text.AppendLine(request.Description.Trim());
        text.AppendLine(request.Current is null ? "</описание>" : "</просьба>");
        text.AppendLine();
        text.AppendLine("<черновик>");
        text.AppendLine(previousJson.Trim());
        text.AppendLine("</черновик>");
        text.AppendLine();
        text.AppendLine("<ошибки>");
        foreach (var problem in problems)
        {
            text.Append("- ").AppendLine(problem);
        }

        text.AppendLine("</ошибки>");
        return text.ToString();
    }

    private static void AppendVariables(StringBuilder text, IReadOnlyList<VariableDefinition> variables)
    {
        if (variables.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("В сценарии уже есть переменные (их можно использовать, объявлять заново не нужно):");
        foreach (var variable in variables)
        {
            text.Append("- ").Append(variable.Name).Append(" (").Append(TypeName(variable.Type)).Append(')');
            if (variable.Description.Length > 0)
            {
                text.Append(": ").Append(variable.Description);
            }

            text.AppendLine();
        }
    }

    private string BuildSystem()
    {
        var text = new StringBuilder();
        text.AppendLine("""
            Ты помогаешь пользователю платформы автоматизаций Pepa (Windows) составлять сценарии. Сценарий — схема из блоков,
            соединённых связями: процесс идёт от блока к блоку по выходам (портам). Пользователь описывает задачу словами по-русски,
            ты возвращаешь черновик блоков. Черновик вставляется в редактор схемы: пользователь проверит его, поправит и сохранит
            сам. Ничего не запускается автоматически.

            Правила Pepa, которые важно соблюдать:
            - Pepa делает только то, что задано в схеме: никаких «на всякий случай» действий, которых пользователь не просил.
            - Неожиданная ситуация — остановка с понятной причиной, а не попытка угадать. Не подключай выход «Ошибка» к
              продолжению, если пользователь не просил обрабатывать ошибку; без подключения процесс сам остановится и спросит.
            - Повторы шагов (settings) не настраивай: их выбирает пользователь.
            - Если какое-то значение неизвестно (какой чат, какой профиль AdsPower, какой селектор на сайте), не выдумывай:
              вынеси его во входную переменную (isInput = true) с понятным описанием или поставь явную заглушку и задай вопрос
              в questions.
            - Селекторы сайтов ты не видишь. Используй устойчивые по смыслу: text=Войти, button:has-text("Отправить"),
              input[name=email], [placeholder="Поиск"] — и попроси проверить их (в note блока или в questions). Напомни, что
              точные селекторы удобно получить кнопкой «Записать» в конструкторе.
            - Блоки работают с браузером в текущей сессии: сначала «Открыть браузер» (browser.open) или «Открыть профиль
              AdsPower» (adspower.open), затем блоки страницы.
            - Один профиль AdsPower занят одним процессом; закрывать профиль в конце — хорошая практика, если пользователь
              не просил оставить его открытым.
            - Если нужно одновременно ждать и работать (например, ждать ответ в Telegram, пока идут действия на сайте),
              используй parallel (ветви b1–b4) и join: все ветви приходят в один и тот же join, в середину ветви нельзя
              входить снаружи, ветви не сливаются раньше join. Шаги ветвей выполняются по очереди, одновременно идут ожидания.

            Значения полей:
            - Поля-шаблоны (вид «шаблон», «многострочный шаблон», «путь») — текст с подстановками {{ выражение }}, например
              «Заказ {{ vars.order }} готов». В подстановках доступны vars (переменные процесса), process (id, scenarioName,
              startedAt) и lastError (последняя ошибка).
            - Поля «выражение» и «условие» — выражения JavaScript без {{ }}: vars.count > 3, vars.reply.payload.text.includes("да").
            - Поле «условие события» проверяется на пришедшем событии: event.payload.text, event.correlationKey, а также vars.
            - Поле «код» — JavaScript для страницы; переменные — vars.имя, без {{ }}.
            - Поле «переменная» — имя переменной сценария (без vars.). Каждую переменную, которую используешь, объяви в
              variables (если её ещё нет в сценарии). Имена — латиницей или кириллицей, без пробелов (например order, reply).
            - Числа и секунды — числом («30»), да/нет — true или false, выбор — одно из перечисленных значений (value).
            - Поля, которые не нужны (значение по умолчанию подходит), не указывай.
            - Результаты блоков «Ждать сообщение» и других ожиданий события сохраняются объектом события: текст сообщения —
              vars.имя.payload.text.

            Формат ответа — JSON строго по схеме:
            - summary — 1–3 предложения: что делает черновик.
            - questions — что пользователю нужно уточнить или проверить (может быть пусто).
            - variables — новые переменные: name, type (any, text, number, boolean, list, object), description, isInput
              (true — спрашивается при запуске), default (значение по умолчанию строкой или пустая строка).
            - blocks — блоки по порядку выполнения: id (короткий латиницей: open, send, wait…), type (тип из каталога), label
              (короткое название по-русски или пустая строка), note (пояснение для пользователя или пустая строка), settings —
              список полей {field, value}, value — всегда строка.
            - links — связи {from, port, to}: from и to — id блоков черновика, port — id выхода блока from из каталога.
              Каждый обязательный выход каждого блока должен быть куда-то подключён (обычно к следующему блоку или к блоку
              «Конец»). Первый блок (без входящих связей) Pepa подключит к началу сценария сама; «Начало» не добавляй.
            - Сценарий должен заканчиваться блоками «Конец» (end): для успеха и, где есть отдельные исходы (тайм-аут, «нет»),
              для каждого исхода — свой «Конец» с понятным сообщением.

            Каталог блоков (тип — название; выходы; поля):
            """);

        var categories = _registry.Categories.ToDictionary(c => c.Id, c => c.Title, StringComparer.Ordinal);
        foreach (var descriptor in _registry.Descriptors.Where(d => !d.IsStart)
                     .OrderBy(d => _registry.Categories.ToList().FindIndex(c => c.Id == d.Category)).ThenBy(d => d.Type, StringComparer.Ordinal))
        {
            text.AppendLine();
            text.Append("### ").Append(descriptor.Type).Append(" — ").Append(descriptor.Title);
            if (categories.TryGetValue(descriptor.Category, out var category))
            {
                text.Append(" (").Append(category).Append(')');
            }

            text.AppendLine();
            text.AppendLine(descriptor.Description);
            if (descriptor.ResourceHint is { Length: > 0 } hint)
            {
                text.Append("Ресурс: ").AppendLine(hint);
            }

            text.Append("Выходы: ");
            text.AppendLine(descriptor.Outputs.Count == 0
                ? "нет (процесс здесь заканчивается)"
                : string.Join("; ", descriptor.Outputs.Select(p => $"{p.Id} «{p.Label}»" + p.Kind switch
                {
                    PortKind.Error => " — ошибка, подключать необязательно",
                    PortKind.Optional => " — необязательный",
                    _ => " — обязательный",
                })));
            if (descriptor.Fields.Count > 0)
            {
                text.AppendLine("Поля:");
                foreach (var field in descriptor.Fields)
                {
                    text.Append("- ").Append(field.Name).Append(" — ").Append(field.Label).Append(" [").Append(KindName(field.Kind));
                    if (field.Required)
                    {
                        text.Append(", обязательное");
                    }

                    text.Append(']');
                    if (field.Options is { Count: > 0 } options)
                    {
                        text.Append(" значения: ").Append(string.Join(", ", options.Select(o => $"{(o.Value.Length == 0 ? "\"\"" : o.Value)} ({o.Label})")));
                    }

                    if (field.Default is { } value)
                    {
                        text.Append("; по умолчанию ").Append(value.ToJsonString());
                    }

                    if (field.VisibleWhen is { } when)
                    {
                        text.Append("; только если ").Append(when.Field).Append(" = ").Append(string.Join(" или ", when.Values));
                    }

                    if (field.Help is { Length: > 0 } help)
                    {
                        text.Append(". ").Append(help);
                    }

                    text.AppendLine();
                }
            }
        }

        if (_examples.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Примеры готовых сценариев Pepa в формате ответа:");
            foreach (var example in _examples)
            {
                text.AppendLine();
                text.Append("Описание: ").AppendLine(example.Description);
                text.AppendLine(JsonSerializer.Serialize(ToDraft(example), DraftSerializer.Options));
            }
        }

        return text.ToString();
    }

    /// <summary>Сценарий в формате ответа (для примеров): без «Начала» и его связей.</summary>
    internal static DraftJson ToDraft(ScenarioDefinition scenario)
    {
        var start = scenario.Nodes.Where(n => n.Type == "start").Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        return new DraftJson
        {
            Summary = scenario.Description,
            Variables = scenario.Variables.Select(v => new DraftVariable(v.Name, TypeName(v.Type), v.Description, v.IsInput,
                v.Default is JsonValue value && value.TryGetValue<string>(out var s) ? s : v.Default?.ToJsonString() ?? "")).ToList(),
            Blocks = scenario.Nodes.Where(n => !start.Contains(n.Id)).Select(n => new DraftBlock(n.Id, n.Type, n.Label ?? "", n.Note ?? "",
                n.Config.Select(p => new DraftSetting(p.Key, p.Value is JsonValue v && v.TryGetValue<string>(out var text) ? text : p.Value?.ToJsonString() ?? ""))
                    .ToList())).ToList(),
            Links = scenario.Edges.Where(e => !start.Contains(e.Source)).Select(e => new DraftLink(e.Source, e.SourcePort, e.Target)).ToList(),
        };
    }

    internal static string TypeName(VariableType type) => type.ToString().ToLowerInvariant();

    private static string KindName(FieldKind kind) => kind switch
    {
        FieldKind.Text => "текст",
        FieldKind.Template => "шаблон",
        FieldKind.MultilineTemplate => "многострочный шаблон",
        FieldKind.Path => "путь (шаблон, относительно рабочей папки)",
        FieldKind.Expression => "выражение",
        FieldKind.Condition => "условие",
        FieldKind.Number => "число",
        FieldKind.Boolean => "да/нет",
        FieldKind.Select => "выбор",
        FieldKind.Variable => "переменная",
        FieldKind.Time => "время ЧЧ:ММ",
        FieldKind.MatchCondition => "условие события",
        FieldKind.Scenario => "id другого сценария — оставь пустым и спроси",
        FieldKind.Seconds => "секунды",
        FieldKind.Code => "код",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Схема ответа (структурированный вывод): у всех объектов additionalProperties=false и все поля обязательны —
    /// так модель возвращает именно этот формат.
    /// </summary>
    private Dictionary<string, JsonElement> BuildSchema()
    {
        static JsonObject Obj(JsonObject properties) => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Key).ToArray()),
            ["properties"] = properties,
        };

        static JsonObject Str() => new() { ["type"] = "string" };
        static JsonObject Arr(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

        var root = Obj(new JsonObject
        {
            ["summary"] = Str(),
            ["questions"] = Arr(Str()),
            ["variables"] = Arr(Obj(new JsonObject
            {
                ["name"] = Str(),
                ["type"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Enum.GetValues<VariableType>().Select(t => (JsonNode)TypeName(t)).ToArray()),
                },
                ["description"] = Str(),
                ["isInput"] = new JsonObject { ["type"] = "boolean" },
                ["default"] = Str(),
            })),
            ["blocks"] = Arr(Obj(new JsonObject
            {
                ["id"] = Str(),
                ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(BlockTypes.Select(t => (JsonNode)t).ToArray()) },
                ["label"] = Str(),
                ["note"] = Str(),
                ["settings"] = Arr(Obj(new JsonObject { ["field"] = Str(), ["value"] = Str() })),
            })),
            ["links"] = Arr(Obj(new JsonObject { ["from"] = Str(), ["port"] = Str(), ["to"] = Str() })),
        });
        return root.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
    }

    private static Dictionary<string, JsonElement> BuildExplainSchema()
    {
        static JsonObject Str() => new() { ["type"] = "string" };
        var step = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("block", "text"),
            ["properties"] = new JsonObject { ["block"] = Str(), ["text"] = Str() },
        };
        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("summary", "steps", "cautions"),
            ["properties"] = new JsonObject
            {
                ["summary"] = Str(),
                ["steps"] = new JsonObject { ["type"] = "array", ["items"] = step },
                ["cautions"] = new JsonObject { ["type"] = "array", ["items"] = Str() },
            },
        };
        return root.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
    }
}

internal static class DraftSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
