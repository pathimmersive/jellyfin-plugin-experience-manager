using System;
using System.IO;
using Jellyfin.Plugin.ExperienceManager.Layouts;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

/// <summary>
/// A database file of its own for one test, deleted with its folder afterwards.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "em-tests-" + Guid.NewGuid().ToString("N"));

    public string FilePath => Path.Combine(_folder, "data", "experience-manager.db");

    public LayoutDatabase Open() => new(FilePath);

    public PageStore Store() => new(Open());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }
}
