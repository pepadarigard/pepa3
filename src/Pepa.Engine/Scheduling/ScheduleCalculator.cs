using System.Globalization;
using System.Text.Json.Nodes;
using Pepa.Core;

namespace Pepa.Engine.Scheduling;

/// <summary>
/// Расписания в понятном виде (без cron):
/// <c>{"mode":"interval","everyMinutes":30}</c>,
/// <c>{"mode":"daily","time":"09:30","days":[1,2,3,4,5]}</c> (1 — понедельник … 7 — воскресенье; пусто — каждый день),
/// <c>{"mode":"once","at":"2026-11-01T10:00"}</c> (местное время).
/// </summary>
public static class ScheduleCalculator
{
    public const int MaxIntervalMinutes = 60 * 24 * 366;

    /// <summary>null — расписание корректно, иначе понятное описание ошибки.</summary>
    public static string? Validate(JsonObject config)
    {
        switch (config.GetString("mode"))
        {
            case "interval":
                var minutes = config.GetNumber("everyMinutes");
                return minutes is null or < 1 or > MaxIntervalMinutes
                    ? $"Интервал — от 1 минуты до {MaxIntervalMinutes} минут."
                    : null;
            case "daily":
                if (!TryParseTime(config.GetString("time"), out _))
                {
                    return "Время укажите как ЧЧ:ММ, например 09:30.";
                }

                return Days(config).Any(d => d is < 1 or > 7) ? "Дни недели — числа от 1 (понедельник) до 7 (воскресенье)." : null;
            case "once":
                return TryParseLocal(config.GetString("at"), out _) ? null : "Дату и время укажите как ГГГГ-ММ-ДДTЧЧ:ММ.";
            default:
                return "Неизвестный вид расписания: выберите «каждые N минут», «ежедневно» или «один раз».";
        }
    }

    /// <summary>Следующий запуск строго после <paramref name="after"/>; null — запусков больше не будет.</summary>
    public static DateTimeOffset? Next(JsonObject config, DateTimeOffset after, TimeZoneInfo zone)
    {
        switch (config.GetString("mode"))
        {
            case "interval":
            {
                var minutes = config.GetNumber("everyMinutes") ?? 0;
                if (minutes < 1)
                {
                    return null;
                }

                // Привязка к началу местных суток, чтобы «каждые 30 минут» было в :00 и :30, а не «от момента сохранения».
                var period = TimeSpan.FromMinutes(minutes);
                var localDate = TimeZoneInfo.ConvertTime(after, zone).Date;
                var anchor = new DateTimeOffset(localDate, zone.GetUtcOffset(localDate));
                var passed = (after - anchor).Ticks / period.Ticks;
                var next = anchor + TimeSpan.FromTicks((passed + 1) * period.Ticks);
                return next > after ? next : next + period;
            }

            case "daily":
            {
                if (!TryParseTime(config.GetString("time"), out var time))
                {
                    return null;
                }

                var days = Days(config);
                var localAfter = TimeZoneInfo.ConvertTime(after, zone);
                for (var offset = 0; offset <= 8; offset++)
                {
                    var date = localAfter.Date.AddDays(offset);
                    var isoDay = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
                    if (days.Count > 0 && !days.Contains(isoDay))
                    {
                        continue;
                    }

                    var candidate = date + time.ToTimeSpan();
                    while (zone.IsInvalidTime(candidate))
                    {
                        candidate = candidate.AddHours(1);
                    }

                    var moment = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
                    if (moment > after)
                    {
                        return moment;
                    }
                }

                return null;
            }

            case "once":
            {
                if (!TryParseLocal(config.GetString("at"), out var local))
                {
                    return null;
                }

                var moment = new DateTimeOffset(local, zone.GetUtcOffset(local));
                return moment > after ? moment : null;
            }

            default:
                return null;
        }
    }

    /// <summary>Сколько запусков пришлось на промежуток (from, to] — для учёта пропущенных (не больше <paramref name="cap"/>).</summary>
    public static int CountBetween(JsonObject config, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, int cap = 1000)
    {
        var count = 0;
        var cursor = from;
        while (count < cap && Next(config, cursor, zone) is { } next && next <= to)
        {
            count++;
            cursor = next;
        }

        return count;
    }

    public static string Describe(JsonObject config)
    {
        switch (config.GetString("mode"))
        {
            case "interval":
                var minutes = (int)(config.GetNumber("everyMinutes") ?? 0);
                return minutes % 60 == 0 ? $"каждые {minutes / 60} ч" : $"каждые {minutes} мин";
            case "daily":
                var days = Days(config);
                var names = new[] { "пн", "вт", "ср", "чт", "пт", "сб", "вс" };
                var when = days.Count is 0 or 7 ? "ежедневно" : string.Join(", ", days.Order().Select(d => names[d - 1]));
                return $"{when} в {config.GetString("time")}";
            case "once":
                return TryParseLocal(config.GetString("at"), out var local)
                    ? $"один раз {local:dd.MM.yyyy HH:mm}"
                    : "один раз";
            default:
                return "расписание";
        }
    }

    private static List<int> Days(JsonObject config) =>
        config["days"] is JsonArray array
            ? array.Select(n => (int)(n.AsNumber() ?? 0)).Distinct().ToList()
            : [];

    private static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static bool TryParseLocal(string? text, out DateTime local) =>
        DateTime.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out local);
}
