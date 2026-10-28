namespace Pepa.Host.Tests;

/// <summary>Часы, которые показывают заданное время (для сроков хранения).</summary>
internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
