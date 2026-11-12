using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.ExperienceManager.Rules;

/// <summary>
/// What the rules need to know about a section.
/// </summary>
/// <param name="Type">Its section type.</param>
/// <param name="Feature">Whether it is shown as a feature.</param>
/// <param name="OptedOut">The rules it opted out of.</param>
public sealed record RuleTarget(string Type, bool Feature, IReadOnlyCollection<string> OptedOut);

/// <summary>
/// Applies the content rules to a section's items. It reads only what is loaded with the items and, for
/// Continue Watching clean-up, one batch of the user's data, so it adds no per-item lookups.
/// </summary>
public sealed class RuleFilter
{
    private readonly Func<ContentRules> _rules;
    private readonly Func<ContentRules> _draft;
    private readonly Func<int> _revision;
    private readonly ILocalizationManager _localization;
    private readonly IUserDataManager _userData;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuleFilter"/> class.
    /// </summary>
    /// <param name="rules">The published version, whose rules Home serves.</param>
    /// <param name="draft">The draft rules, which previews apply.</param>
    /// <param name="localization">Jellyfin's ratings.</param>
    /// <param name="userData">Jellyfin's user data.</param>
    public RuleFilter(Publishing.PublishStore rules, RulesStore draft, ILocalizationManager localization, IUserDataManager userData)
        : this(() => rules.Live.Rules, () => rules.Version, localization, userData, TimeProvider.System, () => draft.Rules)
    {
    }

    internal RuleFilter(RulesStore rules, ILocalizationManager localization, IUserDataManager userData, TimeProvider time)
        : this(() => rules.Rules, () => rules.Revision, localization, userData, time)
    {
    }

    internal RuleFilter(Func<ContentRules> rules, Func<int> revision, ILocalizationManager localization, IUserDataManager userData, TimeProvider time, Func<ContentRules>? draft = null)
    {
        _rules = rules;
        _draft = draft ?? rules;
        _revision = revision;
        _localization = localization;
        _userData = userData;
        _time = time;
    }

    /// <summary>
    /// Gets the rules' revision, which joins the layout version once they are changed.
    /// </summary>
    public int Revision => _revision();

    /// <summary>
    /// Applies the rules a section has not opted out of.
    /// </summary>
    /// <param name="target">The section.</param>
    /// <param name="user">The viewer.</param>
    /// <param name="audiences">The viewer's audiences, highest first, for the rating ceiling.</param>
    /// <param name="items">The section's items, in order.</param>
    /// <param name="notes">Collects what each rule left out, for the preview's why panel, when given.</param>
    /// <param name="draft">Whether to apply the draft rules, as previews do, rather than the published ones.</param>
    /// <returns>The items the rules let through, in order.</returns>
    public IReadOnlyList<BaseItem> Apply(RuleTarget target, User user, IReadOnlyList<string> audiences, IReadOnlyList<BaseItem> items, ICollection<string>? notes = null, bool draft = false)
    {
        var rules = draft ? _draft() : _rules();
        bool On(string rule, bool enabled) => enabled && !target.OptedOut.Contains(rule, StringComparer.OrdinalIgnoreCase);
        IEnumerable<BaseItem> result = items;
        var count = items.Count;

        // Only when explaining: count what a rule left out, so the request path stays lazy.
        void Noted(string rule)
        {
            if (notes is null)
            {
                return;
            }

            var kept = result.ToList();
            if (kept.Count < count)
            {
                notes.Add($"{rule} left out {count - kept.Count} {(count - kept.Count == 1 ? "item" : "items")}");
            }

            count = kept.Count;
            result = kept;
        }

        if (On(RuleIds.Tags, rules.ExcludeTags) && rules.ExcludedTags is { Count: > 0 } tags)
        {
            var excluded = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            result = result.Where(i => !(i.Tags ?? []).Any(excluded.Contains));
            Noted("Excluded tags");
        }

        if (target.Feature)
        {
            if (On(RuleIds.FeatureArt, rules.FeatureArt))
            {
                result = result.Where(i => i.HasImage(ImageType.Backdrop, 0) && (!rules.RequireLogo || i.HasImage(ImageType.Logo, 0)));
                Noted("Artwork for features");
            }

            if (On(RuleIds.Ceiling, rules.CeilingOn) && CeilingFor(rules, audiences) is { } ceiling && _localization.GetRatingScore(ceiling, null) is { } max)
            {
                result = result.Where(i => Score(i) is { } score && (score.Score < max.Score || (score.Score == max.Score && (score.SubScore ?? 0) <= (max.SubScore ?? 0))));
                Noted($"The {ceiling} ceiling");
            }
        }
        else if (On(RuleIds.Poster, rules.Poster) && target.Type != SectionTypes.MyMedia)
        {
            // Titles need a poster; episodes and libraries draw from other images.
            result = result.Where(i => i is not (Movie or Series or BoxSet) || i.HasImage(ImageType.Primary, 0));
            Noted("Require a poster");
        }

        if (On(RuleIds.Versions, rules.Versions))
        {
            result = OneOfEach(result);
            Noted("Collapse versions");
        }

        var list = result.ToList();
        if (target.Type == SectionTypes.ContinueWatching && On(RuleIds.ContinueCleanup, rules.ContinueCleanup) && list.Count > 0)
        {
            list = CleanUp(rules, user, list);
            if (notes is not null && list.Count < count)
            {
                notes.Add($"Continue Watching clean-up left out {count - list.Count} {(count - list.Count == 1 ? "item" : "items")}");
            }
        }

        return list;
    }

    // The first audience of the user's, in precedence order, with a ceiling sets it; else Everyone's.
    private static string? CeilingFor(ContentRules rules, IReadOnlyList<string> audiences)
    {
        foreach (var audience in audiences)
        {
            if (rules.AudienceCeilings?.FirstOrDefault(p => string.Equals(p.Key, audience, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } ceiling)
            {
                return ceiling;
            }
        }

        return rules.EveryoneCeiling is { Length: > 0 } everyone ? everyone : null;
    }

    private ParentalRatingScore? Score(BaseItem item)
    {
        var rating = item.OfficialRating;
        if (string.IsNullOrEmpty(rating) && item is Episode episode)
        {
            rating = episode.Series?.OfficialRating;
        }

        return string.IsNullOrEmpty(rating) ? null : _localization.GetRatingScore(rating, null);
    }

    private static IEnumerable<BaseItem> OneOfEach(IEnumerable<BaseItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is Movie && Key(item) is { } key && !seen.Add(key))
            {
                continue;
            }

            yield return item;
        }
    }

    private static string? Key(BaseItem item)
    {
        foreach (var provider in new[] { "Tmdb", "Imdb" })
        {
            var id = item.ProviderIds?.FirstOrDefault(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrEmpty(id))
            {
                return provider + ":" + id;
            }
        }

        return null;
    }

    private List<BaseItem> CleanUp(ContentRules rules, User user, List<BaseItem> items)
    {
        var data = _userData.GetUserDataBatch(items, user);
        var oldest = rules.CleanupDays is { } days ? _time.GetUtcNow().UtcDateTime.AddDays(-days) : (DateTime?)null;
        return items.Where(i =>
        {
            var d = data.GetValueOrDefault(i.Id);
            if (d is null)
            {
                return true;
            }

            if (oldest is { } cut && d.LastPlayedDate is { } played && played < cut)
            {
                return false;
            }

            return rules.CleanupPercent is not { } percent || i.RunTimeTicks is not > 0 || d.PlaybackPositionTicks * 100.0 / i.RunTimeTicks.Value <= percent;
        }).ToList();
    }
}
