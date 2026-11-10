using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.ExperienceManager.Programming;

/// <summary>
/// When an event is on, in the viewer's wall-clock time.
/// </summary>
/// <param name="Kind">One of <see cref="ScheduleKinds"/>.</param>
/// <param name="Start">Once: the local start, <c>yyyy-MM-ddTHH:mm</c>. Yearly: the first day, <c>MM-dd</c>.</param>
/// <param name="End">Once: the local end, not included. Yearly: the last day, included; before Start, it runs across the new year.</param>
/// <param name="Days">Weekly: the days it starts on, as <c>Monday</c> to <c>Sunday</c>.</param>
/// <param name="From">Weekly: the start time, <c>HH:mm</c>.</param>
/// <param name="To">Weekly: the end time, not included; at or before From, it ends the next day.</param>
public sealed record EventSchedule(
    string Kind,
    string? Start = null,
    string? End = null,
    IReadOnlyList<string>? Days = null,
    string? From = null,
    string? To = null);

/// <summary>
/// The kinds of <see cref="EventSchedule"/>.
/// </summary>
public static class ScheduleKinds
{
    /// <summary>From one local date and time to another.</summary>
    public const string Once = "once";

    /// <summary>The same days every year.</summary>
    public const string Yearly = "yearly";

    /// <summary>The same hours on some days of every week.</summary>
    public const string Weekly = "weekly";
}

/// <summary>
/// Reads schedules. Every time here is wall-clock time with no zone: the caller has already turned the
/// moment into the viewer's local time.
/// </summary>
public static class Schedules
{
    // How far ahead boundaries are looked for: a yearly event's next start is never further.
    internal static readonly TimeSpan Horizon = TimeSpan.FromDays(400);

    /// <summary>
    /// Checks a schedule, returning why it cannot be read.
    /// </summary>
    /// <param name="schedule">The schedule.</param>
    /// <returns>The reason, or null if it is valid.</returns>
    public static string? Validate(EventSchedule? schedule)
    {
        if (schedule is null)
        {
            return "a schedule is required";
        }

        switch (schedule.Kind)
        {
            case ScheduleKinds.Once:
                return LocalTime(schedule.Start) is { } start && LocalTime(schedule.End) is { } end
                    ? (end > start ? null : "a once schedule ends after it starts")
                    : "a once schedule needs Start and End as yyyy-MM-ddTHH:mm";
            case ScheduleKinds.Yearly:
                return Day(schedule.Start) is not null && Day(schedule.End) is not null ? null : "a yearly schedule needs Start and End as MM-dd";
            case ScheduleKinds.Weekly:
                if (schedule.Days is not { Count: > 0 } || DaysOf(schedule) is not { Count: > 0 } days || days.Count != schedule.Days.Count)
                {
                    return "a weekly schedule needs Days, Monday to Sunday";
                }

                return Clock(schedule.From) is { } from && Clock(schedule.To) is { } to
                    ? (from != to ? null : "a weekly schedule's From and To differ")
                    : "a weekly schedule needs From and To as HH:mm";
            default:
                return $"unknown schedule kind '{schedule.Kind}'";
        }
    }

    /// <summary>
    /// Gets a value indicating whether the schedule is on at a local time.
    /// </summary>
    /// <param name="schedule">A valid schedule.</param>
    /// <param name="local">The viewer's local time.</param>
    /// <returns>Whether it is on.</returns>
    public static bool IsActive(EventSchedule schedule, DateTime local)
    {
        switch (schedule.Kind)
        {
            case ScheduleKinds.Once:
                return local >= LocalTime(schedule.Start)!.Value && local < LocalTime(schedule.End)!.Value;
            case ScheduleKinds.Yearly:
                var day = (local.Month, local.Day);
                var (first, last) = (Day(schedule.Start)!.Value, Day(schedule.End)!.Value);
                return first.CompareTo(last) <= 0
                    ? day.CompareTo(first) >= 0 && day.CompareTo(last) <= 0
                    : day.CompareTo(first) >= 0 || day.CompareTo(last) <= 0;
            case ScheduleKinds.Weekly:
                var days = DaysOf(schedule)!;
                var (from, to) = (Clock(schedule.From)!.Value, Clock(schedule.To)!.Value);
                var time = local.TimeOfDay;
                if (from < to)
                {
                    return days.Contains(local.DayOfWeek) && time >= from && time < to;
                }

                // It runs past midnight: on its start day from From, and on the next day until To.
                return (days.Contains(local.DayOfWeek) && time >= from) || (days.Contains(local.AddDays(-1).DayOfWeek) && time < to);
            default:
                return false;
        }
    }

