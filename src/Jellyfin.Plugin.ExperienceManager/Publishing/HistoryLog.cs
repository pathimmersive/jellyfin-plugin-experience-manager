using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Programming;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExperienceManager.Publishing;

/// <summary>
/// Writes a line to History, and the same entry to Jellyfin's activity log, when a published event starts or ends,
/// so an admin can see why Home changed.
/// </summary>
public sealed partial class HistoryLog : IHostedService
{
    private readonly EventTimeline _timeline;
    private readonly PublishStore _published;
    private readonly IActivityManager _activity;
    private readonly TimeProvider _time;
    private readonly ILogger<HistoryLog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryLog"/> class.
    /// </summary>
    /// <param name="timeline">Which events are on.</param>
    /// <param name="published">History.</param>
    /// <param name="activity">Jellyfin's activity log.</param>
    /// <param name="logger">The logger.</param>
    public HistoryLog(EventTimeline timeline, PublishStore published, IActivityManager activity, ILogger<HistoryLog> logger)
        : this(timeline, published, activity, logger, TimeProvider.System)
    {
    }

    internal HistoryLog(EventTimeline timeline, PublishStore published, IActivityManager activity, ILogger<HistoryLog> logger, TimeProvider time)
    {
        _timeline = timeline;
        _published = published;
        _activity = activity;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timeline.Boundary += OnBoundary;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timeline.Boundary -= OnBoundary;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the line for an event starting or ending, in the server's time zone.
    /// </summary>
    /// <param name="value">The event.</param>
    /// <param name="started">Whether it started.</param>
    /// <param name="at">When.</param>
    /// <param name="zone">The server's time zone.</param>
    /// <returns>The line, as in "Halloween started 1 Oct 00:00".</returns>
    internal static string Line(ProgrammedEvent value, bool started, DateTimeOffset at, TimeZoneInfo zone) =>
        value.Name + (started ? " started " : " ended ") + TimeZoneInfo.ConvertTime(at, zone).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    internal void OnBoundary(ProgrammedEvent value, bool started, DateTimeOffset at)
    {
        var line = Line(value, started, at, _time.LocalTimeZone);
        _published.AddEntry(at, line);
        _ = WriteActivity(line);
    }

    private async Task WriteActivity(string line)
    {
        try
        {
            await _activity.CreateAsync(new ActivityLog("Experience Manager: " + line, "ExperienceManagerEvent", Guid.Empty)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogActivityFailed(_logger, line, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write '{Line}' to the activity log")]
    private static partial void LogActivityFailed(ILogger logger, string line, Exception exception);
}
