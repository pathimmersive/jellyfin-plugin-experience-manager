using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ExperienceManager.Native;

/// <summary>
/// A family of Jellyfin apps that reach users the same way, and what Experience Manager reaches there.
/// </summary>
/// <param name="Id">The family's stable id.</param>
/// <param name="Name">The name an admin knows it by.</param>
/// <param name="IsService">Whether the family is a server app that signs in as a device, not a client a user watches on.</param>
/// <param name="Today">What Experience Manager reaches on these apps now.</param>
/// <param name="Planned">What a planned way of reaching them would add, or null when nothing is planned.</param>
public sealed record ClientFamily(string Id, string Name, bool IsService, string Today, string? Planned);

/// <summary>
/// Maps the app name a device reports to its client family.
/// </summary>
public static class ClientFamilies
{
    private const string FullHome = "The full home screen: the featured banner and every section, as the layout sets them.";
    private const string ContentOnly = "Content only, as Jellyfin serves it: no Experience Manager sections yet.";
    private const string NotAClient = "Not a client: nothing to reach.";

    /// <summary>Apps nobody has mapped yet. They show with the name they reported.</summary>
    public static readonly ClientFamily Other = new("other", "Other", false, "Unknown: this app is not mapped yet.", null);

    // Each family with the app names its apps report, matched whole or, ending in '*', by prefix; case is ignored.
    private static readonly (ClientFamily Family, string[] Names)[] Map =
    [
        (new("web", "Jellyfin Web and Desktop", false, FullHome, null), ["Jellyfin Web", "Jellyfin Media Player", "Jellyfin Desktop"]),
        (new("webos", "Jellyfin for LG webOS", false, FullHome, null), ["Jellyfin for WebOS"]),
        (new("tizen", "Jellyfin for Samsung Tizen", false, FullHome, null), ["Jellyfin for Tizen"]),
        (new("android", "Jellyfin for Android", false, FullHome, null), ["Jellyfin Android", "Jellyfin for Android"]),
        (new("ios", "Jellyfin for iPhone and iPad", false, FullHome, null), ["Jellyfin iOS", "Jellyfin iPadOS", "Jellyfin Mobile (iOS)"]),
        (new("androidtv", "Jellyfin for Android TV and Fire TV", false, ContentOnly, "Stock home sections written from the layout."), ["Android TV", "Jellyfin Android TV"]),
        (new("roku", "Jellyfin for Roku", false, ContentOnly, "Stock home sections written from the layout."), ["Jellyfin Roku"]),
        (new("moonfin", "Moonfin", false, ContentOnly, "Experience Manager sections as Moonfin home rows."), ["Moonfin*"]),
        (new("wholphin", "Wholphin", false, ContentOnly, "Experience Manager sections as Wholphin home rows."), ["Wholphin*"]),
        (new("streamyfin", "Streamyfin", false, ContentOnly, "Experience Manager sections as Streamyfin home sections."), ["Streamyfin*"]),
        (new("swiftfin", "Swiftfin", false, ContentOnly, "Collections kept up to date from sections and events."), ["Swiftfin*"]),
        (new("infuse", "Infuse", false, ContentOnly, "Collections kept up to date from sections and events, which users can pin."), ["Infuse*"]),
        (new("findroid", "Findroid", false, ContentOnly, null), ["Findroid*"]),
        (new("services", "Server apps", true, NotAClient, null), ["Seerr", "Jellyseerr", "Overseerr", "Streamystats", "Jellystat", "Jellyfin Vue"]),
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
