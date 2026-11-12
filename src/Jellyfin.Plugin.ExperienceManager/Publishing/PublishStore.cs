using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using Jellyfin.Plugin.ExperienceManager.Rules;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.ExperienceManager.Publishing;

/// <summary>
/// One audience's Home layout in a published version.
/// </summary>
/// <param name="AudienceId">The audience.</param>
/// <param name="Entries">Its entries, in its order.</param>
public sealed record PublishedGroupLayout(string AudienceId, IReadOnlyList<GroupEntry> Entries);

/// <summary>
/// Everything a version publishes: Everyone's Home, the audiences' Home layouts, the events and the content rules.
/// </summary>
/// <param name="Sections">Everyone's Home sections, in order.</param>
/// <param name="GroupLayouts">The audiences' own Home layouts.</param>
/// <param name="Events">The events, highest first.</param>
/// <param name="Rules">The content rules.</param>
public sealed record PublishedState(
    IReadOnlyList<StoredSection> Sections,
    IReadOnlyList<PublishedGroupLayout> GroupLayouts,
    IReadOnlyList<ProgrammedEvent> Events,
    ContentRules Rules);

/// <summary>
/// A published version, as History lists it.
/// </summary>
/// <param name="Id">Its number, counting up.</param>
/// <param name="Author">Who published it.</param>
/// <param name="At">When.</param>
/// <param name="Summary">What changed.</param>
public sealed record PublishedVersion(int Id, string Author, DateTimeOffset At, string Summary);

/// <summary>
/// A line History lists that nobody published, such as an event starting.
/// </summary>
/// <param name="At">When it happened.</param>
/// <param name="Text">What happened.</param>
public sealed record HistoryEntry(DateTimeOffset At, string Text);

/// <summary>
/// Drafts and published versions. The stores hold the draft, which every admin edit changes; Home serves the last
/// published version until the next Publish. Restore, Discard and Undo write an earlier state back into the stores.
/// </summary>
public sealed class PublishStore
{
    /// <summary>How many published versions History keeps.</summary>
    public const int Keep = 100;

    /// <summary>
    /// Published versions and History's other lines.
    /// </summary>
    public static readonly Migration Migration = new(6, """
        CREATE TABLE versions (
            id INTEGER PRIMARY KEY,
            author TEXT NOT NULL,
            at TEXT NOT NULL,
            summary TEXT NOT NULL,
            state TEXT NOT NULL
        );
        CREATE TABLE history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            at TEXT NOT NULL,
            text TEXT NOT NULL
        );
        """);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly LayoutDatabase _database;
    private readonly PageStore _pages;
    private readonly AudienceStore _audiences;
    private readonly EventStore _events;
    private readonly RulesStore _rules;
    private readonly TimeProvider _time;
    private readonly object _write = new();
    private readonly Stack<string> _undo = new();
    private volatile Release _published;
    private string _working;
    private bool _restoring;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublishStore"/> class. On a server with no version yet, what the
    /// stores hold becomes version 1, so Home goes on serving what it served.
    /// </summary>
    /// <param name="database">The plugin's database.</param>
    /// <param name="pages">The pages.</param>
    /// <param name="audiences">The audiences and their layouts.</param>
    /// <param name="events">The events.</param>
    /// <param name="rules">The content rules.</param>
    public PublishStore(LayoutDatabase database, PageStore pages, AudienceStore audiences, EventStore events, RulesStore rules)
        : this(database, pages, audiences, events, rules, TimeProvider.System)
    {
    }

    internal PublishStore(LayoutDatabase database, PageStore pages, AudienceStore audiences, EventStore events, RulesStore rules, TimeProvider time)
    {
        _database = database;
        _pages = pages;
        _audiences = audiences;
        _events = events;
        _rules = rules;
        _time = time;
        _published = Load() ?? Insert("Experience Manager", "The layout as it was before publishing began.", Working());
        _working = JsonSerializer.Serialize(Working(), Json);
        _pages.Changed += OnDraftChanged;
        _audiences.Changed += OnDraftChanged;
        _events.Changed += OnDraftChanged;
        _rules.Changed += OnDraftChanged;
    }

