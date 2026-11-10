using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.ExperienceManager.Programming;

/// <summary>
/// The events on for viewers at one UTC offset, until the next moment one may turn on or off.
/// </summary>
/// <param name="Events">The published events on now, highest first.</param>
/// <param name="Key">Names this set, for layout versions and cache keys; empty when no event is on.</param>
/// <param name="Until">When the set may next change, or null if it never does.</param>
public sealed record ActiveEvents(IReadOnlyList<ProgrammedEvent> Events, string Key, DateTimeOffset? Until);

/// <summary>
/// Which events are on, per viewer UTC offset. The set is worked out once per boundary, by a timer set for the next
/// boundary, so a request between boundaries only reads it.
/// </summary>
public sealed class EventTimeline : IDisposable
{
    /// <summary>The earliest UTC offset there is, in minutes.</summary>
    public const int MinOffset = -12 * 60;

    /// <summary>The latest UTC offset there is, in minutes.</summary>
    public const int MaxOffset = 14 * 60;

    private const int ServerZone = int.MinValue;

    // Timers take at most about 49 days; a boundary further off is reached by waking once a day.
    private static readonly TimeSpan LongestWait = TimeSpan.FromDays(1);

    private readonly EventStore _events;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, ActiveEvents> _sets = new();
    private readonly object _timerLock = new();
    private ITimer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventTimeline"/> class.
    /// </summary>
    /// <param name="events">The events.</param>
    public EventTimeline(EventStore events)
        : this(events, TimeProvider.System)
    {
    }

    internal EventTimeline(EventStore events, TimeProvider time)
    {
        _events = events;
        _time = time;
        _events.Changed += OnEventsChanged;
    }

    /// <summary>
    /// Gets the events on for viewers at a UTC offset.
    /// </summary>
    /// <param name="utcOffset">The viewer's offset in minutes east of UTC, or null for the server's time zone.</param>
    /// <returns>The events on now.</returns>
    public ActiveEvents For(int? utcOffset)
    {
        var key = utcOffset is >= MinOffset and <= MaxOffset ? utcOffset.Value : ServerZone;
        var now = _time.GetUtcNow();
        if (_sets.TryGetValue(key, out var set) && (set.Until is null || now < set.Until))
        {
            return set;
        }

        // The first viewer at an offset, or a boundary the timer has not reached yet.
        set = Compute(key, now);
        _sets[key] = set;
        Arm();
        return set;
    }

    /// <summary>
    /// Gets the local time for a UTC offset.
    /// </summary>
    /// <param name="utcOffset">The offset in minutes east of UTC, or null for the server's time zone.</param>
    /// <returns>The local wall-clock time.</returns>
    public DateTime LocalNow(int? utcOffset) => Local(utcOffset is >= MinOffset and <= MaxOffset ? utcOffset.Value : ServerZone, _time.GetUtcNow());

    /// <inheritdoc />
    public void Dispose()
    {
        _events.Changed -= OnEventsChanged;
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private DateTime Local(int key, DateTimeOffset utc) =>
        key == ServerZone ? TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).DateTime : utc.UtcDateTime.AddMinutes(key);

    private DateTimeOffset Utc(int key, DateTime local)
    {
        if (key != ServerZone)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(local.AddMinutes(-key), DateTimeKind.Utc));
        }

        // A wall-clock time skipped by a daylight-saving change happens when the clocks reach the other side of it.
        var zone = _time.LocalTimeZone;
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
    }

    private ActiveEvents Compute(int key, DateTimeOffset now)
    {
        var local = Local(key, now);
        var published = _events.All.Where(e => e.Published).ToList();
        var active = published.Where(e => Schedules.IsActive(e.Schedule, local)).ToList();
        var next = published.Select(e => Schedules.NextBoundary(e.Schedule, local)).OfType<DateTime>().Order().Select(t => (DateTime?)t).FirstOrDefault();
        return new ActiveEvents(active, KeyOf(active), next is { } t ? Utc(key, t) : null);
    }

    internal static string KeyOf(IReadOnlyList<ProgrammedEvent> active) =>
        active.Count == 0 ? string.Empty : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(active)))[..8];

    // The timer fires at the earliest boundary of any set in use and works out the sets that reached it.
    private void Arm()
    {
        lock (_timerLock)
        {
            var next = _sets.Values.Select(s => s.Until).OfType<DateTimeOffset>().Order().Select(t => (DateTimeOffset?)t).FirstOrDefault();
            _timer?.Dispose();
            _timer = next is { } at
                ? _time.CreateTimer(_ => OnBoundary(), null, Min(Max(at - _time.GetUtcNow(), TimeSpan.Zero), LongestWait), Timeout.InfiniteTimeSpan)
                : null;
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void OnBoundary()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, set) in _sets.ToArray())
        {
            if (set.Until is { } until && now >= until)
            {
                _sets[key] = Compute(key, now);
            }
        }

        Arm();
    }

    private void OnEventsChanged(object? sender, EventArgs e)
    {
        _sets.Clear();
        Arm();
    }
}
