using System.Globalization;
using System.Text.Json.Nodes;
using Pepa.Adapters.FileSystem;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Telegram;

// ───────────── Нажать кнопку ─────────────

/// <summary>
/// Нажимает кнопку бота: под сообщением (бот получает нажатие и отвечает) или на клавиатуре бота (отправляется её текст).
/// Кнопка ищется по тексту; если подходит несколько — Pepa не угадывает. Кнопка-ссылка не открывается: адрес сохраняется.
/// </summary>
public sealed class PressTelegramButtonNode(ITelegramGateway telegram) : INodeExecutor
{
    public const string TypeName = "telegram.button";

    /// <summary>Сколько последних сообщений чата смотреть в поисках кнопок, если номер сообщения не указан.</summary>
    private const int RecentScan = 50;

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TelegramNodes.Category.Id,
        Title = "Нажать кнопку в Telegram",
        Description = "Нажимает кнопку бота по её тексту: под сообщением или на клавиатуре бота. Если подходят несколько кнопок " +
                      "или кнопки нет — процесс остановится с объяснением. Нажатие кнопки под сообщением после сбоя проверить " +
                      "нельзя — решение за вами; кнопку клавиатуры Pepa проверит по истории чата.",
        Icon = "mouse-pointer-click",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "button",
        Outputs = [new PortDescriptor(Ports.Out, "Нажато"), new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error)],
        Fields =
        [
            TelegramNodes.ChatField with { Placeholder = "@shop_bot", Help = "Чат с ботом: @имя бота или ключ чата." },
            new FieldDescriptor
            {
                Name = "button", Label = "Текст кнопки", Kind = FieldKind.Template, Required = true, Placeholder = "Подтвердить",
            },
            new FieldDescriptor
            {
                Name = "match", Label = "Текст кнопки", Kind = FieldKind.Select, Default = "exact",
                Options = [new("exact", "совпадает целиком"), new("contains", "содержит")],
            },
            new FieldDescriptor
            {
                Name = "messageId", Label = "Номер сообщения с кнопками", Kind = FieldKind.Template,
                Placeholder = "{{ vars.reply.payload.messageId }}",
                Help = $"Необязательно. Пусто — последнее сообщение чата с кнопками (из {RecentScan} последних).",
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить итог в", Kind = FieldKind.Variable,
                Help = "Объект: кнопка, ответ бота (answer.message), адрес (url) для кнопки-ссылки.",
            },
        ],
    };

    /// <summary>
    /// До нажатия (только чтение): какое сообщение и какая кнопка, — чтобы нажать именно её и после сбоя знать, что
    /// проверять; плюс отметка истории и ключ от повторов для кнопок клавиатуры (они отправляют текст).
    /// </summary>
    public async ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var previous = context.PreviousCheckpoint;
        var checkpoint = new JsonObject
        {
            ["randomId"] = previous?.GetString("randomId") is { Length: > 0 } kept
                ? kept
                : Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture),
        };
        try
        {
            var chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            checkpoint["chat"] = chat.Key;
            checkpoint["afterId"] = previous?.GetString("chat") == chat.Key && previous.GetNumber("afterId") is { } earlier
                ? earlier
                : await telegram.LastMessageIdAsync(chat, cancellationToken);
            if (await FindAsync(context, chat, cancellationToken) is ({ } message, { } button))
            {
                checkpoint["messageId"] = message.MessageId;
                checkpoint["target"] = TelegramEvents.ButtonJson(button);
            }
        }
        catch (Exception ex) when (ex is TelegramUnavailableException or TelegramChatNotFoundException or ButtonException)
        {
            // Нажатие всё равно не состоится — блок сообщит причину сам.
        }

        return checkpoint;
    }

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        TelegramPeer chat;
        TelegramHistoryMessage message;
        TelegramButton button;
        try
        {
            chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            (message, button) = await FindAsync(context, chat, cancellationToken) switch
            {
                ({ } m, { } b) => (m, b),
                _ => throw new ButtonException("Сообщения с такой кнопкой нет.", retryable: true),
            };
            // Нажимаем ту кнопку, что нашли перед действием (если сообщение не поменялось).
            if (context.Checkpoint?["target"] is JsonObject target && context.Checkpoint.GetNumber("messageId") is { } preparedId &&
                (preparedId != message.MessageId || target.GetString("text") != button.Text))
            {
                return NodeResult.Fail("Пока Pepa готовилась нажать, кнопки бота изменились — ничего не нажато.");
            }
        }
        catch (ButtonException ex)
        {
            return NodeResult.Fail(ex.Message, ex.Retryable);
        }
        catch (TelegramUnavailableException ex)
        {
            return NodeResult.Fail(ex.Message);
        }
        catch (TelegramChatNotFoundException ex)
        {
            return NodeResult.Fail(ex.Message, retryable: false);
        }

        var output = new JsonObject
        {
            ["messageId"] = message.MessageId,
            ["chat"] = TelegramEvents.PeerJson(chat),
            ["button"] = TelegramEvents.ButtonJson(button),
        };
        try
        {
            switch (button.Kind)
            {
                case TelegramButtonKind.Url:
                    output["url"] = button.Url;
                    return new NodeResult.Success(Ports.Out, SaveTo(context, output), output,
                        $"Кнопка «{button.Text}» — ссылка: Pepa её не открывает сама, адрес сохранён ({button.Url}).", MessageLevel.Warning);

                case TelegramButtonKind.Text:
                {
                    var randomId = long.TryParse(context.Checkpoint?.GetString("randomId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored)
                        ? stored
                        : Random.Shared.NextInt64(1, long.MaxValue);
                    // Кнопка клавиатуры отправляет свой текст — как обычное сообщение (с защитой от повтора).
                    var sent = await telegram.SendAsync(chat, button.Text, randomId, cancellationToken);
                    output["sentMessageId"] = sent.MessageId;
                    return NodeResult.Ok(Ports.Out, SaveTo(context, output), output, $"Нажата кнопка клавиатуры «{button.Text}» в «{chat.Title}».");
                }

                case TelegramButtonKind.Callback:
                {
                    var answer = await telegram.PressAsync(chat, message.MessageId, button, cancellationToken);
                    output["answer"] = new JsonObject { ["message"] = answer.Message, ["url"] = answer.Url, ["alert"] = answer.Alert };
                    return NodeResult.Ok(Ports.Out, SaveTo(context, output), output,
                        $"Нажата кнопка «{button.Text}» в «{chat.Title}»" + (answer.Message is { Length: > 0 } text ? $": бот ответил «{text}»." : "."));
                }

                default:
                    return NodeResult.Fail($"Кнопку «{button.Text}» такого вида (оплата, игра, запрос данных…) Pepa не нажимает.", retryable: false);
            }
        }
        catch (TelegramUnavailableException ex)
        {
            return NodeResult.Fail(ex.Message);
        }
        catch (TelegramSendRejectedException ex)
        {
            return NodeResult.Fail(ex.Message, ex.Retryable);
        }
        catch (TelegramAlreadySentException ex)
        {
            return new NodeResult.Unknown(ex.Message);
        }
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Checkpoint?["target"] is not JsonObject target)
        {
            return VerificationResult.Inconclusive("перед нажатием кнопка не была найдена — проверить нечем, решите сами");
        }

        switch (target.GetString("kind"))
        {
            case "url":
                return VerificationResult.NotDone("кнопка-ссылка ничего не нажимает — шаг можно выполнить снова");
            case "callback":
                return VerificationResult.Inconclusive(
                    "нажатие кнопки под сообщением по истории чата не видно — посмотрите на ответ бота в чате и решите сами");
        }

        if (context.Checkpoint.GetNumber("afterId") is not { } afterId)
        {
            return VerificationResult.Inconclusive("перед нажатием не удалось отметить историю чата — проверить нечем");
        }

        try
        {
            var chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            var text = TelegramEvents.NormalizeText(target.GetString("text") ?? "");
            var newer = await telegram.MessagesAfterAsync(chat, (int)afterId, 1000, cancellationToken);
            var found = newer.Where(m => m.Outgoing && TelegramEvents.NormalizeText(m.Text) == text).MinBy(m => m.MessageId);
            return found is not null
                ? VerificationResult.Done($"текст кнопки есть в истории чата (№{found.MessageId})")
                : newer.Count >= 1000
                    ? VerificationResult.Inconclusive("после отметки в чате слишком много сообщений — решите сами")
                    : VerificationResult.NotDone("кнопка не нажата (её текста нет в истории чата) — можно нажать снова");
        }
        catch (Exception ex) when (ex is TelegramUnavailableException or TelegramChatNotFoundException)
        {
            return VerificationResult.Inconclusive($"проверить историю чата не удалось: {ex.Message}");
        }
    }

    /// <summary>Сообщение и кнопка по тексту; несколько подходящих кнопок или ни одной — <see cref="ButtonException"/>.</summary>
    private async Task<(TelegramHistoryMessage? Message, TelegramButton? Button)> FindAsync(NodeExecutionContext context, TelegramPeer chat,
        CancellationToken cancellationToken)
    {
        var wanted = context.RequireString("button").Trim();
        var contains = context.GetString("match") == "contains";
        bool Matches(TelegramButton b) => contains
            ? b.Text.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            : string.Equals(b.Text.Trim(), wanted, StringComparison.OrdinalIgnoreCase);

        TelegramHistoryMessage? message;
        if (context.GetString("messageId") is { } idText && idText.Trim().Length > 0)
        {
            if (!int.TryParse(idText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw new NodeInputException($"Номер сообщения «{idText}» — не число.");
            }

            message = await telegram.GetMessageAsync(chat, id, cancellationToken)
                      ?? throw new ButtonException($"Сообщения №{id} в чате «{chat.Title}» нет (удалено?) — нажимать нечего.", retryable: false);
        }
        else
        {
            var recent = await telegram.MessagesAfterAsync(chat, 0, RecentScan, cancellationToken);
            message = recent.Where(m => !m.Outgoing && m.Buttons.Count > 0).MaxBy(m => m.MessageId)
                      ?? throw new ButtonException($"В последних {RecentScan} сообщениях чата «{chat.Title}» нет кнопок бота.", retryable: true);
        }

        var candidates = message.Buttons.Where(Matches).ToList();
        if (candidates.Count == 0)
        {
            var available = message.Buttons.Count == 0 ? "кнопок нет" : $"есть: {string.Join(", ", message.Buttons.Select(b => $"«{b.Text}»"))}";
            throw new ButtonException($"В сообщении №{message.MessageId} нет кнопки «{wanted}» ({available}).", retryable: false);
        }

        if (candidates.Count > 1)
        {
            throw new ButtonException(
                $"Под описание «{wanted}» подходит несколько кнопок ({string.Join(", ", candidates.Select(b => $"«{b.Text}»"))}) — Pepa не угадывает. " +
                "Уточните текст кнопки или выберите «совпадает целиком».", retryable: false);
        }

        return (message, candidates[0]);
    }

    private static Dictionary<string, JsonNode?> SaveTo(NodeExecutionContext context, JsonObject output)
    {
        var updates = new Dictionary<string, JsonNode?>();
        if (context.GetString("saveTo") is { Length: > 0 } saveTo)
        {
            updates[saveTo] = output.DeepClone();
        }

        return updates;
    }

    private sealed class ButtonException(string message, bool retryable) : Exception(message)
    {
        public bool Retryable { get; } = retryable;
    }
}

