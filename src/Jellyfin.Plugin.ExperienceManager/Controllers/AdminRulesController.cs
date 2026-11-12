using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Rules;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExperienceManager.Controllers;

/// <summary>
/// The content rules as the admin sees them.
/// </summary>
/// <param name="Rules">The rules.</param>
/// <param name="Revision">Counts their edits; 1 until first changed.</param>
/// <param name="OptOuts">How many sections opted out of each rule, in Everyone's layout and the audiences' own sections.</param>
/// <param name="Ratings">The ratings Jellyfin knows, for the ceilings, lowest first.</param>
public sealed record AdminRulesResponse(ContentRules Rules, int Revision, IReadOnlyDictionary<string, int> OptOuts, IReadOnlyList<string> Ratings);

/// <summary>
/// Reads and sets the content rules. Administrators only; not part of the published home API.
/// </summary>
[ApiController]
[Route(AdminController.Route + "/Rules")]
[Authorize(Policy = "RequiresElevation")]
public class AdminRulesController : ControllerBase
{
    private readonly RulesStore _rules;
    private readonly PageStore _pages;
    private readonly AudienceStore _audiences;
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminRulesController"/> class.
    /// </summary>
    /// <param name="rules">The rules.</param>
    /// <param name="pages">The stored pages, for the opt-out counts.</param>
    /// <param name="audiences">The audiences, for their ceilings and own sections.</param>
    /// <param name="localization">Jellyfin's ratings.</param>
    public AdminRulesController(RulesStore rules, PageStore pages, AudienceStore audiences, ILocalizationManager localization)
    {
        _rules = rules;
        _pages = pages;
        _audiences = audiences;
        _localization = localization;
    }

    /// <summary>
    /// Gets the content rules, how many sections opted out of each, and the ratings for the ceilings.
    /// </summary>
    /// <returns>The rules.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AdminRulesResponse> GetRules() => Describe();

    /// <summary>
    /// Sets the content rules. Clients see the change with a new layout version.
    /// </summary>
    /// <param name="rules">The rules.</param>
    /// <returns>The rules as stored.</returns>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<AdminRulesResponse> SetRules([FromBody] ContentRules rules)
    {
        var error = rules.Validate(r => _localization.GetRatingScore(r, null) is not null)
            ?? rules.AudienceCeilings?.Keys.Where(id => _audiences.Get(id) is null).Select(id => $"there is no audience '{id}'").FirstOrDefault();
        if (error is not null)
        {
            return BadRequest(new { Error = error });
        }

        _rules.Set(rules);
        return Describe();
    }

    private AdminRulesResponse Describe()
    {
        var sections = (_pages.Get(PageIds.Home)?.Sections ?? [])
            .Concat(_audiences.All.Select(a => _audiences.Layout(a.Id, PageIds.Home)).OfType<StoredGroupLayout>()
                .SelectMany(l => l.Entries.Where(e => e.Section is not null && e.Mode is GroupModes.Changed or GroupModes.Added).Select(e => e.Section!)))
            .ToList();
        var counts = RuleIds.All.ToDictionary(r => r, r => sections.Count(s => new SectionConfig(s.Config).Strings("RulesOff").Contains(r, StringComparer.OrdinalIgnoreCase)));
        var ratings = _localization.GetParentalRatings()
            .Select(r => (r.Name, Score: _localization.GetRatingScore(r.Name, null)))
            .Where(r => r.Score is not null && !string.IsNullOrEmpty(r.Name))
            .OrderBy(r => r.Score!.Score).ThenBy(r => r.Score!.SubScore ?? 0)
            .Select(r => r.Name!)
            .Distinct()
            .ToList();
        return new AdminRulesResponse(_rules.Rules, _rules.Revision, counts, ratings);
    }
}
