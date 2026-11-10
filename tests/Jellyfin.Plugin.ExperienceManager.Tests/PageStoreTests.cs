using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class PageStoreTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static long Count(LayoutDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public void AServerWithNoDatabaseGetsOneAtTheLatestSchemaVersionAndOpeningItAgainChangesNothing()
    {
        Assert.False(File.Exists(_db.FilePath));

        var first = _db.Open();
        Assert.True(File.Exists(_db.FilePath));
        Assert.Equal(LayoutDatabase.Migrations.Max(m => m.Version), first.SchemaVersion());
        _ = new PageStore(first);

        var again = _db.Open();
        _ = new PageStore(again);
        Assert.Equal(first.SchemaVersion(), again.SchemaVersion());
        Assert.Equal(LayoutDatabase.Migrations.Count, Count(again, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1, Count(again, "SELECT COUNT(*) FROM pages;"));
        Assert.Equal(PageStore.HomeSeed.Count, Count(again, "SELECT COUNT(*) FROM sections;"));
    }

    [Fact]
    public void ADatabaseAtAnOlderSchemaVersionIsMigratedForwardAndKeepsItsPages()
    {
        var store = new PageStore(new LayoutDatabase(_db.FilePath, LayoutDatabase.Migrations));
        store.Replace(PageIds.Home, [new("nextup", SectionTypes.NextUp, "Up next")], out _);

        var next = new Migration(LayoutDatabase.Migrations.Max(m => m.Version) + 1, "ALTER TABLE sections ADD COLUMN note TEXT;");
        var migrated = new LayoutDatabase(_db.FilePath, [.. LayoutDatabase.Migrations, next]);

        Assert.Equal(next.Version, migrated.SchemaVersion());
        var page = new PageStore(migrated).Get(PageIds.Home)!;
        Assert.Equal([new StoredSection("nextup", SectionTypes.NextUp, "Up next")], page.Sections);
        Assert.Equal(2, page.Revision);
    }

    [Fact]
    public void AFailingMigrationLeavesTheDatabaseAtTheVersionBeforeIt()
    {
        _ = _db.Store();
        var bad = new Migration(LayoutDatabase.Migrations.Max(m => m.Version) + 1, "CREATE TABLE extra (id TEXT); ALTER TABLE missing ADD COLUMN x TEXT;");

        Assert.Throws<SqliteException>(() => new LayoutDatabase(_db.FilePath, [.. LayoutDatabase.Migrations, bad]));

        var database = _db.Open();
        Assert.Equal(bad.Version - 1, database.SchemaVersion());
        Assert.Equal(0, Count(database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'extra';"));
    }

    [Fact]
    public void AFreshInstallStoresHomeAsItWasServedBefore()
    {
        var home = _db.Store().Get("HOME")!;

        Assert.Equal(["featured", "mymedia", "continue", "nextup", "latest"], home.Sections.Select(s => s.Id));
        Assert.Equal([SectionTypes.Featured, SectionTypes.MyMedia, SectionTypes.ContinueWatching, SectionTypes.NextUp, SectionTypes.LatestLibraries], home.Sections.Select(s => s.Type));
        Assert.All(home.Sections, s => Assert.Null(s.Title));
        Assert.Equal("r1", home.LayoutVersion);
    }

    [Fact]
    public void AnEditIsKeptAcrossARestartWithItsConfigAndANewRevision()
    {
        var config = new JsonObject { ["note"] = "kept" };
        var saved = _db.Store().Replace(PageIds.Home, [new("latest", SectionTypes.LatestLibraries), new("featured", SectionTypes.Featured, "Tonight", config)], out var error);

        Assert.Null(error);
        Assert.Equal("r2", saved!.LayoutVersion);
        var reopened = _db.Store().Get(PageIds.Home)!;
        Assert.Equal("r2", reopened.LayoutVersion);
        Assert.Equal(["latest", "featured"], reopened.Sections.Select(s => s.Id));
        Assert.Equal("Tonight", reopened.Sections[1].Title);
        Assert.Equal("kept", reopened.Sections[1].Config!["note"]!.GetValue<string>());
    }

    [Fact]
    public void SavingTheSameSectionsAgainKeepsTheLayoutVersion()
    {
        var store = _db.Store();

        var page = store.Replace(PageIds.Home, PageStore.HomeSeed, out _);

        Assert.Equal("r1", page!.LayoutVersion);
    }

    [Theory]
    [InlineData("unknown type")]
    [InlineData("duplicate id")]
    [InlineData("empty id")]
    [InlineData("two latest")]
    public void AnInvalidListIsRefusedAndNothingIsWritten(string fault)
    {
        StoredSection[] sections = fault switch
        {
            "unknown type" => [new("hero", "spotlight")],
            "duplicate id" => [new("a", SectionTypes.NextUp), new("A", SectionTypes.Featured)],
            "empty id" => [new(" ", SectionTypes.NextUp)],
            _ => [new("a", SectionTypes.LatestLibraries), new("b", SectionTypes.LatestLibraries)],
        };
        var store = _db.Store();

        Assert.Null(store.Replace(PageIds.Home, sections, out var error));
        Assert.NotNull(error);
        Assert.Equal(PageStore.HomeSeed, store.Get(PageIds.Home)!.Sections);
        Assert.Equal(PageStore.HomeSeed, _db.Store().Get(PageIds.Home)!.Sections);
    }

    [Fact]
    public void ThereIsNoPageOtherThanHome()
    {
        var store = _db.Store();

        Assert.Null(store.Get("movies"));
        Assert.Null(store.Replace("movies", PageStore.HomeSeed, out _));
    }

    [Fact]
    public void TheAdminRoutesReadAndReplaceAPageAndRefuseWhatTheStoreRefuses()
    {
        var controller = new AdminPagesController(_db.Store());

        var read = controller.GetPage("home").Value!;
        Assert.Equal(("home", "r1"), (read.PageId, read.LayoutVersion));
        Assert.Equal(SectionTypes.All, read.SectionTypes);

        var put = controller.PutPage("home", new AdminPageRequest([new("nextup", SectionTypes.NextUp)])).Value!;
        Assert.Equal(("r2", "nextup"), (put.LayoutVersion, put.Sections.Single().Id));

        Assert.IsType<BadRequestObjectResult>(controller.PutPage("home", new AdminPageRequest([new("x", "spotlight")])).Result);
        Assert.IsType<NotFoundResult>(controller.GetPage("movies").Result);
        Assert.IsType<NotFoundResult>(controller.PutPage("movies", new AdminPageRequest([])).Result);
    }

    [Fact]
    public void TheDatabaseLivesUnderTheServersDataFolderNotThePluginsVersionedFolder()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(Path.Combine("config", "data"));

        Assert.Equal(Path.Combine("config", "data", "experience-manager", "experience-manager.db"), LayoutDatabase.DefaultPath(paths));
    }
}
