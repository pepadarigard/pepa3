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
            if (previous?.GetString("chat") is { } earlierChat && earlierChat != chat.Key)
            {
                // Чат сменился: ключ прошлой попытки относится к другому чату — Telegram ответил бы «уже отправлено», хотя
                // в этот чат ничего не уходило.
                checkpoint["randomId"] = Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture);
            }

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
                Name = "contains", Label = "Текст", Kind = FieldKind.Template, Placeholder = "готово",
                Help = "Необязательно. Без учёта регистра и разницы «е» и «ё»; как сравнивать — ниже.",
            },
            new FieldDescriptor
            {
                Name = "match", Label = "Как сравнивать текст", Kind = FieldKind.Select, Default = "word",
                Options =
                [
                    new("exact", "Сообщение — ровно это"), new("word", "Есть слово или фраза целиком"),
                    new("contains", "Содержит (даже внутри слова)"),
                ],
                Help = "«Ровно это» — для ответов-команд («готово», «да»): «ещё не готово» не подойдёт (знаки и смайлы по краям не мешают). " +
                       "«Слово целиком» — «код» найдётся в «Ваш код: 1234», но не в «кодовое», а «готово» найдётся и в «не готово».",
            },
            new FieldDescriptor
            {
                Name = "replyTo", Label = "Ответ на сообщение", Kind = FieldKind.Template, Placeholder = "{{ vars.sent.messageId }}",
                Help = "Необязательно. Номер вашего сообщения (из «Отправить сообщение» → «Сохранить отправленное в»): подойдёт только " +
                       "ответ на него (в Telegram — «Ответить»). Так ответы одного человека на разные сообщения достаются разным процессам.",
            },
            new FieldDescriptor
            {
                Name = "from", Label = "От кого (в группе)", Kind = FieldKind.Template, Placeholder = "@manager",
                Help = "Необязательно. В группе — @имя пользователя или ключ (user:123), чьё сообщение ждёте. В личном чате не нужно.",
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

        var conditions = new List<string>();
        if (Value(context, "contains") is { } contains)
        {
            conditions.Add(TextCondition(contains, context.GetString("match") ?? "word"));
        }

        if (Value(context, "replyTo") is { } replyTo)
        {
            if (!long.TryParse(replyTo, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var replyId) ||
                replyId <= 0)
            {
                throw new NodeInputException($"«{replyTo}» — не номер сообщения (поле «Ответ на сообщение»).");
            }

            conditions.Add($"Number(event.payload.replyTo) === {replyId}");
        }

        if (Value(context, "from") is { } from)
        {
            var username = JsonSerializer.Serialize(from.TrimStart('@').ToLowerInvariant());
            conditions.Add($"(f => !!f && (String(f.username ?? '').toLowerCase() === {username} || f.key === {JsonSerializer.Serialize(from)}))(event.payload.from)");
        }

        var match = conditions.Count > 0 ? string.Join(" && ", conditions) : null;

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

    /// <summary>Буква, цифра или «_» — часть слова (как и в JavaScript-условии ниже).</summary>
    private const string IsWordJs = "c => c !== undefined && (c.toLowerCase() !== c.toUpperCase() || (c >= '0' && c <= '9') || c === '_')";

    private const string NormalizedTextJs = "String(event.payload.text ?? '').toLowerCase().replace(/ё/g, 'е')";

    /// <summary>
    /// Условие на текст сообщения. Сравнение без учёта регистра и «е»/«ё». «Ровно это» — весь текст без знаков и смайлов по
    /// краям; «слово целиком» — вхождение, по краям которого не буква и не цифра; «содержит» — любое вхождение.
    /// </summary>
    internal static string TextCondition(string text, string mode)
    {
        var expected = Normalize(text);
        if (expected.Length == 0)
        {
            throw new NodeInputException($"В тексте «{text}» нет ни букв, ни цифр — сравнивать не с чем.");
        }

        var value = JsonSerializer.Serialize(expected);
        return mode switch
        {
            "exact" => $"((t, w) => {{ const isWord = {IsWordJs}; let a = 0, b = t.length; while (a < b && !isWord(t[a])) a++; " +
                       $"while (b > a && !isWord(t[b - 1])) b--; return t.slice(a, b) === w; }})({NormalizedTextJs}, {value})",
            "contains" => $"{NormalizedTextJs}.includes({value})",
            "word" => $"((t, w) => {{ const isWord = {IsWordJs}; for (let i = t.indexOf(w); i >= 0; i = t.indexOf(w, i + 1)) " +
                      $"{{ if (!isWord(t[i - 1]) && !isWord(t[i + w.length])) return true; }} return false; }})({NormalizedTextJs}, {value})",
            _ => throw new NodeInputException($"«{mode}» — не вариант поля «Как сравнивать текст»."),
        };
    }

    /// <summary>Как сравниваются тексты: строчные, «ё» — «е», без знаков и смайлов по краям.</summary>
    private static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant().Replace('ё', 'е');
        static bool IsWord(char c) => char.ToLowerInvariant(c) != char.ToUpperInvariant(c) || c is >= '0' and <= '9' or '_';
        var start = 0;
        var end = lower.Length;
        while (start < end && !IsWord(lower[start]))
        {
            start++;
        }

        while (end > start && !IsWord(lower[end - 1]))
        {
            end--;
        }

        return lower[start..end];
    }

    /// <summary>
    /// Значение условия; null — условия нет. Заданное в схеме, но получившееся пустым (переменная не заполнена), — ошибка:
    /// иначе процесс ждал бы любое сообщение и забрал бы чужое.
    /// </summary>
    private static string? Value(NodeExecutionContext context, string field)
    {
        var configured = context.Node.Config.GetString(field);
        var value = context.GetString(field)?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) && string.IsNullOrEmpty(value))
        {
            throw new NodeInputException($"Условие «{field}» ({configured}) получилось пустым — иначе подошло бы любое сообщение. Проверьте переменные.");
        }

        return string.IsNullOrEmpty(value) ? null : value;
    }
}
