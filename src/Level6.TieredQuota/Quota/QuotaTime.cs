namespace Level6.TieredQuota.Quota;

/// <summary>
/// Calendar helpers, all UTC. Every caller passes an explicit `now` (from an injected TimeProvider)
/// — we never touch DateTimeOffset.UtcNow here, so time-based logic (window keys, resets, retry-after)
/// is fully deterministic under test.
/// </summary>
public static class QuotaTime
{
    public static string MinuteBucket(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMddHHmm");
    public static string DayBucket(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd");

    public static DateTimeOffset NextMinute(DateTimeOffset now)
    {
        var t = now.UtcDateTime;
        return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero).AddMinutes(1);
    }

    public static DateTimeOffset NextMidnight(DateTimeOffset now) =>
        new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);

    public static DateTimeOffset StartOfMonth(DateTimeOffset now) =>
        new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset StartOfNextMonth(DateTimeOffset now) =>
        StartOfMonth(now).AddMonths(1);
}
