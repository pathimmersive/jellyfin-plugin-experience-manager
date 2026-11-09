using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ExperienceManager.Native;

/// <summary>
/// A family of Jellyfin apps that reach users the same way. What Experience Manager reaches there comes
/// from the reach adapters that name the family.
/// </summary>
/// <param name="Id">The family's stable id.</param>
/// <param name="Name">The name an admin knows it by.</param>
/// <param name="IsService">Whether the family is a server app that signs in as a device, not a client a user watches on.</param>
public sealed record ClientFamily(string Id, string Name, bool IsService);

/// <summary>
/// Maps the app name a device reports to its client family.
/// </summary>
public static class ClientFamilies
{
    /// <summary>Apps nobody has mapped yet. They show with the name they reported.</summary>
    public static readonly ClientFamily Other = new("other", "Other", false);

    // Each family with the app names its apps report, matched whole or, ending in '*', by prefix; case is ignored.
    private static readonly (ClientFamily Family, string[] Names)[] Map =
    [
        (new("web", "Jellyfin Web and Desktop", false), ["Jellyfin Web", "Jellyfin Media Player", "Jellyfin Desktop"]),
        (new("webos", "Jellyfin for LG webOS", false), ["Jellyfin for WebOS"]),
        (new("tizen", "Jellyfin for Samsung Tizen", false), ["Jellyfin for Tizen"]),
        (new("android", "Jellyfin for Android", false), ["Jellyfin Android", "Jellyfin for Android"]),
        (new("ios", "Jellyfin for iPhone and iPad", false), ["Jellyfin iOS", "Jellyfin iPadOS", "Jellyfin Mobile (iOS)"]),
        (new("androidtv", "Jellyfin for Android TV and Fire TV", false), ["Android TV", "Jellyfin Android TV"]),
        (new("roku", "Jellyfin for Roku", false), ["Jellyfin Roku"]),
        (new("moonfin", "Moonfin", false), ["Moonfin*"]),
        (new("wholphin", "Wholphin", false), ["Wholphin*"]),
        (new("streamyfin", "Streamyfin", false), ["Streamyfin*"]),
        (new("swiftfin", "Swiftfin", false), ["Swiftfin*"]),
        (new("infuse", "Infuse", false), ["Infuse*"]),
        (new("findroid", "Findroid", false), ["Findroid*"]),
        (new("services", "Server apps", true), ["Seerr", "Jellyseerr", "Overseerr", "Streamystats", "Jellystat", "Jellyfin Vue"]),
    ];

    /// <summary>
    /// Gets every known family, in the order an admin reads them, then <see cref="Other"/>.
    /// </summary>
    public static IReadOnlyList<ClientFamily> All { get; } = Map.Select(m => m.Family).Append(Other).ToList();

    /// <summary>
    /// Gets the family of the app that reported a name.
    /// </summary>
    /// <param name="appName">The app name the device reported, or null.</param>
    /// <returns>Its family, or <see cref="Other"/>.</returns>
    public static ClientFamily For(string? appName)
    {
        var name = appName?.Trim() ?? string.Empty;
        foreach (var (family, names) in Map)
        {
            if (names.Any(n => Matches(n, name)))
            {
                return family;
            }
        }

        return Other;
    }

    private static bool Matches(string pattern, string name) => pattern.EndsWith('*')
        ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
}
