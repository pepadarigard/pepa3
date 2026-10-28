using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;
using Pepa.Core;

namespace Pepa.Adapters.Email;

/// <summary>Как защищено соединение с почтовым сервером.</summary>
public enum EmailSecurity
{
    /// <summary>По порту: 993 — шифрование сразу, иначе STARTTLS, если сервер умеет.</summary>
    Auto,

    /// <summary>Шифрование сразу (SSL/TLS, обычно порт 993).</summary>
    Ssl,

    /// <summary>Обычное соединение, затем STARTTLS (обычно порт 143).</summary>
    StartTls,

    /// <summary>Без шифрования — только для сервера на этом компьютере (пароль иначе ушёл бы открытым).</summary>
    None,
}

/// <summary>Почтовый ящик: сервер IMAP, вход и папка, за которой следит Pepa. Пароль хранится отдельно и наружу не отдаётся.</summary>
public sealed record EmailAccount
{
    public required string Server { get; init; }
    public int Port { get; init; } = 993;
    public EmailSecurity Security { get; init; } = EmailSecurity.Auto;
    public required string Login { get; init; }

    /// <summary>Папка с входящими («INBOX» — «Входящие»).</summary>
    public string Folder { get; init; } = "INBOX";

    public bool Enabled { get; init; } = true;

    /// <summary>Ящик и папка: при смене — новые письма считаются с того, что в ящике уже есть.</summary>
    internal string Key => $"{Server.Trim().ToLowerInvariant()}:{Port}:{Login.Trim().ToLowerInvariant()}:{Folder.Trim()}";
}

public enum EmailState
{
    NotConfigured,
    Off,
    Connecting,
    Connected,
    Error,
}

/// <summary>Состояние почты для «Интеграций».</summary>
public sealed record EmailStatus(
    EmailState State,
    EmailAccount? Account,
    DateTimeOffset? LastCheckAt,
    string? Error,
    int? Messages,
    string? Note);

/// <summary>Почта не настроена, сервер не отвечает или вход не удался.</summary>
public sealed class EmailUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>События почты: новое письмо в отслеживаемой папке.</summary>
public static partial class EmailEvents
{
    public const string Source = "email";
    public const string MessageType = "message";

    /// <summary>Больше текста письма в событие не попадает (длинные рассылки).</summary>
    public const int MaxText = 100_000;

    private const int MaxLinks = 50;

    /// <param name="received">Когда письмо пришло на сервер (по серверу, а не по часам отправителя).</param>
    public static EventInput ToEvent(MimeMessage message, uint uid, uint uidValidity, DateTimeOffset received, EmailAccount account,
        bool bodySkipped = false)
    {
        var html = message.HtmlBody;
        var text = message.TextBody is { Length: > 0 } plain ? plain : html is { Length: > 0 } ? HtmlToText(html) : "";
        var from = message.From.Mailboxes.FirstOrDefault();
        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(m => m.Address)
            .Concat(message.Headers.Where(h => h.Field is "Delivered-To" or "X-Original-To").Select(h => h.Value.Trim().Trim('<', '>')))
            .Where(a => a.Length > 0).Select(a => a.ToLowerInvariant()).Distinct().ToList();
        return new EventInput
        {
            Source = Source,
            Type = MessageType,
            // Один и тот же Message-ID — одно письмо (в том числе после переподключения или повторной проверки).
            ExternalId = message.MessageId is { Length: > 0 } id ? $"mid:{id}" : $"uid:{account.Key}:{uidValidity}:{uid}",
            OccurredAt = received,
            Payload = new JsonObject
            {
                ["from"] = from?.Address?.ToLowerInvariant(),
                ["fromName"] = from?.Name is { Length: > 0 } name ? name : null,
                ["to"] = new JsonArray([.. recipients.Select(r => (JsonNode)r)]),
                ["subject"] = message.Subject ?? "",
                ["text"] = text.Length > MaxText ? text[..MaxText] : text,
                ["links"] = new JsonArray([.. Links(html, text).Select(l => (JsonNode)l)]),
                ["attachments"] = new JsonArray([.. message.Attachments.Select(a => (JsonNode)(a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "вложение"))]),
                ["date"] = received.ToString("O"),
                ["messageId"] = message.MessageId,
                ["folder"] = account.Folder,
                ["tooLarge"] = bodySkipped ? true : null,
            },
        };
    }

    /// <summary>Текст письма из HTML: без разметки, стилей и скриптов, с переводами строк вместо абзацев.</summary>
    public static string HtmlToText(string html)
    {
        var text = HiddenBlocks().Replace(html, " ");
        text = LineBreaks().Replace(text, "\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return string.Join('\n', text.Split('\n').Select(l => l.Trim())).Trim();
    }

    /// <summary>
    /// Ссылки письма: из HTML (адрес кнопки «Подтвердить» не виден в тексте) и из текста — только http(s), без повторов.
    /// </summary>
    private static List<string> Links(string? html, string text)
    {
        var links = new List<string>();
        if (html is { Length: > 0 })
        {
            links.AddRange(Hrefs().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[2].Value).Trim()));
        }

        links.AddRange(UrlsInText().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', ';', '!', '?')));
        return links.Where(l => l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).Take(MaxLinks).ToList();
    }

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|tr|li|h[1-6]|table)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n(\s*\n)+")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"href\s*=\s*([""'])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hrefs();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlsInText();
}