    /// <summary>
    /// Raised after a publish, so what Home serves changes.
    /// </summary>
    public event EventHandler? Published;

    /// <summary>
    /// Gets what Home serves: the last published version's state.
    /// </summary>
    public PublishedState Live => _published.State;

    /// <summary>
    /// Gets the last published version's number.
    /// </summary>
    public int Version => _published.Version.Id;

    /// <summary>
    /// Gets a key for the published layouts and rules, the same for the same content: publishing them back as they
    /// were gives clients the same layout version and ETag as before. Events count only while they are on.
    /// </summary>
    public string ContentKey => _published.Key;

    /// <summary>
    /// Gets a value indicating whether the draft differs from what is published.
    /// </summary>
    public bool HasDraft => JsonSerializer.Serialize(Working(), Json) != JsonSerializer.Serialize(Live, Json);

    /// <summary>
    /// Gets a value indicating whether Undo has a change to step back.
    /// </summary>
    public bool CanUndo
    {
        get
        {
            lock (_write)
            {
                return _undo.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the published layout of an audience, or null when it has none.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <returns>The layout.</returns>
    public StoredGroupLayout? LiveLayout(string audienceId) =>
        Live.GroupLayouts.FirstOrDefault(l => string.Equals(l.AudienceId, audienceId, StringComparison.OrdinalIgnoreCase)) is { } layout
            ? new StoredGroupLayout(layout.AudienceId, PageIds.Home, Version, layout.Entries)
            : null;

    /// <summary>
    /// Gets the published Home page.
    /// </summary>
    /// <param name="pageId">The page id; only Home is published.</param>
    /// <returns>The page, or null.</returns>
    public StoredPage? LivePage(string pageId) =>
        string.Equals(pageId, PageIds.Home, StringComparison.OrdinalIgnoreCase) ? new StoredPage(PageIds.Home, Version, Live.Sections) : null;

    /// <summary>
    /// Gets the draft's changes against what is published, one line each.
    /// </summary>
    /// <returns>The lines, none when there is no draft.</returns>
    public IReadOnlyList<string> Changes() => Describe(Live, Working(), _audiences);

    /// <summary>
    /// Publishes the draft: Home serves it from now on, and History keeps it.
    /// </summary>
    /// <param name="author">Who publishes.</param>
    /// <returns>The new version, or null when there was nothing to publish.</returns>
    public PublishedVersion? Publish(string author)
    {
        lock (_write)
        {
            var working = Working();
            var lines = Describe(Live, working, _audiences);
            if (lines.Count == 0)
            {
                return null;
            }

            _published = Insert(author, string.Join("; ", lines), working);
            _undo.Clear();
        }

        Published?.Invoke(this, EventArgs.Empty);
        return _published.Version;
    }

    /// <summary>
    /// Makes a published version the draft, to publish again.
    /// </summary>
    /// <param name="versionId">The version.</param>
    /// <returns>Whether there is such a version.</returns>
    public bool Restore(int versionId)
    {
        var state = StateOf(versionId);
        if (state is null)
        {
            return false;
        }

        WriteDraft(state, keepUndo: true);
        return true;
    }

    /// <summary>
    /// Drops the draft, so the stores hold what is published again.
    /// </summary>
    public void Discard()
    {
        WriteDraft(Live, keepUndo: false);
        lock (_write)
        {
            _undo.Clear();
        }
    }

    /// <summary>
    /// Steps the draft back one change.
    /// </summary>
    /// <returns>Whether there was a change to step back.</returns>
    public bool Undo()
    {
        string previous;
        lock (_write)
        {
            if (_undo.Count == 0)
            {
                return false;
            }

            previous = _undo.Pop();
        }

        WriteDraft(JsonSerializer.Deserialize<PublishedState>(previous, Json)!, keepUndo: false);
        return true;
    }

    /// <summary>
    /// Gets the published versions, newest first.
    /// </summary>
    /// <returns>The versions.</returns>
    public IReadOnlyList<PublishedVersion> Versions()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, author, at, summary FROM versions ORDER BY id DESC;";
        var versions = new List<PublishedVersion>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            versions.Add(new PublishedVersion(reader.GetInt32(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetString(3)));
        }

        return versions;
    }

    /// <summary>
    /// Gets History's other lines, newest first.
    /// </summary>
    /// <param name="limit">How many.</param>
    /// <returns>The lines.</returns>
    public IReadOnlyList<HistoryEntry> Entries(int limit = 200)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT at, text FROM history ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new HistoryEntry(DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetString(1)));
        }

