using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.ExperienceManager.Announcements;

/// <summary>
/// The images uploaded for announcements, kept in the plugin's data folder and named by their content, so the
/// same image is stored once and an image an older published version uses is never lost.
/// </summary>
public sealed partial class AnnouncementImages
{
    /// <summary>The largest image taken: 5 MB.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    private readonly string _folder;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnnouncementImages"/> class.
    /// </summary>
    /// <param name="paths">Jellyfin's paths.</param>
    public AnnouncementImages(IApplicationPaths paths)
        : this(Path.Combine(paths.DataPath, "experience-manager", "images"))
    {
    }

    internal AnnouncementImages(string folder)
    {
        _folder = folder;
    }

    /// <summary>
    /// Gets a value indicating whether a text is an image id as uploads are named: a hash and the image's type.
    /// </summary>
    /// <param name="id">The text.</param>
    /// <returns>Whether it is one.</returns>
    public static bool IsId(string id) => IdPattern().IsMatch(id);

    /// <summary>
    /// Gets an image's content type from its id.
    /// </summary>
    /// <param name="id">The image id.</param>
    /// <returns>The content type.</returns>
    public static string ContentType(string id) => Path.GetExtension(id) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>
    /// Reads an upload and stores it when it is a PNG, JPEG or WebP image of at most 5 MB.
    /// </summary>
    /// <param name="body">The upload.</param>
    /// <param name="id">The stored image's id.</param>
    /// <returns>Why it was refused, or null.</returns>
    public string? Save(Stream body, out string? id)
    {
        id = null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                return "The image is larger than 5 MB.";
            }
        }

        var bytes = buffer.ToArray();
        var extension = ExtensionOf(bytes);
        if (extension is null)
        {
            return "Only PNG, JPEG and WebP images are taken.";
        }

        id = Convert.ToHexStringLower(SHA256.HashData(bytes))[..32] + extension;
        var path = Path.Combine(_folder, id);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(_folder);
            var temporary = path + ".part";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }

        return null;
    }

    /// <summary>
    /// Opens a stored image.
    /// </summary>
    /// <param name="id">The image id.</param>
    /// <returns>The image, or null when there is no such image.</returns>
    public Stream? Open(string id)
    {
        if (!IsId(id))
        {
            return null;
        }

        var path = Path.Combine(_folder, id);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    // The type from the file's first bytes, not from what the upload claims.
    internal static string? ExtensionOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        return null;
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.(png|jpg|webp)$")]
    private static partial Regex IdPattern();
}
