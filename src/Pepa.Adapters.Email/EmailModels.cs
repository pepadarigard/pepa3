using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;
using Pepa.Core;

namespace Pepa.Adapters.Email;

/// <summary>Как защищено соединение с почтовым сервером.</summary>
public enum EmailSecurity
{
    /// <summary>По порту: 993 — шифрование сразу, иначе обязательно STARTTLS (без него соединения не будет).</summary>
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

    /// <summary>Сервер отправки (SMTP), например smtp.yandex.ru; пусто — Pepa письма не отправляет. Логин и пароль — те же.</summary>
    public string? SmtpServer { get; init; }

    public int SmtpPort { get; init; } = 465;

    /// <summary>Защита соединения с сервером отправки: «по порту» — 465 шифрование сразу, иначе STARTTLS.</summary>
    public EmailSecurity SmtpSecurity { get; init; } = EmailSecurity.Auto;

    /// <summary>
    /// Папка отправленных, где проверяется, ушло ли письмо, если ответ сервера потерялся; пусто — найти самой (отмеченная
    /// сервером как «Отправленные» или с таким названием).
    /// </summary>
    public string? SentFolder { get; init; }

    /// <summary>
    /// Какой это ящик и папка (без порта и способа защиты: смена только их — тот же ящик, и письма за время без связи
    /// не теряются). Смена ящика или папки — счёт с того, что в ней уже есть.
    /// </summary>
    internal string Key =>
        $"{Server.Trim().ToLowerInvariant()}:{Login.Trim().ToLowerInvariant()}:" +
        (Folder.Trim().Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "INBOX" : Folder.Trim());
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
public sealed class EmailUnavailableException(string message, Exception? inner = null, bool lasting = false) : Exception(message, inner)
{
    /// <summary>Само не пройдёт (пароль не принят, папки нет) — ждать письма бессмысленно, пока не исправят настройки.</summary>
    public bool Lasting { get; } = lasting;
}

/// <summary>Письмо не отправлено — и это точно: сервер отказал или до отправки дело не дошло.</summary>
public sealed class EmailNotSentException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Повтор может помочь (сервер был недоступен, временная ошибка); нет — адрес отклонён, письмо не принято.</summary>
    public bool Retryable { get; } = retryable;
}

/// <summary>Связь прервалась, когда письмо уже передавалось серверу: ушло ли оно — неизвестно.</summary>
public sealed class EmailSendUncertainException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Итог поиска отправленного письма в «Отправленных».</summary>
public enum SentCheck
{
    /// <summary>Письмо с этим Message-ID есть в «Отправленных» — оно ушло.</summary>
    Found,

    /// <summary>Не найдено. Это не доказывает, что письмо не ушло: не все серверы кладут отправленное в «Отправленные».</summary>
    NotFound,
}

/// <summary>События почты: новое письмо в отслеживаемой папке.</summary>
public static partial class EmailEvents
{
    public const string Source = "email";
    public const string MessageType = "message";

    /// <summary>Больше текста письма в событие не попадает (длинные рассылки).</summary>
    public const int MaxText = 100_000;

    /// <summary>Больше HTML не разбирается: текст письма всё равно ограничен.</summary>
    private const int MaxHtml = 2_000_000;

    private const int MaxLinks = 50;
    private const int MaxSubject = 1_000;
    private const int MaxName = 300;
    private const int MaxAddress = 320;
    private const int MaxListItems = 50;