        return entries;
    }

    /// <summary>
    /// Adds a line to History.
    /// </summary>
    /// <param name="at">When it happened.</param>
    /// <param name="text">What happened.</param>
    public void AddEntry(DateTimeOffset at, string text)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO history (at, text) VALUES ($at, $text);";
        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }

    internal static IReadOnlyList<string> Describe(PublishedState live, PublishedState draft, AudienceStore audiences)
    {
        var lines = new List<string>();
        var sections = Count(live.Sections, draft.Sections);
        if (sections > 0)
        {
            lines.Add(sections == 1 ? "1 change in Everyone's Home" : $"{sections} changes in Everyone's Home");
        }

        var groups = live.GroupLayouts.Select(g => g.AudienceId).Union(draft.GroupLayouts.Select(g => g.AudienceId), StringComparer.OrdinalIgnoreCase);
        foreach (var id in groups)
        {
            var before = live.GroupLayouts.FirstOrDefault(g => string.Equals(g.AudienceId, id, StringComparison.OrdinalIgnoreCase));
            var after = draft.GroupLayouts.FirstOrDefault(g => string.Equals(g.AudienceId, id, StringComparison.OrdinalIgnoreCase));
            if (JsonSerializer.Serialize(before?.Entries, Json) != JsonSerializer.Serialize(after?.Entries, Json))
            {
                lines.Add($"{audiences.Get(id)?.Name ?? id}'s Home changed");
            }
        }

        var oldEvents = live.Events.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var newEvents = draft.Events.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var e in draft.Events)
        {
            if (!oldEvents.TryGetValue(e.Id, out var was))
            {
                lines.Add($"New event: {e.Name}");
            }
            else if (JsonSerializer.Serialize(was, Json) != JsonSerializer.Serialize(e, Json))
            {
                lines.Add(was.Published != e.Published && JsonSerializer.Serialize(was with { Published = e.Published }, Json) == JsonSerializer.Serialize(e, Json)
                    ? $"{(e.Published ? "Published" : "Unpublished")} event: {e.Name}"
                    : $"Event changed: {e.Name}");
            }
        }

        lines.AddRange(live.Events.Where(e => !newEvents.ContainsKey(e.Id)).Select(e => $"Event removed: {e.Name}"));
        if (live.Events.Count == draft.Events.Count && live.Events.Select(e => e.Id).SequenceEqual(draft.Events.Select(e => e.Id), StringComparer.OrdinalIgnoreCase) is false
            && live.Events.Select(e => e.Id).Order().SequenceEqual(draft.Events.Select(e => e.Id).Order()))
        {
            lines.Add("Events reordered");
        }

        if (JsonSerializer.Serialize(live.Rules, Json) != JsonSerializer.Serialize(draft.Rules, Json))
        {
            lines.Add("Content rules changed");
        }

        return lines;
    }

    // Sections added, removed or changed, each counted once.
    private static int Count(IReadOnlyList<StoredSection> before, IReadOnlyList<StoredSection> after)
    {
        var was = before.ToDictionary(s => s.Id, s => JsonSerializer.Serialize(s, Json), StringComparer.OrdinalIgnoreCase);
        var now = after.ToDictionary(s => s.Id, s => JsonSerializer.Serialize(s, Json), StringComparer.OrdinalIgnoreCase);
        var changed = now.Count(p => !was.TryGetValue(p.Key, out var old) || old != p.Value) + was.Keys.Count(k => !now.ContainsKey(k));
        var kept = after.Where(s => was.ContainsKey(s.Id)).Select(s => s.Id).ToList();
        var keptBefore = before.Where(s => now.ContainsKey(s.Id)).Select(s => s.Id).ToList();
        return changed + (kept.SequenceEqual(keptBefore, StringComparer.OrdinalIgnoreCase) ? 0 : 1);
    }

    private PublishedState Working() => new(
        _pages.Get(PageIds.Home)?.Sections ?? [],
        _audiences.All
            .Select(a => _audiences.Layout(a.Id, PageIds.Home))
            .OfType<StoredGroupLayout>()
            .Select(l => new PublishedGroupLayout(l.AudienceId, l.Entries))
            .ToList(),
        _events.All,
        _rules.Rules);

    // Each edit to the draft keeps the state before it, so Undo can step back.
    private void OnDraftChanged(object? sender, EventArgs e)
    {
        lock (_write)
        {
            if (_restoring)
            {
                return;
            }

            var now = JsonSerializer.Serialize(Working(), Json);
            if (now != _working)
            {
                _undo.Push(_working);
                _working = now;
            }
        }
    }

    private void WriteDraft(PublishedState state, bool keepUndo)
    {
        lock (_write)
        {
            var before = _working;
            _restoring = true;
            try
            {
                _pages.Replace(PageIds.Home, state.Sections, out _);
                foreach (var audience in _audiences.All)
                {
                    var layout = state.GroupLayouts.FirstOrDefault(g => string.Equals(g.AudienceId, audience.Id, StringComparison.OrdinalIgnoreCase));
                    if (layout is null)
                    {
                        _audiences.ClearLayout(audience.Id, PageIds.Home);
                    }
                    else
                    {
                        _audiences.SetLayout(audience.Id, PageIds.Home, state.Sections, layout.Entries, out _);
                    }
                }

                _events.ReplaceAll(state.Events);
                _rules.Set(state.Rules);
            }
            finally
            {
                _restoring = false;
            }

            _working = JsonSerializer.Serialize(Working(), Json);
            if (keepUndo && _working != before)
            {
                _undo.Push(before);
            }
        }
    }

    private PublishedState? StateOf(int versionId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM versions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", versionId);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PublishedState>(json, Json) : null;
    }

    private Release? Load()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, author, at, summary, state FROM versions ORDER BY id DESC LIMIT 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new Release(
            new PublishedVersion(reader.GetInt32(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetString(3)),
            JsonSerializer.Deserialize<PublishedState>(reader.GetString(4), Json)!);
    }

    private Release Insert(string author, string summary, PublishedState state)
    {
        var at = _time.GetUtcNow();
        using (var connection = _database.Open())
        {
            using var transaction = connection.BeginTransaction();
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO versions (author, at, summary, state) VALUES ($author, $at, $summary, $state);";
                insert.Parameters.AddWithValue("$author", author);
                insert.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$summary", summary);
                insert.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, Json));
                insert.ExecuteNonQuery();
            }

            using (var trim = connection.CreateCommand())
            {
                trim.Transaction = transaction;
                trim.CommandText = $"DELETE FROM versions WHERE id NOT IN (SELECT id FROM versions ORDER BY id DESC LIMIT {Keep});";
                trim.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Load()!;
    }

    private sealed record Release(PublishedVersion Version, PublishedState State)
    {
        // Events join the layout version through the set on now, so an event that is not on changes nothing here.
        public string Key { get; } = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { State.Sections, State.GroupLayouts, State.Rules }, Json)))[..8];
    }
}
