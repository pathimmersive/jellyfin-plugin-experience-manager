using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Jellyfin.Plugin.ExperienceManager.Native;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class ClientCensusTests
{
    private static readonly Guid Ann = Guid.NewGuid();
    private static readonly Guid Ben = Guid.NewGuid();

    // App names real servers report, each with the family it belongs to.
    [Theory]
    [InlineData("Jellyfin Web", "web")]
    [InlineData("Jellyfin for WebOS", "webos")]
    [InlineData("Jellyfin for Tizen", "tizen")]
    [InlineData("Android TV", "androidtv")]
    [InlineData("Jellyfin Android TV", "androidtv")]
    [InlineData("Jellyfin Android", "android")]
    [InlineData("Jellyfin for Android", "android")]
    [InlineData("Jellyfin iOS", "ios")]
    [InlineData("Jellyfin iPadOS", "ios")]
    [InlineData("Jellyfin Mobile (iOS)", "ios")]
    [InlineData("Jellyfin Roku", "roku")]
    [InlineData("Moonfin for Android TV", "moonfin")]
    [InlineData("Moonfin for iOS", "moonfin")]
    [InlineData("Wholphin", "wholphin")]
    [InlineData("Streamyfin", "streamyfin")]
    [InlineData("Infuse-Direct", "infuse")]
    [InlineData("Swiftfin tvOS", "swiftfin")]
    [InlineData("jellyfin web", "web")]
    public void Each_client_app_maps_to_its_family(string appName, string family)
    {
        var mapped = ClientFamilies.For(appName);
        Assert.Equal(family, mapped.Id);
        Assert.False(mapped.IsService);
    }

    [Theory]
    [InlineData("Seerr")]
    [InlineData("Jellyseerr")]
    [InlineData("Streamystats")]
    public void Server_apps_are_services_not_clients(string appName) =>
        Assert.True(ClientFamilies.For(appName).IsService);

    [Theory]
    [InlineData("Some New Player")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_apps_are_other(string? appName) =>
        Assert.Same(ClientFamilies.Other, ClientFamilies.For(appName));

    [Fact]
    public void Every_family_has_a_stable_id_and_other_comes_last()
    {
        Assert.Equal("other", ClientFamilies.All[^1].Id);
        Assert.Equal(ClientFamilies.All.Count, ClientFamilies.All.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void Every_family_the_adapters_name_exists()
    {
        var known = ClientFamilies.All.Select(f => f.Id).ToHashSet();
        Assert.All(ReachKit.Registry().Adapters.SelectMany(a => a.Families), id => Assert.Contains(id, known));
    }

    [Fact]
    public void The_matrix_says_what_each_family_gets_from_the_adapters()
    {
        var registry = ReachKit.Registry();
        var reach = ClientCensus.Build([], registry.Statuses()).Reach.ToDictionary(r => r.Id);

        var homeClient = registry.Adapters.Single(a => a.Id == "home-client");
        Assert.Equal(homeClient.Reaches, reach["web"].Today);
        Assert.Equal(["home-client"], reach["webos"].Adapters);
        Assert.Null(reach["web"].Planned);
        Assert.Equal(ClientCensus.ContentOnly, reach["androidtv"].Today);
        Assert.Equal("Stock home sections written from the layout.", reach["androidtv"].Planned);
        Assert.Equal(["stock-home-sections"], reach["roku"].Adapters);
        Assert.Equal(ClientCensus.ContentOnly, reach["other"].Today);
        Assert.Null(reach["other"].Planned);
        Assert.All(reach.Values, r => Assert.False(string.IsNullOrWhiteSpace(r.Today)));
    }

    [Fact]
    public void With_the_home_client_off_web_gets_content_only()
    {
        var registry = ReachKit.Registry();
        registry.SetOn("home-client", false);

        var census = ClientCensus.Build([], registry.Statuses());

        Assert.Equal(ClientCensus.ContentOnly, census.Reach.Single(r => r.Id == "web").Today);
        Assert.Equal("off", census.Adapters.Single(a => a.Id == "home-client").Status);
    }

    [Fact]
    public void The_census_groups_devices_by_user_with_services_apart()
    {
        var census = ClientCensus.Build(
        [
            Device("Moonfin for Android TV", "3.0.0", "Fire TV Stick", Ann, "ann", 3),
            Device("Jellyfin Web", "12.2.0", "Firefox", Ann, "ann", 1),
            Device("Wholphin", "1.1.0", "Shield", Ben, "ben", 2),
            Device("Seerr", "3.5.0", "Seerr", Ann, "ann", 0),
            Device("Some New Player", "0.1", "Phone", Ben, "ben", 5),
        ], ReachKit.Registry().Statuses());

        Assert.Equal(5, census.TotalDevices);
        Assert.Equal(["ann", "ben"], census.Users.Select(u => u.UserName));
        var ann = census.Users[0];
        Assert.Equal(["web", "moonfin"], ann.Devices.Select(d => d.FamilyId));
        Assert.Equal("Firefox", ann.Devices[0].DeviceName);
        Assert.Equal(["wholphin", "other"], census.Users[1].Devices.Select(d => d.FamilyId));

        var service = Assert.Single(census.Services);
        Assert.Equal("Seerr", service.AppName);
        Assert.DoesNotContain(census.Users.SelectMany(u => u.Devices), d => d.AppName == "Seerr");
    }

    [Fact]
    public void The_reach_matrix_lists_every_family_with_those_in_use_first()
    {
        var census = ClientCensus.Build(
        [
            Device("Moonfin for Android TV", "3.0.0", "Fire TV Stick", Ann, "ann", 1),
            Device("Moonfin for iOS", "2.5.1", "iPhone", Ben, "ben", 2),
            Device("Some New Player", "0.1", "Phone", Ben, "ben", 3),
        ], ReachKit.Registry().Statuses());

        Assert.Equal(["moonfin", "other"], census.Reach.Take(2).Select(r => r.Id));
        var moonfin = census.Reach[0];
        Assert.Equal((2, 2), (moonfin.Devices, moonfin.Users));
        Assert.Equal(["Moonfin for Android TV", "Moonfin for iOS"], moonfin.AppNames);
        Assert.NotNull(moonfin.Planned);
        Assert.Equal(["Some New Player"], census.Reach[1].AppNames);
        Assert.Equal(ClientFamilies.All.Count(f => !f.IsService), census.Reach.Count);
        Assert.DoesNotContain(census.Reach, r => r.Id == "services");
    }

    [Fact]
    public void A_custom_device_name_wins_over_the_reported_one()
    {
        var device = Device("Jellyfin Web", "12.2.0", "Chrome", Ann, "ann", 1);
        device.CustomName = "Living room PC";
        Assert.Equal("Living room PC", ClientCensus.Build([device], []).Users[0].Devices[0].DeviceName);
    }

    [Fact]
    public void The_admin_routes_are_for_administrators_only()
    {
        var policy = typeof(AdminController).GetCustomAttribute<AuthorizeAttribute>()?.Policy;
        Assert.Equal("RequiresElevation", policy);
        Assert.StartsWith("ExperienceManager/Admin", AdminController.Route, StringComparison.Ordinal);
    }

    [Fact]
    public void The_clients_route_reads_every_registered_device()
    {
        var devices = Substitute.For<IDeviceManager>();
        devices.GetDevicesForUser(null).Returns(new QueryResult<DeviceInfoDto>([Device("Wholphin", "1.1.0", "Shield", Ben, "ben", 1)]));
        var census = new AdminController(devices, ReachKit.Registry()).GetClients().Value!;
        Assert.Equal("wholphin", census.Users.Single().Devices.Single().FamilyId);
        Assert.Equal(["home-client", "moonfin-rows", "wholphin-rows", "streamyfin-sections", "stock-home-sections", "maintained-collections"], census.Adapters.Select(a => a.Id));
        Assert.Equal("ready", census.Adapters[0].Status);
        Assert.All(census.Adapters.Skip(1), a => Assert.Equal("not built yet", a.Status));
    }

    private static DeviceInfoDto Device(string app, string version, string name, Guid user, string userName, int daysAgo) => new()
    {
        AppName = app,
        AppVersion = version,
        Name = name,
        LastUserId = user,
        LastUserName = userName,
        DateLastActivity = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo),
    };
}
