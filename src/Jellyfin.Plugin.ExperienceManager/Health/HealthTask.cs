using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExperienceManager.Health;

/// <summary>
/// Once a day, writes each event starting within a week that has a problem to Jellyfin's activity log, so it shows
/// on the Dashboard without opening Experience Manager.
/// </summary>
public sealed class HealthTask : IScheduledTask
{
    private readonly HealthChecks _checks;
    private readonly IActivityManager _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthTask"/> class.
    /// </summary>
    /// <param name="checks">The health checks.</param>
    /// <param name="activity">Jellyfin's activity log.</param>
    public HealthTask(HealthChecks checks, IActivityManager activity)
    {
        _checks = checks;
        _activity = activity;
    }

    /// <inheritdoc />
    public string Name => "Check upcoming events";

    /// <inheritdoc />
    public string Key => "ExperienceManagerHealth";

    /// <inheritdoc />
    public string Description => "Checks the events starting within a week for a missing or empty source and missing artwork, and writes each problem to the activity log.";

    /// <inheritdoc />
    public string Category => "Experience Manager";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var findings = _checks.Report(refresh: true).Findings.Where(f => f.Kind is "starts-soon" or "source-missing" && f.EventId is not null).ToList();
        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _activity.CreateAsync(new ActivityLog("Experience Manager: " + finding.Title + ": " + finding.Text, "ExperienceManagerHealth", Guid.Empty)
            {
                ShortOverview = finding.Text,
                LogSeverity = LogLevel.Warning,
            }).ConfigureAwait(false);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(9).Ticks }];
}
