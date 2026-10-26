using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Telegram;

public static class TelegramNodes
{
    public static readonly NodeCategory Category = new("telegram", "Telegram", 30);

    public static NodeRegistry RegisterTelegramNodes(this NodeRegistry registry, ITelegramGateway telegram) => registry
        .RegisterCategory(Category)
        .Register(new SendTelegramNode(telegram))
        .Register(new WaitTelegramNode(telegram));

    internal static readonly FieldDescriptor ChatField = new()
    {
        Name = "chat", Label = "Чат", Kind = FieldKind.Template, Required = true, Placeholder = "@username или +79001234567",
        Help = "Имя пользователя, бота или канала (@name), номер телефона из ваших контактов или «me» — Избранное. " +
               "Можно подставить из переменной.",
    };

    internal static string Preview(string text) => text.Length > 60 ? text[..60] + "…" : text;
}

// ───────────── Отправить сообщение ─────────────

public sealed class SendTelegramNode(ITelegramGateway telegram) : INodeExecutor
{
    public const string TypeName = "telegram.send";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TelegramNodes.Category.Id,
        Title = "Отправить сообщение в Telegram",
        Description = "Отправляет сообщение от вашего аккаунта Telegram. Если итог неизвестен (обрыв связи), Pepa проверит " +
                      "историю чата, а не отправит сообщение второй раз.",
        Icon = "send",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "chat",
        Outputs = [new PortDescriptor(Ports.Out, "Отправлено"), new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error)],
        Fields =
        [
            TelegramNodes.ChatField,
            new FieldDescriptor
            {
                Name = "text", Label = "Текст", Kind = FieldKind.MultilineTemplate, Required = true,
                Placeholder = "Заказ {{ vars.order }} готов",
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить отправленное в", Kind = FieldKind.Variable,
                Help = "Объект: номер сообщения, чат (ключ для «Ждать сообщение») и время.",
            },
        ],
    };

    public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<JsonObject?>(new JsonObject { ["before"] = context.Time.GetUtcNow().ToString("O") });

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var text = context.GetString("text") ?? "";
        if (text.Trim().Length == 0)
        {
            throw new NodeInputException("Текст сообщения пустой.");
        }

        TelegramPeer chat;
        try
        {
            chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
        }
        catch (TelegramUnavailableException ex)
        {
            return NodeResult.Fail(ex.Message);
        }
        catch (TelegramChatNotFoundException ex)
        {
            return NodeResult.Fail(ex.Message, retryable: false);
        }

        // Ошибка при самой отправке (обрыв связи, тайм-аут) — итог неизвестен: движок проверит историю чата.
        var sent = await telegram.SendAsync(chat, text, silent: false, cancellationToken);
        var output = new JsonObject
        {
            ["messageId"] = sent.MessageId,
            ["chat"] = TelegramEvents.PeerJson(chat),
            ["date"] = sent.Date.ToString("O"),
        };
        var updates = new Dictionary<string, JsonNode?>();
        if (context.GetString("saveTo") is { Length: > 0 } saveTo)
        {
            updates[saveTo] = output.DeepClone();
        }

        return NodeResult.Ok(Ports.Out, updates, output, $"Отправлено в Telegram «{chat.Title}»: {TelegramNodes.Preview(text)}");
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var text = context.GetString("text") ?? "";
        var before = DateTimeOffset.TryParse(context.Checkpoint?.GetString("before"), out var parsed) ? parsed : DateTimeOffset.MinValue;
        try
        {
            var chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            var recent = await telegram.RecentAsync(chat, 30, cancellationToken);
            var found = recent.FirstOrDefault(m => m.Outgoing && m.Text == text && m.Date >= before - TimeSpan.FromSeconds(5));
            return found is not null
                ? VerificationResult.Done($"сообщение есть в истории чата (№{found.MessageId})",
                    new JsonObject { ["messageId"] = found.MessageId, ["chat"] = TelegramEvents.PeerJson(chat) })
                : VerificationResult.NotDone("в истории чата такого сообщения нет — его можно отправить");
        }
        catch (Exception ex) when (ex is TelegramUnavailableException or TelegramChatNotFoundException)
        {
            return VerificationResult.Inconclusive($"проверить историю чата не удалось: {ex.Message}");
        }
    }
}

// ───────────── Ждать сообщение ─────────────

public sealed class WaitTelegramNode(ITelegramGateway telegram) : INodeExecutor
{
    public const string TypeName = "telegram.wait";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TelegramNodes.Category.Id,
        Title = "Ждать сообщение в Telegram",
        Description = "Ждёт входящее сообщение из чата (например, ответ после «Отправить сообщение»). Ожидание переживает " +
                      "закрытие Pepa: сообщения, пришедшие за это время, будут получены после запуска.",
        Icon = "message-circle",
        Effect = NodeEffect.ReadOnly,
        SummaryField = "chat",
        Outputs =
        [
            new PortDescriptor(Ports.Event, "Пришло"),
            new PortDescriptor(Ports.Timeout, "Тайм-аут", PortKind.Optional),
            new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error),
        ],
        Fields =
        [
            TelegramNodes.ChatField with
            {
                Required = false,
                Placeholder = "@username — или пусто: из любого чата",
            },
            new FieldDescriptor
            {
                Name = "contains", Label = "Текст содержит", Kind = FieldKind.Template, Placeholder = "код",
                Help = "Необязательно. Без учёта регистра.",
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
                Help = "Ответ мог прийти, пока процесс выполнял предыдущий шаг. Берутся только сообщения, которые ещё никто не забрал.",
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить сообщение в", Kind = FieldKind.Variable },
        ],
    };

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        string? key = null;
        var configured = context.Node.Config.GetString("chat");
        var chatText = context.GetString("chat");
        if (!string.IsNullOrWhiteSpace(configured) && string.IsNullOrWhiteSpace(chatText))
        {
            throw new NodeInputException($"Чат «{configured}» получился пустым — иначе процесс ждал бы сообщения из любого чата. Проверьте переменные.");
        }

        if (!string.IsNullOrWhiteSpace(chatText))
        {
            try
            {
                key = (await telegram.ResolveAsync(chatText, cancellationToken)).Key;
            }
            catch (TelegramUnavailableException ex)
            {
                return NodeResult.Fail(ex.Message);
            }
            catch (TelegramChatNotFoundException ex)
            {
                return NodeResult.Fail(ex.Message, retryable: false);
            }
        }

        string? match = null;
        if (context.GetString("contains") is { Length: > 0 } contains)
        {
            match = $"String(event.payload.text ?? \"\").toLowerCase().includes({JsonSerializer.Serialize(contains.ToLowerInvariant())})";
        }

        var timeout = context.GetNumber("timeoutSeconds") is { } seconds and > 0
            ? context.Time.GetUtcNow() + TimeSpan.FromSeconds(seconds)
            : (DateTimeOffset?)null;
        return new NodeResult.WaitForEvent(new EventSubscriptionSpec
        {
            Source = TelegramEvents.Source,
            EventType = TelegramEvents.MessageType,
            CorrelationKey = key,
            MatchExpression = match,
            LookbackSeconds = (int)(context.GetNumber("lookbackSeconds") ?? 30),
            SaveTo = context.GetString("saveTo") is { Length: > 0 } saveTo ? saveTo : null,
        }, timeout);
    }
}
