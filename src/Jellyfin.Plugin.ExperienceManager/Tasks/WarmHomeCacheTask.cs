using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExperienceManager.Caching;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ExperienceManager.Tasks;

/// <summary>
/// Builds the home page's shared lists when the server starts, so the first user to open Home
/// does not wait for them.
/// </summary>
public sealed class WarmHomeCacheTask : IScheduledTask
{
    private readonly HomeCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="WarmHomeCacheTask"/> class.
    /// </summary>
    /// <param name="cache">The home cache.</param>
    public WarmHomeCacheTask(HomeCache cache)
    {
        _cache = cache;
    }

    /// <inheritdoc />
    public string Name => "Warm the home screen cache";

    /// <inheritdoc />
    public string Key => "ExperienceManagerWarmHomeCache";

    /// <inheritdoc />
    public string Description => "Builds the lists Experience Manager's home screen shares between users, so Home opens quickly after a restart.";

    /// <inheritdoc />
    public string Category => "Experience Manager";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _cache.Warm();
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }];
}
