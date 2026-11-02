using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class PluginShapeTests
{
    private static readonly Type[] _pluginTypes = typeof(Plugin).Assembly.GetTypes();

    // Jellyfin creates the plugin before its own services exist, so the pages are read
    // from an instance made without running the constructor.
    private static readonly Plugin _plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

    [Fact]
    public void TargetsNet10()
    {
        var attribute = typeof(Plugin).Assembly.GetCustomAttributes(typeof(TargetFrameworkAttribute), false)
            .Cast<TargetFrameworkAttribute>()
            .Single();

        Assert.Equal(".NETCoreApp,Version=v10.0", attribute.FrameworkName);
    }

    // Jellyfin 12.2 cannot create a plugin derived from the non-generic BasePlugin: it disables the
    // plugin, and GET /Plugins then fails for every plugin on the server.
    [Fact]
    public void PluginHasAConfigurationTypeJellyfinCanCreate()
    {
        var baseType = typeof(Plugin).BaseType!;

        Assert.True(baseType.IsGenericType, $"Plugin derives from {baseType}, expected BasePlugin<TConfiguration>");
        Assert.Equal(typeof(BasePlugin<>), baseType.GetGenericTypeDefinition());
        Assert.True(typeof(BasePluginConfiguration).IsAssignableFrom(baseType.GetGenericArguments().Single()));
    }

    [Fact]
    public void AdminPageHasItsOwnSidebarEntry()
    {
        var page = Assert.Single(_plugin.GetPages());

        Assert.Equal(Plugin.AdminPageName, page.Name);
        Assert.Equal("Experience Manager", page.DisplayName);
        Assert.True(page.EnableInMainMenu);
        Assert.False(string.IsNullOrEmpty(page.MenuIcon));
    }

    [Fact]
    public void AdminPageIsEmbeddedInThePluginAssembly()
    {
        var page = Assert.Single(_plugin.GetPages());

        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Contains("pluginConfigurationPage", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void ServicesAreRegisteredThroughARegistratorAndAllResolve()
    {
        var registrator = _pluginTypes.Single(t => typeof(IPluginServiceRegistrator).IsAssignableFrom(t) && !t.IsAbstract);
        var services = new ServiceCollection();

        ((IPluginServiceRegistrator)Activator.CreateInstance(registrator)!).RegisterServices(services, null!);
        var registered = services.ToList();

        // Jellyfin's container provides logging and its own managers to every service.
        services.AddLogging();
        services.AddSingleton(Substitute.For<MediaBrowser.Controller.Library.ILibraryManager>());
        services.AddSingleton(Substitute.For<MediaBrowser.Controller.Library.IUserManager>());
        services.AddSingleton(Substitute.For<MediaBrowser.Controller.Library.IUserDataManager>());
        services.AddSingleton(Substitute.For<MediaBrowser.Controller.Library.IUserViewManager>());
        services.AddSingleton(Substitute.For<MediaBrowser.Controller.TV.ITVSeriesManager>());
        services.AddSingleton(Substitute.For<MediaBrowser.Controller.Dto.IDtoService>());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotEmpty(registered);
        foreach (var descriptor in registered.Where(d => !d.ServiceType.IsGenericTypeDefinition))
        {
            Assert.NotNull(provider.GetService(descriptor.ServiceType));
        }
    }

    [Fact]
    public void MetaJsonDeclaresTheTargetAbiAndThePluginId()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "meta.json")));

        Assert.Equal("12.0.0.0", meta.RootElement.GetProperty("targetAbi").GetString());
        Assert.Equal(Plugin.PluginId, meta.RootElement.GetProperty("guid").GetString());
        Assert.Equal("Experience Manager", meta.RootElement.GetProperty("name").GetString());
        Assert.Equal("icon.png", meta.RootElement.GetProperty("imagePath").GetString());
    }
}
