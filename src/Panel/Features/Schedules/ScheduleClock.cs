namespace Panel.Features.Schedules;

// Phase 7b: a schedule runs on chosen weekdays at one time of day in the unit's time zone. Next() gives the first such moment after
// afterUtc, in UTC. On the day clocks go forward, a time that doesn't exist moves forward by the gap (02:30 → 03:30); on the day they go
// back, a time that happens twice runs once, at the first one (plan Review Focus 1).
public static class ScheduleClock
{
    public const int AllDays = 127;

    public static DateTime Next(int days, int minuteOfDay, TimeZoneInfo tz, DateTime afterUtc)
    {
        if ((days & AllDays) == 0) throw new ArgumentException("Pick at least one day.", nameof(days));
        if (minuteOfDay is < 0 or > 1439) throw new ArgumentOutOfRangeException(nameof(minuteOfDay));
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), tz).Date;
        for (var d = 0; d <= 8; d++)
        {
            var date = today.AddDays(d);
            if ((days & (1 << (int)date.DayOfWeek)) == 0) continue;
            var utc = ToUtc(DateTime.SpecifyKind(date.AddMinutes(minuteOfDay), DateTimeKind.Unspecified), tz);
            if (utc > afterUtc) return utc;
        }
        throw new InvalidOperationException("No run found in the next 8 days."); // unreachable with at least one day
    }

    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        if (tz.IsInvalidTime(local))
        {
            var gap = tz.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= local.Date && local.Date <= r.DateEnd)?.DaylightDelta ?? TimeSpan.FromHours(1);
            local = local.Add(gap);
        }
        if (tz.IsAmbiguousTime(local))
            return new DateTimeOffset(local, tz.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime; // the first occurrence has the larger offset
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    public static string LocalHourMinute(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).ToString("HH:mm");
}
