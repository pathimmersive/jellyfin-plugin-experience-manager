using System;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Integration;
using Jellyfin.Plugin.ExperienceManager.Native;
using Jellyfin.Plugin.ExperienceManager.Native.Adapters;
using Jellyfin.Plugin.ExperienceManager.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ExperienceManager;

/// <summary>
/// Registers the plugin's services with the host container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HomeClient>();
        serviceCollection.AddTransient<IStartupFilter, IndexPageStartupFilter>();

        serviceCollection.AddSingleton<HomeCache>();
        serviceCollection.AddSingleton<IHomeSources, JellyfinHomeSources>();
        serviceCollection.AddSingleton<IHomePage, FixedHomePage>();
        serviceCollection.AddSingleton<HomePageService>();
        serviceCollection.AddSingleton<IScheduledTask, WarmHomeCacheTask>();

        serviceCollection.AddSingleton<ExperienceResolver>();
        foreach (var adapter in ReachAdapters.Create(() => applicationHost.ApplicationVersion))
        {
            serviceCollection.AddSingleton(adapter);
        }

        serviceCollection.AddSingleton<IReachSwitches>(_ => new ConfigurationReachSwitches(
            () => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("The plugin is not loaded."),
            () => Plugin.Instance?.SaveConfiguration()));
        serviceCollection.AddSingleton<ReachRegistry>();
    }
}