    /// <param name="received">Когда письмо пришло (по серверу, уже приведённое к окну проверки).</param>
    /// <param name="mailbox">Ящик и папка (<see cref="EmailAccount.Key"/>): одно письмо ящика — одно событие.</param>
    public static EventInput ToEvent(MimeMessage message, uint uid, uint uidValidity, DateTimeOffset received, EmailAccount account,
        bool bodySkipped = false)
    {
        var html = message.HtmlBody;
        var parsed = html is { Length: > 0 } ? Html.Parse(html.Length > MaxHtml ? html[..MaxHtml] : html) : null;
        var text = message.TextBody is { Length: > 0 } plain ? plain : parsed?.Text ?? "";
        var from = message.From.Mailboxes.FirstOrDefault();
        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(m => m.Address)
            .Concat(message.Headers.Where(h => RecipientHeaders.Contains(h.Field)).Select(h => h.Value.Trim().Trim('<', '>')))
            .Where(a => a.Length > 0).Select(a => Cap(a.ToLowerInvariant(), MaxAddress)).Distinct().Take(MaxListItems).ToList();
        return new EventInput
        {
            Source = Source,
            Type = MessageType,
            // Письмо ящика — по его номеру: повтор после сбоя (тот же номер) отсекается, а разные письма с одинаковым
            // Message-ID (сканер, рассылка) — разные события.
            ExternalId = $"{account.Key}:{uidValidity}:{uid}",
            OccurredAt = received,
            Payload = new JsonObject
            {
                ["from"] = from?.Address is { } address ? Cap(address.ToLowerInvariant(), MaxAddress) : null,
                ["fromName"] = from?.Name is { Length: > 0 } name ? Cap(name, MaxName) : null,
                ["to"] = new JsonArray([.. recipients.Select(r => (JsonNode)r)]),
                ["subject"] = Cap(message.Subject ?? "", MaxSubject),
                ["text"] = Cap(text, MaxText),
                ["links"] = new JsonArray([.. Links(parsed?.Links ?? [], text).Select(l => (JsonNode)l)]),
                ["attachments"] = new JsonArray([.. message.Attachments.Take(MaxListItems)
                    .Select(a => (JsonNode)Cap(a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "вложение", 255))]),
                ["date"] = received.ToString("O"),
                ["messageId"] = message.MessageId is { } id ? Cap(id, MaxAddress) : null,
                ["folder"] = account.Folder,
                ["tooLarge"] = bodySkipped ? true : null,
            },
        };
    }

