using System.Text.Json.Nodes;
using MimeKit;
using MimeKit.Utils;
using Pepa.Adapters.FileSystem;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Email;

/// <summary>
/// «Отправить письмо» с ящика из «Интеграций» (SMTP). Отказ сервера — ошибка (письмо не ушло); обрыв во время передачи —
/// «итог неизвестен», и Pepa ищет письмо в «Отправленных» по его Message-ID, а не отправляет снова (D-074).
/// </summary>
public sealed class SendEmailNode(EmailService email, IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "email.send";

    /// <summary>Больше вложений вместе — нет: в письме они занимают на треть больше, а серверы принимают до 25 МБ.</summary>
    public const long MaxAttachmentBytes = 18 * 1024 * 1024;

    private const int MaxRecipients = 50;

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = EmailNodes.Category.Id,
        Title = "Отправить письмо",
        Description = "Отправляет письмо с вашего ящика (настройки — «Интеграции» → «Почта» → «Сервер отправки»). Если связь " +
                      "оборвалась во время отправки, Pepa не отправляет письмо второй раз, а ищет его в «Отправленных».",
        Icon = "mail",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "to",
        Outputs = [new PortDescriptor(Ports.Out, "Отправлено"), new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error)],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "to", Label = "Кому", Kind = FieldKind.Template, Required = true, Placeholder = "client@site.ru",
                Help = "Адрес почты; несколько — через запятую.",
            },
            new FieldDescriptor { Name = "cc", Label = "Копия", Kind = FieldKind.Template, Help = "Необязательно. Несколько — через запятую." },
            new FieldDescriptor
            {
                Name = "subject", Label = "Тема", Kind = FieldKind.Template, Required = true, Placeholder = "Отчёт за {{ vars.day }}",
            },
            new FieldDescriptor { Name = "text", Label = "Текст", Kind = FieldKind.MultilineTemplate },
            new FieldDescriptor
            {
                Name = "files", Label = "Вложения из рабочей папки", Kind = FieldKind.MultilineTemplate, Placeholder = "отчёты/{{ vars.day }}.xlsx",
                Help = $"Необязательно. По одному файлу в строке; вместе — до {MaxAttachmentBytes / 1024 / 1024} МБ.",
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить отправленное в", Kind = FieldKind.Variable,
                Help = "Объект: messageId (номер письма — по нему письмо находится в «Отправленных»), to и subject.",
            },
        ],
    };

    /// <summary>
    /// Номер письма (Message-ID) — до отправки: по нему письмо ищется в «Отправленных», если ответ сервера потерялся. При
    /// повторе шага — номер прошлой попытки: проверка найдёт письмо, какая бы из попыток его ни отправила.
    /// </summary>
    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var messageId = context.PreviousCheckpoint?.GetString("messageId") is { Length: > 0 } kept
            ? kept
            : MimeUtils.GenerateMessageId(email.SenderAddress?.Split('@')[^1] ?? "pepa.local");
        return ValueTask.FromResult<JsonObject?>(new JsonObject { ["messageId"] = messageId });
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var to = Addresses(context, "to");
        if (to.Count == 0)
        {
            throw new NodeInputException("Не указан адрес получателя (поле «Кому»).");
        }

        var cc = Addresses(context, "cc");
        var subject = context.GetString("subject")?.Trim() ?? "";
        if (subject.Length == 0)
        {
            throw new NodeInputException("Тема письма пустая — проверьте поле «Тема» и подстановки в нём.");
        }

        var files = Files(context);
        if (email.SendProblem is { } problem || email.SenderAddress is not { } sender)
        {
            return NodeResult.Fail(email.SendProblem ?? "Почта не подключена: «Интеграции» → «Почта».", retryable: false);
        }

        var messageId = context.Checkpoint?.GetString("messageId") ?? throw new NodeInputException("Нет номера письма (Message-ID).");
        var body = new BodyBuilder { TextBody = context.GetString("text") ?? "" };
        foreach (var file in files)
        {
            await body.Attachments.AddAsync(file, cancellationToken);
        }

        var message = new MimeMessage
        {
            Subject = subject,
            MessageId = messageId,
            Date = context.Time.GetUtcNow(),
            Body = body.ToMessageBody(),
        };
        message.From.Add(new MailboxAddress("", sender));
        message.To.AddRange(to);
        message.Cc.AddRange(cc);

        try
        {
            await email.SendAsync(message, cancellationToken);
        }
        catch (EmailNotSentException ex)
        {
            return NodeResult.Fail(ex.Message, ex.Retryable);
        }
        catch (EmailSendUncertainException ex)
        {
            return new NodeResult.Unknown(ex.Message);
        }

        var output = Output(messageId, to, subject);
        return NodeResult.Ok(Ports.Out, Updates(context, output), output,
            $"Письмо «{subject}» отправлено: {string.Join(", ", to.Select(a => a.Address))}.");
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Checkpoint?.GetString("messageId") is not { Length: > 0 } messageId)
        {
            return VerificationResult.Inconclusive("нет номера письма — найти его нечем");
        }

        SentCheck found;
        try
        {
            found = await email.FindSentAsync(messageId, cancellationToken);
        }
        catch (EmailUnavailableException ex)
        {
            return VerificationResult.Inconclusive($"не удалось посмотреть «Отправленные»: {ex.Message}");
        }

        if (found == SentCheck.NotFound)
        {
            // Не «не отправлено»: не все серверы кладут в «Отправленные» письма, отправленные программами.
            return VerificationResult.Inconclusive(
                "в «Отправленных» этого письма нет. Не все почтовые серверы кладут туда письма, отправленные программами, — " +
                "посмотрите сами (или спросите получателя), прежде чем отправлять снова");
        }

        var output = Output(messageId, SafeAddresses(context, "to"), context.GetString("subject")?.Trim() ?? "");
        return new VerificationResult(VerificationOutcome.Done, "письмо есть в «Отправленных» — оно ушло", Ports.Out,
            Updates(context, output), output);
    }

    private static JsonObject Output(string messageId, IReadOnlyList<MailboxAddress> to, string subject) => new()
    {
        ["messageId"] = messageId,
        ["to"] = new JsonArray([.. to.Select(a => (JsonNode)a.Address)]),
        ["subject"] = subject,
    };

    private static Dictionary<string, JsonNode?> Updates(NodeExecutionContext context, JsonObject output) =>
        context.GetString("saveTo") is { Length: > 0 } saveTo
            ? new Dictionary<string, JsonNode?> { [saveTo] = output.DeepClone() }
            : [];

    /// <summary>Адреса поля: через запятую, точку с запятой или с новой строки. Неверный адрес — ошибка до отправки.</summary>
    private static List<MailboxAddress> Addresses(NodeExecutionContext context, string field)
    {
        var result = new List<MailboxAddress>();
        foreach (var part in (context.GetString(field) ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailboxAddress.TryParse(part, out var mailbox) || !mailbox.Address.Contains('@') || mailbox.Address.StartsWith('@') ||
                mailbox.Address.EndsWith('@'))
            {
                throw new NodeInputException($"«{part}» — не адрес почты (поле «{(field == "to" ? "Кому" : "Копия")}»).");
            }

            if (!result.Any(a => a.Address.Equals(mailbox.Address, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(mailbox);
            }
        }

        return result.Count > MaxRecipients
            ? throw new NodeInputException($"Получателей больше {MaxRecipients} — так письма похожи на рассылку, и сервер их не пропустит.")
            : result;
    }

    private static IReadOnlyList<MailboxAddress> SafeAddresses(NodeExecutionContext context, string field)
    {
        try
        {
            return Addresses(context, field);
        }
        catch (NodeInputException)
        {
            return [];
        }
    }

    /// <summary>Вложения: файлы рабочей папки, по одному в строке. Нет файла или вместе слишком большие — ошибка до отправки.</summary>
    private List<string> Files(NodeExecutionContext context)
    {
        var files = new List<string>();
        long total = 0;
        foreach (var line in (context.GetString("files") ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var full = workspace.Resolve(line);
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                throw new NodeInputException($"Файла «{workspace.ToRelative(full)}» нет в рабочей папке — письмо не отправлено.");
            }

            total += info.Length;
            files.Add(full);
        }

        return total > MaxAttachmentBytes
            ? throw new NodeInputException(
                $"Вложения вместе больше {MaxAttachmentBytes / 1024 / 1024} МБ — почтовый сервер такое письмо не примет. Отправьте ссылку на файл.")
            : files;
    }
}
