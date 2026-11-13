using System;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Integration;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Native;
using Jellyfin.Plugin.ExperienceManager.Native.Adapters;
using Jellyfin.Plugin.ExperienceManager.Programming;
using Jellyfin.Plugin.ExperienceManager.Publishing;
using Jellyfin.Plugin.ExperienceManager.Rules;
using Jellyfin.Plugin.ExperienceManager.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
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
        serviceCollection.AddSingleton(services => new LayoutDatabase(LayoutDatabase.DefaultPath(services.GetRequiredService<IApplicationPaths>())));
        serviceCollection.AddSingleton<PageStore>();
        serviceCollection.AddSingleton<AudienceStore>();
        serviceCollection.AddSingleton<AudienceResolver>();
        serviceCollection.AddSingleton<IEventConsumer<UserCreatedEventArgs>, UserCreatedRecorder>();
        serviceCollection.AddSingleton<IEventConsumer<UserDeletedEventArgs>, UserDeletedRecorder>();
        serviceCollection.AddSingleton<EventStore>();
        serviceCollection.AddSingleton<RulesStore>();
        serviceCollection.AddSingleton<Announcements.DismissalStore>();
        serviceCollection.AddSingleton<Announcements.AnnouncementImages>();
        serviceCollection.AddSingleton<RuleFilter>();
        serviceCollection.AddSingleton<PublishStore>();
        serviceCollection.AddHostedService<HistoryLog>();
        serviceCollection.AddSingleton<EventTimeline>();
        serviceCollection.AddSingleton<StoredPages>();
        serviceCollection.AddSingleton<IPages>(services => services.GetRequiredService<StoredPages>());
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