    private static readonly HashSet<string> RecipientHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Delivered-To", "X-Original-To", "Envelope-To", "X-Envelope-To",
    };

    private static string Cap(string value, int max) => value.Length > max ? value[..max] : value;

    /// <summary>Текст письма из HTML: без разметки, стилей и скриптов, с переводами строк вместо абзацев.</summary>
    public static string HtmlToText(string html) => Html.Parse(html).Text;

    /// <summary>
    /// Ссылки письма: адреса ссылок HTML (кнопка «Подтвердить» не видна в тексте), затем из текста — только http(s), без
    /// повторов.
    /// </summary>
    private static List<string> Links(IEnumerable<string> hrefs, string text) =>
        hrefs.Concat(UrlsInText().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', ';', '!', '?')))
            .Where(l => l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).Take(MaxLinks).ToList();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UrlsInText();

    /// <summary>
    /// Разбор HTML за один проход (без регулярных выражений: письмо от кого угодно не должно заставить Pepa считать часами).
    /// Скрипты, стили и заголовок страницы пропускаются; абзацы и строки таблиц — переводы строк, ячейки — пробелы; ссылки —
    /// только из &lt;a&gt; и &lt;area&gt;.
    /// </summary>
    private sealed class Html
    {
        private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "head", "title", "noscript" };

        private static readonly HashSet<string> Lines = new(StringComparer.OrdinalIgnoreCase)
        {
            "br", "p", "div", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6", "table", "ul", "ol", "blockquote", "hr", "section", "article",
        };

        private static readonly HashSet<string> Cells = new(StringComparer.OrdinalIgnoreCase) { "td", "th" };

        public required string Text { get; init; }
        public required List<string> Links { get; init; }

        public static Html Parse(string html)
        {
            var text = new StringBuilder();
            var links = new List<string>();
            var i = 0;
            while (i < html.Length)
            {
                var open = html.IndexOf('<', i);
                if (open < 0)
                {
                    text.Append(html, i, html.Length - i);
                    break;
                }

                text.Append(html, i, open - i);
                if (html.AsSpan(open).StartsWith("<!--"))
                {
                    var end = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
                    i = end < 0 ? html.Length : end + 3;
                    continue;
                }

                var close = html.IndexOf('>', open + 1);
                if (close < 0)
                {
                    break;
                }

                var tag = html.AsSpan(open + 1, close - open - 1);
                var closing = tag.Length > 0 && tag[0] == '/';
                var name = Name(closing ? tag[1..] : tag);
                i = close + 1;
                if (!closing && Hidden.Contains(name))
                {
                    // До закрывающего тега (или до конца — одним поиском, без возврата назад).
                    var end = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                    if (end < 0)
                    {
                        break;
                    }

                    var after = html.IndexOf('>', end);
                    i = after < 0 ? html.Length : after + 1;
                    continue;
                }

                if (Lines.Contains(name))
                {
                    text.Append('\n');
                }
                else if (Cells.Contains(name))
                {
                    text.Append(' ');
                }

                if (!closing && links.Count < MaxLinks && (name.Equals("a", StringComparison.OrdinalIgnoreCase) || name.Equals("area", StringComparison.OrdinalIgnoreCase)) &&
                    Href(tag) is { } href)
                {
                    links.Add(WebUtility.HtmlDecode(href).Trim());
                }
            }

            return new Html { Text = Tidy(WebUtility.HtmlDecode(text.ToString())), Links = links };
        }

        private static string Name(ReadOnlySpan<char> tag)
        {
            var length = 0;
            while (length < tag.Length && (char.IsAsciiLetterOrDigit(tag[length]) || tag[length] == '-'))
            {
                length++;
            }

            return tag[..length].ToString();
        }

        /// <summary>Значение href в кавычках, апострофах или без них.</summary>
        private static string? Href(ReadOnlySpan<char> tag)
        {
            var at = tag.IndexOf("href", StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                var j = at + 4;
                while (j < tag.Length && char.IsWhiteSpace(tag[j]))
                {
                    j++;
                }

                if (j < tag.Length && tag[j] == '=' && (at == 0 || char.IsWhiteSpace(tag[at - 1])))
                {
                    j++;
                    while (j < tag.Length && char.IsWhiteSpace(tag[j]))
                    {
                        j++;
                    }

                    if (j >= tag.Length)
                    {
                        return null;
                    }

                    if (tag[j] is '"' or '\'')
                    {
                        var quote = tag[j];
                        var end = tag[(j + 1)..].IndexOf(quote);
                        return end < 0 ? null : tag.Slice(j + 1, end).ToString();
                    }

                    var stop = j;
                    while (stop < tag.Length && !char.IsWhiteSpace(tag[stop]))
                    {
                        stop++;
                    }

                    return tag[j..stop].ToString();
                }

                var next = tag[(at + 4)..].IndexOf("href", StringComparison.OrdinalIgnoreCase);
                at = next < 0 ? -1 : at + 4 + next;
            }

            return null;
        }

        /// <summary>Пробелы внутри строки — по одному, строки без пробелов по краям, не больше одной пустой строки подряд.</summary>
        private static string Tidy(string text)
        {
            var result = new StringBuilder(text.Length);
            var blank = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = new StringBuilder(raw.Length);
                var space = false;
                foreach (var c in raw)
                {
                    if (c is ' ' or '\t' or '\r' or '\f' or '\v' or ' ')
                    {
                        space = line.Length > 0;
                        continue;
                    }

                    if (space)
                    {
                        line.Append(' ');
                        space = false;
                    }

                    line.Append(c);
                }

                if (line.Length == 0)
                {
                    blank++;
                    continue;
                }

                if (result.Length > 0)
                {
                    result.Append(blank > 0 ? "\n\n" : "\n");
                }

                result.Append(line);
                blank = 0;
            }

            return result.ToString();
        }
    }
}
