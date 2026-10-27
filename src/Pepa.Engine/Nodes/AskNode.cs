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
                Name = "image", Label = "Картинка к вопросу", Kind = FieldKind.Path, Placeholder = "снимки/капча.png",
                Help = "Файл в рабочей папке (например, снимок из «Снимок страницы»): PNG, JPEG, GIF, WebP или BMP.",
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

        var spec = new EventSubscriptionSpec
        {
            Source = EventSources.Question,
            EventType = AnswerType,
            // Свой ключ у каждого вопроса: ответ на него не достанется никому другому.
            CorrelationKey = $"{context.ProcessId}:{Guid.NewGuid():N}",
            SaveTo = context.RequireString("saveTo"),
            Purpose = SubscriptionPurpose.Question,
        };
        return ValueTask.FromResult<NodeResult>(new NodeResult.WaitForEvent(spec, FlowNodes.TimeoutAt(context)));
    }

    /// <summary>Варианты ответа: непустые строки без пробелов по краям, без повторов.</summary>
    public static IReadOnlyList<string> Options(string? text) =>
        (text ?? "").Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}
