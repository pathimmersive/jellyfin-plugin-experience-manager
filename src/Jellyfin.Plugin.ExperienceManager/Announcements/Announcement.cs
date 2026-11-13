using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ExperienceManager.Layouts;

namespace Jellyfin.Plugin.ExperienceManager.Announcements;

/// <summary>
/// An announcement on Home: a title, one line of text or up to three tips, an optional image and an optional
/// button that opens something on the server. A client that does not know the <c>announcement</c> kind skips it.
/// </summary>
/// <param name="Lines">The text: one line, or up to three tips, one per line.</param>
/// <param name="Image">The image's path on the server, such as <c>/ExperienceManager/v1/Images/{imageId}</c>, or null.</param>
/// <param name="Button">The button, or null when there is none or the viewer cannot open what it opens.</param>
/// <param name="Dismissible">Whether the viewer may dismiss it, with <c>POST /ExperienceManager/v1/Sections/{sectionId}/Dismiss</c>.</param>
/// <param name="Version">Changes when the title, text, image or button change; a dismissal holds until it does.</param>
public sealed record Announcement(IReadOnlyList<string> Lines, string? Image, AnnouncementButton? Button, bool Dismissible, string Version);

/// <summary>
/// An announcement's button. It never leads outside the app: it opens an item, a collection or a library.
/// </summary>
/// <param name="Label">The button's text.</param>
/// <param name="ItemId">The item, collection or library it opens.</param>
/// <param name="Target">What it opens: <c>item</c>, <c>collection</c> or <c>library</c>.</param>
/// <param name="CollectionType">For a library, its Jellyfin collection type, such as <c>movies</c>; otherwise null.</param>
public sealed record AnnouncementButton(string Label, string ItemId, string Target, string? CollectionType);

/// <summary>
/// An announcement section's content, read from its config: <c>Text</c>, <c>Image</c>, <c>ButtonLabel</c>,
/// <c>ButtonTarget</c> and <c>Dismissible</c>.
/// </summary>
/// <param name="Title">The section's title, or null.</param>
/// <param name="Lines">The text, one entry per line.</param>
/// <param name="ImageId">The uploaded image's id, or null.</param>
/// <param name="ButtonLabel">The button's text, or null.</param>
/// <param name="ButtonTarget">What the button opens, or null.</param>
/// <param name="Dismissible">Whether a viewer may dismiss it.</param>
public sealed record AnnouncementContent(string? Title, IReadOnlyList<string> Lines, string? ImageId, string? ButtonLabel, Guid? ButtonTarget, bool Dismissible)
{
    /// <summary>The most lines an announcement has: a welcome's three tips.</summary>
    public const int MaxLines = 3;

    /// <summary>The longest a line may be.</summary>
    public const int MaxLineLength = 200;

    /// <summary>
    /// Gets what a dismissal is held against: the title, text, image and button, hashed.
    /// </summary>
    public string Version
    {
        get
        {
            var text = JsonSerializer.Serialize(new object?[] { Title, Lines, ImageId, ButtonLabel, ButtonTarget?.ToString("N") });
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        }
    }

    /// <summary>
    /// Reads a section's announcement.
    /// </summary>
    /// <param name="section">The section.</param>
    /// <returns>Its content.</returns>
    public static AnnouncementContent Of(StoredSection section)
    {
        var config = new SectionConfig(section.Config);
        return new AnnouncementContent(
            string.IsNullOrWhiteSpace(section.Title) ? null : section.Title.Trim(),
            LinesOf(config.Text("Text")),
            config.Text("Image"),
            config.Text("ButtonLabel"),
            config.Guid("ButtonTarget"),
            config.Flag("Dismissible"));
    }

    /// <summary>
    /// Checks a section's announcement.
    /// </summary>
    /// <param name="section">The section.</param>
    /// <returns>Why it cannot be used, or null.</returns>
    public static string? Validate(StoredSection section)
    {
        var config = new SectionConfig(section.Config);
        var content = Of(section);
        if (content.Lines.Count == 0)
        {
            return $"announcement {section.Id} needs Text";
        }

        if (content.Lines.Count > MaxLines)
        {
            return $"announcement {section.Id} has more than {MaxLines} lines";
        }

        if (content.Lines.Any(l => l.Length > MaxLineLength))
        {
            return $"announcement {section.Id} has a line longer than {MaxLineLength} characters";
        }

        if (content.ImageId is not null && !AnnouncementImages.IsId(content.ImageId))
        {
            return $"announcement {section.Id} has an Image that is not an uploaded image's id";
        }

        if (config.Text("ButtonTarget") is not null && content.ButtonTarget is null)
        {
            return $"announcement {section.Id} has a ButtonTarget that is not an id";
        }

        if ((content.ButtonTarget is null) != (content.ButtonLabel is null))
        {
            return $"announcement {section.Id} needs both ButtonLabel and ButtonTarget, or neither";
        }

        return null;
    }

    private static IReadOnlyList<string> LinesOf(string? text) =>
        text is null
            ? []
            : text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
