using System.Collections.Generic;

namespace Jellyfin.Plugin.ExperienceManager.Layouts;

/// <summary>
/// The section types a page can store.
/// </summary>
public static class SectionTypes
{
    /// <summary>The featured banner.</summary>
    public const string Featured = "featured";

    /// <summary>The user's libraries.</summary>
    public const string MyMedia = "mymedia";

    /// <summary>Items the user started and has not finished.</summary>
    public const string ContinueWatching = "continue";

    /// <summary>The next episode of each show the user is watching.</summary>
    public const string NextUp = "nextup";

    /// <summary>
    /// The latest items of each movie and show library the user can see, one section per library, in the
    /// user's library order. A library added later gets its section without an edit.
    /// </summary>
    public const string LatestLibraries = "latest-libraries";

    /// <summary>Every type, in the order the admin API lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Featured, MyMedia, ContinueWatching, NextUp, LatestLibraries];
}
