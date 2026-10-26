using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Adapters.FileSystem;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Telegram;

public static class TelegramNodes
{
    public static readonly NodeCategory Category = new("telegram", "Telegram", 30);

    public static NodeRegistry RegisterTelegramNodes(this NodeRegistry registry, ITelegramGateway telegram, IWorkspace workspace) => registry
        .RegisterCategory(Category)
        .Register(new SendTelegramNode(telegram, workspace))
        .Register(new WaitTelegramNode(telegram))
        .Register(new PressTelegramButtonNode(telegram))
        .Register(new DownloadTelegramFileNode(telegram, workspace));

    internal static readonly FieldDescriptor ChatField = new()
    {
        Name = "chat", Label = "Чат", Kind = FieldKind.Template, Required = true, Placeholder = "@username или +79001234567",
        Help = "Имя пользователя, бота или канала (@name), номер телефона из ваших контактов или «me» — Избранное. " +
               "Можно подставить из переменной.",
    };

    internal static string Preview(string text) => text.Length > 60 ? text[..60] + "…" : text;
}

// ───────────── Отправить сообщение ─────────────

public sealed class SendTelegramNode(ITelegramGateway telegram, IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "telegram.send";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TelegramNodes.Category.Id,
        Title = "Отправить сообщение в Telegram",
        Description = "Отправляет сообщение (и, если нужно, файл из рабочей папки) от вашего аккаунта Telegram. Если итог неизвестен " +
                      "(обрыв связи), Pepa проверит историю чата, а не отправит сообщение второй раз.",
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
                Name = "text", Label = "Текст", Kind = FieldKind.MultilineTemplate,
                Placeholder = "Заказ {{ vars.order }} готов",
                Help = "С файлом — подпись к нему (до 1024 символов).",
            },
            new FieldDescriptor
            {
                Name = "file", Label = "Файл из рабочей папки", Kind = FieldKind.Path, Placeholder = "отчёты/{{ vars.order }}.pdf",
                Help = "Необязательно. Отправляется как файл (без сжатия).",
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить отправленное в", Kind = FieldKind.Variable,
                Help = "Объект: номер сообщения, чат (ключ для «Ждать сообщение») и время.",
            },
        ],
    };

    /// <summary>
    /// До отправки (только чтение): номер последнего сообщения чата — после сбоя Pepa ищет своё сообщение среди более новых,
    /// не полагаясь на часы компьютера, — и ключ от повторов для Telegram.
    /// </summary>
    /// <remarks>
    /// При повторе шага ключ и отметка берутся из прошлой попытки: если её сообщение всё-таки дойдёт (библиотека
    /// переотправляет запросы после обрыва), Telegram не примет второе с тем же ключом, а проверка найдёт первое.
    /// </remarks>
    public async ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var previous = context.PreviousCheckpoint;
        // Строкой: в числе JSON (double) 64-битный ключ потерял бы точность.
        var randomId = previous?.GetString("randomId") is { Length: > 0 } kept
            ? kept
            : Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture);
        var checkpoint = new JsonObject { ["randomId"] = randomId };
        try
        {
            var chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            checkpoint["chat"] = chat.Key;
            checkpoint["afterId"] = previous?.GetString("chat") == chat.Key && previous.GetNumber("afterId") is { } earlier
                ? earlier
                : await telegram.LastMessageIdAsync(chat, cancellationToken);
        }
        catch (Exception ex) when (ex is TelegramUnavailableException or TelegramChatNotFoundException)
        {
            // Отправка всё равно не состоится — блок сообщит причину сам.
        }

        return checkpoint;
    }

    public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node)
    {
        if (string.IsNullOrWhiteSpace(node.Config.GetString("text")) && string.IsNullOrWhiteSpace(node.Config.GetString("file")))
        {
            yield return new NodeConfigIssue("Укажите текст сообщения или файл.", "text");
        }
    }

    /// <summary>Файл для отправки (полный путь) или null — только текст.</summary>
    private string? FileOf(NodeExecutionContext context)
    {
        if (context.GetString("file") is not { } file || file.Trim().Length == 0)
        {
            return null;
        }

        var full = workspace.Resolve(file.Trim());
        return File.Exists(full) ? full : throw new NodeInputException($"Файла «{workspace.ToRelative(full)}» нет в рабочей папке.");
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var text = context.GetString("text") ?? "";
        var file = FileOf(context);
        if (text.Trim().Length == 0 && file is null)
        {
            throw new NodeInputException("Текст сообщения пустой.");
        }

        if (file is not null && text.Length > 1024)
        {
            throw new NodeInputException($"Подпись к файлу длиннее 1024 символов ({text.Length}) — Telegram такую не примет.");
        }

        if (text.Length > 4096)
        {
            throw new NodeInputException($"Сообщение длиннее 4096 символов ({text.Length}) — Telegram такое не примет.");
        }

        var randomId = long.TryParse(context.Checkpoint?.GetString("randomId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored)
            ? stored
            : Random.Shared.NextInt64(1, long.MaxValue);

        TelegramPeer chat;
        TelegramSentMessage sent;
        try
        {
            chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            // Ошибка связи во время самой отправки — итог неизвестен: движок проверит историю чата, а не отправит снова.
            sent = file is null
                ? await telegram.SendAsync(chat, text, randomId, cancellationToken)
                : await telegram.SendFileAsync(chat, file, text.Length > 0 ? text : null, randomId, cancellationToken);
        }
        catch (TelegramUnavailableException ex)
        {
            return NodeResult.Fail(ex.Message);
        }
        catch (TelegramChatNotFoundException ex)
        {
            return NodeResult.Fail(ex.Message, retryable: false);
        }
        catch (TelegramSendRejectedException ex)
        {
            return NodeResult.Fail(ex.Message, ex.Retryable);
        }
        catch (TelegramAlreadySentException)
        {
            // Telegram узнал ключ прошлой попытки: сообщение уже отправлено, второго не будет. Это не «неизвестно» — найти его в
            // истории, чтобы заполнить итог; не нашлось (текст с тех пор изменился) — всё равно отправлено, номер неизвестен.
            if (await VerifyAsync(context, cancellationToken) is { Outcome: VerificationOutcome.Done } found)
            {
                return new NodeResult.Success(Ports.Out, found.VariableUpdates ?? new Dictionary<string, JsonNode?>(), found.Output,
                    $"Сообщение уже было отправлено прошлой попыткой ({found.Message}) — второй раз не отправлено.", MessageLevel.Warning);
            }

            return new NodeResult.Success(Ports.Out, new Dictionary<string, JsonNode?>(), null,
                "Сообщение уже было отправлено прошлой попыткой (Telegram узнал его) — второй раз не отправлено. В истории чата оно не нашлось " +
                "(возможно, текст с тех пор изменился), поэтому номер сообщения неизвестен.", MessageLevel.Warning);
        }

        var output = Output(chat, sent.MessageId, sent.Date);
        return NodeResult.Ok(Ports.Out, SaveTo(context, output), output,
            $"Отправлено в Telegram «{chat.Title}»: {(file is null ? "" : $"файл «{Path.GetFileName(file)}» ")}{TelegramNodes.Preview(text)}".TrimEnd());
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Checkpoint?.GetNumber("afterId") is not { } afterNumber)
        {
            return VerificationResult.Inconclusive("перед отправкой не удалось отметить историю чата — проверить нечем");
        }

        var text = TelegramEvents.NormalizeText(context.GetString("text") ?? "");
        var fileName = context.GetString("file") is { Length: > 0 } file ? Path.GetFileName(file.Trim()) : null;
        try
        {
            var chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            if (context.Checkpoint.GetString("chat") is { } key && key != chat.Key)
            {
                return VerificationResult.Inconclusive($"чат изменился с момента отправки ({key} → {chat.Key}) — решите сами");
            }

            var newer = await telegram.MessagesAfterAsync(chat, (int)afterNumber, VerifyScanLimit, cancellationToken);
            // Самое раннее своё сообщение с этим текстом после отметки — то, что отправил этот шаг.
            // С файлом — сообщение с этим файлом и подписью.
            var found = newer.Where(m => m.Outgoing && TelegramEvents.NormalizeText(m.Text) == text &&
                                         (fileName is null ? m.Media is null : m.Media?.FileName == fileName))
                .MinBy(m => m.MessageId);
            if (found is not null)
            {
                var output = Output(chat, found.MessageId, found.Date);
                return new VerificationResult(VerificationOutcome.Done, $"сообщение есть в истории чата (№{found.MessageId})",
                    VariableUpdates: SaveTo(context, output), Output: output);
            }

            return newer.Count >= VerifyScanLimit
                ? VerificationResult.Inconclusive($"после отметки в чате больше {VerifyScanLimit} сообщений — найти своё не удалось, решите сами")
                : VerificationResult.NotDone("в истории чата такого сообщения нет — его можно отправить");
        }
        catch (Exception ex) when (ex is TelegramUnavailableException or TelegramChatNotFoundException)
        {
            return VerificationResult.Inconclusive($"проверить историю чата не удалось: {ex.Message}");
        }
    }

    private const int VerifyScanLimit = 1000;

    private static JsonObject Output(TelegramPeer chat, int messageId, DateTimeOffset date) => new()
    {
        ["messageId"] = messageId,
        ["chat"] = TelegramEvents.PeerJson(chat),
        ["date"] = date.ToString("O"),
    };

    private static Dictionary<string, JsonNode?> SaveTo(NodeExecutionContext context, JsonObject output)
    {
        var updates = new Dictionary<string, JsonNode?>();
        if (context.GetString("saveTo") is { Length: > 0 } saveTo)
        {
            updates[saveTo] = output.DeepClone();
        }

        return updates;
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
                Help = "Имя пользователя, бота или канала (@name), номер телефона из ваших контактов или ключ чата " +
                       "(например, {{ vars.sent.chat.key }} из «Отправить сообщение»). Пусто — из любого чата.",
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
            if (chatText.Trim() is "me" or "self" or "Избранное" or "избранное")
            {
                throw new NodeInputException("В «Избранное» входящих сообщений не бывает — укажите чат, из которого ждёте ответ.");
            }

            TelegramPeer peer;
            try
            {
                peer = await telegram.ResolveAsync(chatText, cancellationToken);
            }
            catch (TelegramUnavailableException ex)
            {
                return NodeResult.Fail(ex.Message);
            }
            catch (TelegramChatNotFoundException ex)
            {
                return NodeResult.Fail(ex.Message, retryable: false);
            }

            key = peer.Key;
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
