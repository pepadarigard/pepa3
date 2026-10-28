using System.Text.Json;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Email;

public static class EmailNodes
{
    public static readonly NodeCategory Category = new("email", "Почта", 22);

    /// <param name="ready">Почта настроена и включена (иначе ждать письма бессмысленно — они не придут).</param>
    public static NodeRegistry RegisterEmailNodes(this NodeRegistry registry, Func<bool> ready) => registry
        .RegisterCategory(Category)
        .Register(new WaitEmailNode(ready));
}

/// <summary>
/// «Ждать письмо»: письмо с кодом или ссылкой подтверждения после регистрации на сайте. Ожидание переживает закрытие
/// Pepa: письма, пришедшие за это время, будут получены после запуска.
/// </summary>
public sealed class WaitEmailNode(Func<bool> ready) : INodeExecutor
{
    public const string TypeName = "email.wait";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = EmailNodes.Category.Id,
        Title = "Ждать письмо",
        Description = "Ждёт новое письмо в почтовом ящике (например, с кодом подтверждения от сайта). Код из письма достаёт " +
                      "«Найти в тексте», ссылку подтверждения — список links письма. Письма, пришедшие, пока Pepa была закрыта, будут получены после запуска.",
        Icon = "mail",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "from",
        Outputs =
        [
            new PortDescriptor(Ports.Event, "Пришло"),
            new PortDescriptor(Ports.Timeout, "Тайм-аут", PortKind.Optional),
            new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error),
        ],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "from", Label = "От кого", Kind = FieldKind.Template, Placeholder = "noreply@site.ru",
                Help = "Адрес или имя отправителя содержит (без учёта регистра). Пусто — от кого угодно.",
            },
            new FieldDescriptor
            {
                Name = "subject", Label = "Тема содержит", Kind = FieldKind.Template, Placeholder = "код подтверждения",
                Help = "Необязательно. Без учёта регистра.",
            },
            new FieldDescriptor
            {
                Name = "contains", Label = "Текст содержит", Kind = FieldKind.Template,
                Help = "Необязательно. Без учёта регистра; ищется в тексте письма (у HTML-писем — в тексте без разметки).",
            },
            new FieldDescriptor
            {
                Name = "to", Label = "Кому (адрес)", Kind = FieldKind.Template, Placeholder = "{{ vars.email }}",
                Help = "Необязательно. Точный адрес получателя — например, отдельный адрес вида имя+сайт@почта для каждого аккаунта: " +
                       "так письмо одного аккаунта не достанется процессу другого.",
            },
            new FieldDescriptor
            {
                Name = "timeoutSeconds", Label = "Ждать не дольше, секунд", Kind = FieldKind.Seconds, Min = 0, Max = 31_536_000,
                Placeholder = "без ограничения",
            },
            new FieldDescriptor
            {
                Name = "lookbackSeconds", Label = "Учитывать пришедшие раньше, секунд", Kind = FieldKind.Seconds, Default = 120,
                Min = 0, Max = 86_400,
                Help = "Письмо могло прийти, пока процесс выполнял предыдущий шаг (например, сразу после нажатия «Зарегистрироваться»). " +
                       "Берутся только письма, которые ещё никто не забрал.",
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить письмо в", Kind = FieldKind.Variable,
                Help = "Письмо: {{ vars.mail.payload.subject }}, текст — payload.text, ссылки — payload.links, отправитель — payload.from.",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (!ready())
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail(
                "Почта не подключена: «Интеграции» → «Почта» — укажите ящик и пароль приложения.", retryable: false));
        }

        var conditions = new List<string>();
        foreach (var (field, expression) in new[]
                 {
                     ("from", "String(event.payload.from ?? '') + ' ' + String(event.payload.fromName ?? '')"),
                     ("subject", "String(event.payload.subject ?? '')"),
                     ("contains", "String(event.payload.text ?? '')"),
                 })
        {
            if (Value(context, field) is { } value)
            {
                conditions.Add($"({expression}).toLowerCase().includes({JsonSerializer.Serialize(value.ToLowerInvariant())})");
            }
        }

        if (Value(context, "to") is { } to)
        {
            conditions.Add($"(event.payload.to ?? []).some(a => String(a).toLowerCase() === {JsonSerializer.Serialize(to.ToLowerInvariant())})");
        }

        var timeout = context.GetNumber("timeoutSeconds") is { } seconds and > 0
            ? context.Time.GetUtcNow() + TimeSpan.FromSeconds(seconds)
            : (DateTimeOffset?)null;
        return ValueTask.FromResult<NodeResult>(new NodeResult.WaitForEvent(new EventSubscriptionSpec
        {
            Source = EmailEvents.Source,
            EventType = EmailEvents.MessageType,
            MatchExpression = conditions.Count > 0 ? string.Join(" && ", conditions) : null,
            LookbackSeconds = (int)(context.GetNumber("lookbackSeconds") ?? 120),
            SaveTo = context.GetString("saveTo") is { Length: > 0 } saveTo ? saveTo : null,
        }, timeout));
    }

    /// <summary>
    /// Значение условия; null — условия нет. Заданное в схеме, но получившееся пустым (переменная не заполнена), — ошибка:
    /// иначе процесс ждал бы любое письмо и забрал бы чужое.
    /// </summary>
    private static string? Value(NodeExecutionContext context, string field)
    {
        var configured = context.Node.Config.GetString(field);
        var value = context.GetString(field)?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) && string.IsNullOrEmpty(value))
        {
            throw new NodeInputException($"Условие «{field}» ({configured}) получилось пустым — иначе подошло бы любое письмо. Проверьте переменные.");
        }

        return string.IsNullOrEmpty(value) ? null : value;
    }
}
