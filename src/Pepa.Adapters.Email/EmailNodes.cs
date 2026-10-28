using System.Text.Json;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Email;

public static class EmailNodes
{
    public static readonly NodeCategory Category = new("email", "Почта", 22);

    /// <param name="problem">Почему ждать письмо бессмысленно (почта не подключена, пароль не принят); null — можно ждать.</param>
    public static NodeRegistry RegisterEmailNodes(this NodeRegistry registry, Func<string?> problem) => registry
        .RegisterCategory(Category)
        .Register(new WaitEmailNode(problem));
}

/// <summary>
/// «Ждать письмо»: письмо с кодом или ссылкой подтверждения после регистрации на сайте. Ожидание переживает закрытие
/// Pepa: письма, пришедшие за это время, будут получены после запуска.
/// </summary>
public sealed partial class WaitEmailNode(Func<string?> problem) : INodeExecutor
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
                Help = "Адрес (noreply@site.ru — ровно он), сайт (site.ru — адрес на нём или его поддомене) или слова из имени " +
                       "отправителя. Имя любой может написать какое угодно, адрес и сайт надёжнее. Пусто — от кого угодно.",
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
                Name = "lookbackSeconds", Label = "Учитывать пришедшие раньше, секунд", Kind = FieldKind.Seconds, Default = 30,
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
        if (problem() is { } reason)
        {
            return ValueTask.FromResult<NodeResult>(NodeResult.Fail(reason));
        }

        var conditions = new List<string>();
        if (Value(context, "from") is { } from)
        {
            conditions.Add(FromCondition(from.ToLowerInvariant()));
        }

        foreach (var (field, expression) in new[]
                 {
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
            LookbackSeconds = (int)(context.GetNumber("lookbackSeconds") ?? 30),
            SaveTo = context.GetString("saveTo") is { Length: > 0 } saveTo ? saveTo : null,
        }, timeout));
    }

    /// <summary>
    /// Условие «от кого». Адрес — ровно он; «@site.ru» или «site.ru» — адрес на этом сайте или его поддомене (не
    /// «evil-site.ru» и не «site.ru.evil.com»); слова — в имени или адресе. Имя отправителя пишет сам отправитель, поэтому
    /// адрес и сайт сравниваются только с адресом.
    /// </summary>
    private static string FromCondition(string from)
    {
        const string address = "String(event.payload.from ?? '').toLowerCase()";
        if (EmailAddress().IsMatch(from))
        {
            return $"{address} === {JsonSerializer.Serialize(from)}";
        }

        var domain = from.TrimStart('@');
        if (Domain().IsMatch(domain))
        {
            return $"({address}.endsWith({JsonSerializer.Serialize("@" + domain)}) || {address}.endsWith({JsonSerializer.Serialize("." + domain)}))";
        }

        return $"(String(event.payload.from ?? '') + ' ' + String(event.payload.fromName ?? '')).toLowerCase().includes({JsonSerializer.Serialize(from)})";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial System.Text.RegularExpressions.Regex EmailAddress();

    [System.Text.RegularExpressions.GeneratedRegex(@"^([a-z0-9а-яё-]+\.)+[a-z0-9а-яё-]{2,}$")]
    private static partial System.Text.RegularExpressions.Regex Domain();

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
