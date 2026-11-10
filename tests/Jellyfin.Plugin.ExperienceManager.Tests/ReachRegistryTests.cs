using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Configuration;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Native;
using Jellyfin.Plugin.ExperienceManager.Native.Adapters.HomeClient;
using MediaBrowser.Controller.Entities;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class ReachRegistryTests
{
    private static readonly User Viewer = new("viewer", "auth", "reset") { Id = Guid.NewGuid() };

    [Fact]
    public void On_a_fresh_install_only_the_home_client_is_on()
    {
        var registry = ReachKit.Registry(new ConfigurationReachSwitches(() => new PluginConfiguration(), () => { }));

        Assert.Equal(["home-client"], registry.Adapters.Where(registry.IsOn).Select(a => a.Id));
    }

    [Fact]
    public void The_home_client_is_a_serve_adapter_for_jellyfin_web_and_the_apps_that_load_it_and_is_ready()
    {
        var registry = ReachKit.Registry();
        var homeClient = registry.Adapters.Single(a => a.Id == HomeClientAdapter.AdapterId);

        Assert.Equal(ReachMechanism.Serve, homeClient.Mechanism);
        Assert.Equal(["web", "webos", "tizen", "android", "ios"], homeClient.Families);
        Assert.Equal(ReachState.Ready, registry.Status(homeClient).State);
        Assert.True(registry.IsOn(homeClient));

        var plan = homeClient.Plan(ReachKit.Experience, homeClient.Detect());
        Assert.Empty(plan.Losses);
        Assert.Equal(ReachKit.Experience.Sections.Select(s => s.Id), plan.Operations.Select(o => o.SectionId));
    }

    [Fact]
    public void Planned_adapters_show_as_not_built_and_cannot_be_switched_on()
    {
        var registry = ReachKit.Registry();
        var planned = registry.Adapters.Where(a => !a.Built).ToList();

        Assert.Equal(["moonfin-rows", "wholphin-rows", "streamyfin-sections", "stock-home-sections", "maintained-collections"], planned.Select(a => a.Id));
        Assert.All(planned, a =>
        {
            Assert.False(registry.SetOn(a.Id, true));
            Assert.False(registry.IsOn(a));
            Assert.Equal("not built yet", registry.Status(a).Label);
        });
        Assert.All(registry.Statuses().Where(s => !s.Built), s => Assert.Equal(("not built yet", false), (s.Status, s.On)));
    }

    [Fact]
    public void An_adapter_that_is_off_writes_nothing()
    {
        var target = new FakeTarget();
        var registry = ReachKit.Registry(null, new FakeWriteAdapter(target));
        var fake = registry.Adapters.Single(a => a.Id == "fake-write");
        var plan = fake.Plan(ReachKit.Experience, fake.Detect());
        var before = target.Snapshot();

        var applied = registry.Apply("fake-write", plan);

        Assert.False(registry.IsOn(fake));
        Assert.Equal(0, applied.Changed);
        Assert.Equal("Switched off; it writes nothing.", applied.Refused);
        Assert.Equal(before, target.Snapshot());

        Assert.True(registry.SetOn("fake-write", true));
        Assert.Equal(plan.Operations.Count, registry.Apply("fake-write", plan).Changed);
    }

    [Fact]
    public void Switches_are_kept_in_the_plugin_configuration_and_survive_a_save()
    {
        var configuration = new PluginConfiguration();
        var saves = 0;
        var registry = ReachKit.Registry(new ConfigurationReachSwitches(() => configuration, () => saves++));

        Assert.True(registry.SetOn("home-client", false));
        Assert.Equal(1, saves);

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new StringWriter();
        serializer.Serialize(buffer, configuration);
        var reloaded = (PluginConfiguration)serializer.Deserialize(new StringReader(buffer.ToString()))!;

        var after = ReachKit.Registry(new ConfigurationReachSwitches(() => reloaded, () => { }));
        Assert.False(after.IsOn(after.Adapters.Single(a => a.Id == "home-client")));
        Assert.Equal("off", after.Statuses().Single(s => s.Id == "home-client").Status);
    }

    [Fact]
    public void The_home_client_refuses_a_server_older_than_it_is_built_for()
    {
        var adapter = new HomeClientAdapter(() => ReachKit.Unsupported);

        var target = adapter.Detect();

        Assert.Equal((ReachState.UnsupportedVersion, "10.10.7"), (target.State, target.Version));
        Assert.Contains("older than 12.0", target.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_resolver_gives_the_sections_in_order_with_display_and_source_without_loading_items()
    {
        var page = Substitute.For<IPages>();
        page.LayoutVersion("home").Returns("r1");
        page.SectionsFor("home", Viewer).Returns(
        [
            new PageSection("featured", "Featured", new SectionDisplay("feature", "landscape"), new SectionSource(SectionSources.Featured), null, 5, _ => throw new InvalidOperationException("items loaded")),
            new PageSection("latest-x", "Latest Movies", new SectionDisplay("row", "portrait"), new SectionSource(SectionSources.Latest, "x", "Movie"), new SeeAllTarget("x", "Movie", "DateCreated", "Descending", null), 16, _ => throw new InvalidOperationException("items loaded")),
        ]);

        var resolved = new ExperienceResolver(page).Resolve(Viewer, "moonfin");

        Assert.Equal(("home", "r1", "moonfin"), (resolved.PageId, resolved.LayoutVersion, resolved.FamilyId));
        Assert.Equal(["featured", "latest-x"], resolved.Sections.Select(s => s.Id));
        Assert.Equal("feature", resolved.Sections[0].Display.Hint);
        Assert.Equal(new SectionSource(SectionSources.Latest, "x", "Movie"), resolved.Sections[1].Source);
        Assert.Equal("x", resolved.Sections[1].SeeAll!.ParentId);
    }
}
