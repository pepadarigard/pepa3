using System.Globalization;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Engine.Nodes;

/// <summary>
/// Спросить пользователя и дождаться ответа: ввести код с картинки, подтвердить оплату, выбрать вариант. Вопрос виден
/// на «Главной» и на странице процесса (и приходит уведомлением), ответ — событие, которое создаёт только интерфейс Pepa:
/// ожидание переживает закрытие приложения, а повторное нажатие не даст второго ответа.
/// </summary>
public sealed class AskUserNode : INodeExecutor
{
    public const string TypeName = "user.ask";
    public const string AnswerType = "answer";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Спросить пользователя",
        Description = "Задаёт вопрос и ждёт ответа: выбрать вариант, ввести текст или код с картинки. Вопрос появится на " +
                      "«Главной» и на странице процесса.",
        Icon = "message-circle-question",
        SummaryField = "question",
        Outputs = [new PortDescriptor(Ports.Event, "Ответ получен"), FlowNodes.TimeoutPort],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "question", Label = "Вопрос", Kind = FieldKind.MultilineTemplate, Required = true,
                Placeholder = "Подтвердить оплату заказа {{ vars.orderId }} на {{ vars.amount }} ₽?",
            },
            new FieldDescriptor
            {
                Name = "options", Label = "Варианты ответа (каждый с новой строки)", Kind = FieldKind.MultilineTemplate,
                Placeholder = "Да\nНет",
                Help = "Кнопки для ответа. Пусто — ответ своим текстом.",
            },
            new FieldDescriptor
            {
                Name = "allowText", Label = "Можно ответить своим текстом", Kind = FieldKind.Boolean, Default = false,
            },
            new FieldDescriptor
            {
                Name = "secret", Label = "Ответ — секрет (пароль, код)", Kind = FieldKind.Boolean, Default = false,
                Help = "Ответ не показывается в истории и на странице «События». Так же — если переменная для ответа секретная.",
            },
            new FieldDescriptor
            {
                Name = "image", Label = "Картинка к вопросу", Kind = FieldKind.Path, Placeholder = "снимки/капча-{{ process.id }}.png",
                Help = "Файл в рабочей папке (например, снимок из «Снимок страницы»): PNG, JPEG, GIF, WebP или BMP. Показывается " +
                       "файл таким, какой он сейчас, — у каждого процесса должно быть своё имя файла ({{ process.id }}).",
            },
            FlowNodes.TimeoutField("Если ответа не будет за это время — процесс пойдёт по выходу «Тайм-аут»."),
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить ответ в", Kind = FieldKind.Variable, Required = true },
        ],
    };

    public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node)
    {
        if (node.Config.GetString("options") is { } options && !options.Contains("{{", StringComparison.Ordinal) &&
            Options(options).Count == 0 && options.Trim().Length > 0)
        {
            yield return new NodeConfigIssue("Варианты ответа пустые.", "options");
        }
    }

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.GetString("question")))
        {
            throw new NodeInputException("Вопрос получился пустым — проверьте переменные в нём.");
        }

        // Варианты заданы, но получились пустыми (переменная не заполнена): без них подошёл бы любой ответ.
        var options = Options(context.GetString("options"));
        if (!string.IsNullOrWhiteSpace(context.Node.Config.GetString("options")) && options.Count == 0)
        {
            throw new NodeInputException("Варианты ответа получились пустыми — проверьте переменные в них.");
        }

        // Ответ должен поместиться в переменную: иначе на вопрос нельзя было бы ответить ни одним вариантом.
        var saveTo = context.RequireString("saveTo");
        var type = context.Graph?.Definition.Variables.FirstOrDefault(v => v.Name == saveTo)?.Type ?? VariableType.Any;
        if (type is VariableType.List or VariableType.Object)
        {
            throw new NodeInputException($"Ответ — текст, а переменная «{saveTo}» — {(type == VariableType.List ? "список" : "объект")}: " +
                                         "сделайте её текстом или «любой».");
        }

        foreach (var option in context.GetBoolean("allowText") ? [] : options)
        {
            if (!TryConvert(option, type, out _, out var problem))
            {
                throw new NodeInputException($"Вариант «{option}» не подходит для переменной «{saveTo}»: {problem}");
            }
        }

        var spec = new EventSubscriptionSpec
        {
            Source = EventSources.Question,
            EventType = AnswerType,
            // Свой ключ у каждого вопроса: ответ на него не достанется никому другому.
            CorrelationKey = $"{context.ProcessId}:{Guid.NewGuid():N}",
            SaveTo = saveTo,
            Purpose = SubscriptionPurpose.Question,
        };
        return ValueTask.FromResult<NodeResult>(new NodeResult.WaitForEvent(spec, FlowNodes.TimeoutAt(context)));
    }

    /// <summary>
    /// Ответ — в тип переменной: текст и «любой» — как есть; число — с запятой или точкой, с пробелами между разрядами, но
    /// без нулей в начале (код «048213» числом стал бы другим) и конечное; «да/нет» — true/false.
    /// </summary>
    public static bool TryConvert(string value, VariableType type, out JsonNode converted, out string problem)
    {
        converted = JsonValue.Create(value);
        problem = "";
        switch (type)
        {
            case VariableType.Any or VariableType.Text:
                return true;
            case VariableType.Number:
            {
                var normalized = value.Trim().Replace(" ", "", StringComparison.Ordinal).Replace(',', '.');
                var digits = normalized.TrimStart('-', '+');
                if (digits.Length > 1 && digits[0] == '0' && char.IsAsciiDigit(digits[1]))
                {
                    problem = "Ответ с нулём в начале — не число (это код?): сохраняйте его в переменную-текст.";
                    return false;
                }

                if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    problem = "Ответ должен быть числом.";
                    return false;
                }

                converted = JsonValue.Create(number);
                return true;
            }

            case VariableType.Boolean:
                switch (value.Trim().ToLowerInvariant())
                {
                    case "да" or "true" or "yes":
                        converted = JsonValue.Create(true);
                        return true;
                    case "нет" or "false" or "no":
                        converted = JsonValue.Create(false);
                        return true;
                    default:
                        problem = "Ответ должен быть «да» или «нет».";
                        return false;
                }

            default:
                problem = "ответ — текст, а переменная — список или объект: сделайте её текстом или «любой».";
                return false;
        }
    }

    /// <summary>Варианты ответа: непустые строки без пробелов по краям, без повторов.</summary>
    public static IReadOnlyList<string> Options(string? text) =>
        (text ?? "").Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}