    /// <summary>
    /// Gets every moment, after <paramref name="from"/> and up to <paramref name="until"/>, at which the schedule
    /// may turn on or off.
    /// </summary>
    /// <param name="schedule">A valid schedule.</param>
    /// <param name="from">The local time to look after.</param>
    /// <param name="until">The last local time to look at.</param>
    /// <returns>The moments, in order.</returns>
    public static IEnumerable<DateTime> Boundaries(EventSchedule schedule, DateTime from, DateTime until)
    {
        IEnumerable<DateTime> all = schedule.Kind switch
        {
            ScheduleKinds.Once => [LocalTime(schedule.Start)!.Value, LocalTime(schedule.End)!.Value],
            ScheduleKinds.Yearly => YearlyBoundaries(schedule, from.Year, until.Year),
            ScheduleKinds.Weekly => WeeklyBoundaries(schedule, from.Date.AddDays(-1), until.Date),
            _ => [],
        };
        return all.Where(t => t > from && t <= until).Distinct().Order();
    }

    /// <summary>
    /// Gets the next moment after <paramref name="local"/> at which the schedule may turn on or off.
    /// </summary>
    /// <param name="schedule">A valid schedule.</param>
    /// <param name="local">The viewer's local time.</param>
    /// <returns>The moment, or null if it never changes again.</returns>
    public static DateTime? NextBoundary(EventSchedule schedule, DateTime local) =>
        Boundaries(schedule, local, local + Horizon).Select(t => (DateTime?)t).FirstOrDefault();

    internal static DateTime? LocalTime(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    internal static (int Month, int Day)? Day(string? value) =>
        DateTime.TryParseExact(value + "-2000", "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? (t.Month, t.Day) : null;

    internal static TimeSpan? Clock(string? value) =>
        TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : null;

    internal static HashSet<DayOfWeek>? DaysOf(EventSchedule schedule) =>
        schedule.Days?.Select(d => Enum.TryParse<DayOfWeek>(d, true, out var day) && Enum.IsDefined(day) && !int.TryParse(d, out _) ? (DayOfWeek?)day : null)
            .OfType<DayOfWeek>()
            .ToHashSet();

    // A yearly event turns on at the start of its first day and off at the end of its last. February 29 falls on
    // March 1 in other years.
    private static IEnumerable<DateTime> YearlyBoundaries(EventSchedule schedule, int fromYear, int untilYear)
    {
        var (first, last) = (Day(schedule.Start)!.Value, Day(schedule.End)!.Value);
        for (var year = fromYear - 1; year <= untilYear + 1; year++)
        {
            yield return Date(year, first);
            yield return Date(year, last).AddDays(1);
        }
    }

    private static DateTime Date(int year, (int Month, int Day) day) =>
        day.Day > DateTime.DaysInMonth(year, day.Month) ? new DateTime(year, day.Month, 1).AddMonths(1) : new DateTime(year, day.Month, day.Day);

    private static IEnumerable<DateTime> WeeklyBoundaries(EventSchedule schedule, DateTime firstDay, DateTime lastDay)
    {
        var days = DaysOf(schedule)!;
        var (from, to) = (Clock(schedule.From)!.Value, Clock(schedule.To)!.Value);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (days.Contains(day.DayOfWeek))
            {
                yield return day + from;
                yield return from < to ? day + to : day.AddDays(1) + to;
            }
        }
    }
}