// ───────────── Сохранить вложение ─────────────

/// <summary>Сохраняет файл или фото из сообщения Telegram в рабочую папку. Повтор безопасен: файл перезаписывается.</summary>
public sealed class DownloadTelegramFileNode(ITelegramGateway telegram, IWorkspace workspace) : INodeExecutor
{
    public const string TypeName = "telegram.download";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = TelegramNodes.Category.Id,
        Title = "Сохранить вложение из Telegram",
        Description = "Сохраняет файл или фото из сообщения (например, из ответа, полученного блоком «Ждать сообщение») в рабочую папку.",
        Icon = "download",
        Effect = NodeEffect.External,
        SupportsVerification = true,
        SummaryField = "path",
        Outputs = [new PortDescriptor(Ports.Out, "Сохранено"), new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error)],
        Fields =
        [
            TelegramNodes.ChatField with { Placeholder = "{{ vars.reply.payload.chat.key }}" },
            new FieldDescriptor
            {
                Name = "messageId", Label = "Номер сообщения", Kind = FieldKind.Template, Required = true,
                Placeholder = "{{ vars.reply.payload.messageId }}",
            },
            new FieldDescriptor
            {
                Name = "path", Label = "Куда сохранить", Kind = FieldKind.Path, Default = "telegram/",
                Help = "Путь в рабочей папке. Если заканчивается на «/» — это папка, имя файла берётся из сообщения. " +
                       "Файл с таким же именем заменяется.",
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить сведения о файле в", Kind = FieldKind.Variable },
        ],
    };

    public TimeSpan? ExpectedDuration(JsonObject input) => TimeSpan.FromMinutes(5);

    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var messageId = MessageId(context);
        TelegramPeer chat;
        TelegramHistoryMessage? message;
        try
        {
            chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            message = await telegram.GetMessageAsync(chat, messageId, cancellationToken);
        }
        catch (TelegramUnavailableException ex)
        {
            return NodeResult.Fail(ex.Message);
        }
        catch (TelegramChatNotFoundException ex)
        {
            return NodeResult.Fail(ex.Message, retryable: false);
        }

        if (message is null)
        {
            return NodeResult.Fail($"Сообщения №{messageId} в чате «{chat.Title}» нет (удалено?).", retryable: false);
        }

        if (message.Media is not { } media)
        {
            return NodeResult.Fail($"В сообщении №{messageId} нет файла или фото.", retryable: false);
        }

        var target = Target(context, messageId, media);
        long size;
        try
        {
            size = await telegram.DownloadAsync(chat, messageId, target, cancellationToken);
        }
        catch (TelegramUnavailableException ex)
        {
            // Файл пишется во временный и переименовывается целиком: недокачанного файла не остаётся, повтор безопасен.
            return NodeResult.Fail(ex.Message);
        }
        catch (TelegramNoMediaException ex)
        {
            return NodeResult.Fail(ex.Message, retryable: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NodeResult.Fail($"Не удалось записать файл: {ex.Message}", retryable: false);
        }

        var output = new JsonObject
        {
            ["path"] = workspace.ToRelative(target),
            ["size"] = size,
            ["fileName"] = Path.GetFileName(target),
            ["kind"] = media.Kind,
        };
        var updates = new Dictionary<string, JsonNode?>();
        if (context.GetString("saveTo") is { Length: > 0 } saveTo)
        {
            updates[saveTo] = output.DeepClone();
        }

        return NodeResult.Ok(Ports.Out, updates, output, $"Сохранено из Telegram: «{workspace.ToRelative(target)}» ({size:N0} байт).");
    }

    public async ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // Сохранение повторять безопасно: файл пишется во временный и заменяет прежний целиком. А то, что файл есть, —
        // не довод (с тем же именем мог лежать прежний), поэтому, пока вложение доступно, Pepa сохраняет его заново.
        try
        {
            var chat = await telegram.ResolveAsync(context.RequireString("chat"), cancellationToken);
            return await telegram.GetMessageAsync(chat, MessageId(context), cancellationToken) is { Media: not null }
                ? VerificationResult.NotDone("сохранение безопасно повторить — файл заменится целиком")
                : VerificationResult.Inconclusive("сообщения с вложением больше нет — посмотрите, сохранился ли файл, и решите сами");
        }
        catch (Exception ex) when (ex is TelegramUnavailableException or TelegramChatNotFoundException)
        {
            return VerificationResult.Inconclusive($"проверить не удалось: {ex.Message}");
        }
    }

    private static int MessageId(NodeExecutionContext context) =>
        int.TryParse(context.RequireString("messageId").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new NodeInputException($"Номер сообщения «{context.GetString("messageId")}» — не число.");

    private string Target(NodeExecutionContext context, int messageId, TelegramMedia media)
    {
        var path = (context.GetString("path") ?? "").Trim();
        if (path.Length == 0 || path.EndsWith('/') || path.EndsWith('\\'))
        {
            // Имя присылает собеседник — в нём могут быть папки, «..» и символы, недопустимые в Windows.
            path += Workspace.SafeFileName(media.FileName, $"telegram-{messageId}{Extension(media)}");
        }

        return workspace.Resolve(path);
    }

    private static string Extension(TelegramMedia media) => media.Kind switch
    {
        "photo" => ".jpg",
        "voice" => ".ogg",
        "video" => ".mp4",
        _ => media.MimeType switch
        {
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "text/plain" => ".txt",
            _ => "",
        },
    };
}
