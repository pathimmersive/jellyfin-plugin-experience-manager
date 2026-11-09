using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.ExperienceManager.Native;

/// <summary>
/// One registered device in the client census.
/// </summary>
/// <param name="FamilyId">The id of its client family.</param>
/// <param name="AppName">The app name it reported.</param>
/// <param name="AppVersion">The app version it reported.</param>
/// <param name="DeviceName">The device's name, as the admin set it or the app reported it.</param>
/// <param name="LastActivity">When it was last active, in UTC.</param>
public sealed record CensusDevice(string FamilyId, string AppName, string AppVersion, string DeviceName, DateTime LastActivity);

/// <summary>
/// One user's devices in the client census, most recently active first.
/// </summary>
/// <param name="UserId">The user's id, or null for devices no user is signed in on.</param>
/// <param name="UserName">The user's name.</param>
/// <param name="Devices">The user's client devices.</param>
public sealed record CensusUser(Guid? UserId, string UserName, IReadOnlyList<CensusDevice> Devices);

/// <summary>
/// One row of the reach matrix: a client family, how many devices and users run it, and what Experience Manager reaches there.
/// </summary>
/// <param name="Id">The family's id.</param>
/// <param name="Name">The family's name.</param>
/// <param name="Devices">How many registered devices run it.</param>
/// <param name="Users">How many users run it.</param>
/// <param name="Today">What Experience Manager reaches on it now.</param>
/// <param name="Planned">What a planned way of reaching it would add, or null.</param>
/// <param name="AppNames">The app names its devices reported, for families matched by name or for Other.</param>
public sealed record ReachRow(string Id, string Name, int Devices, int Users, string Today, string? Planned, IReadOnlyList<string> AppNames);

/// <summary>
/// The client census: which clients each user runs, the reach matrix, and the server apps that sign in as devices.
/// </summary>
/// <param name="TotalDevices">Every registered device, services included.</param>
/// <param name="Users">Each user's client devices.</param>
/// <param name="Reach">The reach matrix: every client family, with the families in use first.</param>
/// <param name="Services">Server apps that sign in as devices, listed apart from clients.</param>
public sealed record ClientCensusResponse(int TotalDevices, IReadOnlyList<CensusUser> Users, IReadOnlyList<ReachRow> Reach, IReadOnlyList<CensusDevice> Services);

/// <summary>
/// Builds the client census from Jellyfin's registered devices.
/// </summary>
public static class ClientCensus
{
    /// <summary>
    /// Builds the census.
    /// </summary>
    /// <param name="devices">Every registered device.</param>
    /// <returns>The census.</returns>
    public static ClientCensusResponse Build(IReadOnlyList<DeviceInfoDto> devices)
    {
        var entries = devices
            .Select(d => (Info: d, Family: ClientFamilies.For(d.AppName)))
            .Select(e => (e.Info, e.Family, Device: new CensusDevice(
                e.Family.Id,
                e.Info.AppName ?? string.Empty,
                e.Info.AppVersion ?? string.Empty,
                string.IsNullOrWhiteSpace(e.Info.CustomName) ? e.Info.Name ?? string.Empty : e.Info.CustomName,
                e.Info.DateLastActivity ?? DateTime.MinValue)))
            .ToList();
        var clients = entries.Where(e => !e.Family.IsService).ToList();

        var users = clients
            .GroupBy(e => e.Info.LastUserId is { } id && id != Guid.Empty ? id : (Guid?)null)
            .Select(g => new CensusUser(
                g.Key,
                g.Select(e => e.Info.LastUserName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty,
                g.Select(e => e.Device).OrderByDescending(d => d.LastActivity).ToList()))
            .OrderByDescending(u => u.Devices[0].LastActivity)
            .ToList();

        var reach = ClientFamilies.All
            .Where(f => !f.IsService)
            .Select((family, order) =>
            {
                var used = clients.Where(e => e.Family.Id == family.Id).ToList();
                return (Order: order, Row: new ReachRow(
                    family.Id,
                    family.Name,
                    used.Count,
                    used.Select(e => e.Info.LastUserId).Distinct().Count(),
                    family.Today,
                    family.Planned,
                    used.Select(e => e.Device.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList()));
            })
            .OrderBy(r => r.Row.Devices == 0)
            .ThenBy(r => r.Order)
            .Select(r => r.Row)
            .ToList();

        var services = entries
            .Where(e => e.Family.IsService)
            .Select(e => e.Device)
            .OrderByDescending(d => d.LastActivity)
            .ToList();

        return new ClientCensusResponse(devices.Count, users, reach, services);
    }
}
