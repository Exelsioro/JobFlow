using System.Globalization;
using JobFlow.Domain;
namespace JobFlow.Infrastructure;

public static class TaskSchedule
{
    public static void Validate(ScheduledTask task)
    {
        if (task.ScheduleKind is not ("interval" or "daily" or "weekly")) throw new ArgumentException("Select interval, daily or weekly schedule.");
        if (task.IntervalMinutes is < 1 or > 525600) throw new ArgumentException("Interval must be 1–525600 minutes.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZone);
        if (!TimeOnly.TryParseExact(task.LocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("Start time must use HH:mm.");
        if (task.ScheduleKind == "weekly" && !Days(task).Any()) throw new ArgumentException("Select weekdays using 0–6 (Sunday–Saturday).");
    }
    private static HashSet<int> Days(ScheduledTask task)
    {
        var values = task.DaysOfWeek.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Any(x => !int.TryParse(x, out var n) || n is < 0 or > 6)) throw new ArgumentException("Weekdays must be 0–6.");
        return values.Select(int.Parse).ToHashSet();
    }
    public static DateTime Next(ScheduledTask task, DateTime afterUtc)
    {
        Validate(task);
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        if (task.ScheduleKind == "interval")
        {
            // Keep the original cadence; skip missed slots rather than creating a catch-up storm.
            var anchor = DateTime.SpecifyKind(task.NextRunAt, DateTimeKind.Utc);
            if (anchor > afterUtc) return anchor;
            var steps = Math.Floor((afterUtc - anchor).TotalMinutes / task.IntervalMinutes) + 1;
            return anchor.AddMinutes(steps * task.IntervalMinutes);
        }
        var zone = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZone);
        var date = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone).Date;
        var clock = TimeOnly.ParseExact(task.LocalTime, "HH:mm", CultureInfo.InvariantCulture);
        var days = Days(task);
        for (var i = 0; i < 15; i++)
        {
            var local = DateTime.SpecifyKind(date.AddDays(i).Add(clock.ToTimeSpan()), DateTimeKind.Unspecified);
            if (task.ScheduleKind == "weekly" && !days.Contains((int)local.DayOfWeek)) continue;
            // On a spring-forward gap run at the first valid local minute.
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            var utc = zone.IsAmbiguousTime(local)
                ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime
                : TimeZoneInfo.ConvertTimeToUtc(local, zone);
            if (utc > afterUtc) return utc;
        }
        throw new InvalidOperationException("No next schedule occurrence found.");
    }
}
