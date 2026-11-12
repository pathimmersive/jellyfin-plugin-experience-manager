// The admin page's Content rules page: rules set once for every section on Home, each with how many sections
// opted out. admin.html loads it from /ExperienceManager/admin-rules.js; every route it calls is for
// administrators only. The pure helpers at the top are also loaded by the tests under node.
(function () {
    'use strict';

    var RULES = [
        { id: 'tags', name: 'Exclude tags', about: 'Items with any of these tags are never shown on Home.' },
        { id: 'ceiling', name: 'Rating ceiling for featuring', about: 'Feature sections show nothing rated above the ceiling, and nothing unrated while one is set. Rows are not limited.' },
        { id: 'feature-art', name: 'Artwork for features', about: 'Feature sections show only items with a backdrop, and a logo if you ask for one.' },
        { id: 'poster', name: 'Require a poster', about: 'Rows show films and shows only when they have a poster.' },
        { id: 'versions', name: 'Collapse versions', about: 'Films that share a TMDb or IMDb id show once.' },
        { id: 'continue-cleanup', name: 'Continue Watching clean-up', about: 'Continue Watching leaves out what was last played long ago, or played nearly to the end.' }
    ];

    function optOutLine(count) {
        if (!count) return 'Every section follows it.';
        return count === 1 ? '1 section opted out.' : count + ' sections opted out.';
    }

    // The rules as the server takes them, from the form's values.
    function toRules(form) {
        var number = function (v) { var n = parseInt(v, 10); return isNaN(n) ? null : n; };
        var ceilings = {};
        Object.keys(form.audienceCeilings || {}).forEach(function (id) { if (form.audienceCeilings[id]) ceilings[id] = form.audienceCeilings[id]; });
        return {
            ExcludeTags: !!form.excludeTags,
            ExcludedTags: form.excludedTags || [],
            CeilingOn: !!form.ceilingOn,
            EveryoneCeiling: form.everyoneCeiling || null,
            AudienceCeilings: ceilings,
            FeatureArt: !!form.featureArt,
            RequireLogo: !!form.requireLogo,
            Poster: !!form.poster,
            Versions: !!form.versions,
            ContinueCleanup: !!form.continueCleanup,
            CleanupDays: number(form.cleanupDays),
            CleanupPercent: number(form.cleanupPercent)
        };
    }

    var pure = { RULES: RULES, optOutLine: optOutLine, toRules: toRules };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = pure;
        return;
    }

    // ---- The page ----

    var state = { rules: null, optOuts: {}, ratings: [], audiences: [], tags: [], form: null, error: '', message: '' };
    var root = null;

    function el(tag, text, className) {
        var node = document.createElement(tag);
        if (text != null) node.textContent = text;
        if (className) node.className = className;
        return node;
    }

    function button(text, className, onClick) {
        var b = el('button', text, 'raised button-alt em-prog-button' + (className ? ' ' + className : ''));
        b.type = 'button';
        b.addEventListener('click', onClick);
        return b;
    }

    function call(method, path, body) {
        var headers = { Authorization: 'MediaBrowser Token="' + ApiClient.accessToken() + '"' };
        if (body !== undefined) headers['Content-Type'] = 'application/json';
        return fetch(ApiClient.getUrl(path), { method: method, headers: headers, body: body === undefined ? undefined : JSON.stringify(body) })
            .then(function (r) {
                return r.text().then(function (text) {
                    var parsed = null;
                    try { parsed = text ? JSON.parse(text) : null; } catch (e) { parsed = null; }
                    return { ok: r.ok, status: r.status, body: parsed };
                });
            });
    }

    function fromRules(rules) {
        return {
            excludeTags: rules.ExcludeTags, excludedTags: (rules.ExcludedTags || []).slice(),
            ceilingOn: rules.CeilingOn, everyoneCeiling: rules.EveryoneCeiling || '', audienceCeilings: JSON.parse(JSON.stringify(rules.AudienceCeilings || {})),
            featureArt: rules.FeatureArt, requireLogo: rules.RequireLogo, poster: rules.Poster, versions: rules.Versions,
            continueCleanup: rules.ContinueCleanup, cleanupDays: rules.CleanupDays == null ? '' : String(rules.CleanupDays),
            cleanupPercent: rules.CleanupPercent == null ? '' : String(rules.CleanupPercent)
        };
    }

    function took(body) {
        state.rules = body.Rules;
        state.optOuts = body.OptOuts;
        state.ratings = body.Ratings;
        state.form = fromRules(body.Rules);
    }

    function check(parent, label, key, name) {
        var wrap = parent.appendChild(el('label', null, 'em-prog-switch'));
        var box = wrap.appendChild(el('input'));
        box.type = 'checkbox';
        box.name = name;
        box.checked = !!state.form[key];
        box.addEventListener('change', function () { state.form[key] = box.checked; });
        wrap.appendChild(el('span', label));
        return box;
    }

    function ratingSelect(name, value, onChange) {
        var node = el('select');
        node.name = name;
        [''].concat(state.ratings).forEach(function (r) {
            var option = node.appendChild(el('option', r || 'No ceiling'));
            option.value = r;
            option.selected = r === (value || '');
        });
        node.addEventListener('change', function () { onChange(node.value); });
        return node;
    }

    function field(parent, labelText, input) {
        var label = parent.appendChild(el('label', null, 'em-prog-field'));
        label.appendChild(el('span', labelText));
        label.appendChild(input);
        return input;
    }

    function draw() {
        if (!root) return;
        root.textContent = '';
        root.appendChild(el('p', 'Rules set once apply to every section on Home. A section can opt out of a rule in its settings on Home layout.'));
        if (state.error) root.appendChild(el('p', state.error, 'em-aud-error')).setAttribute('role', 'alert');
        if (state.message) root.appendChild(el('p', state.message, 'em-muted em-rules-message'));
        var f = state.form;
        RULES.forEach(function (rule) {
            var box = root.appendChild(el('section', null, 'em-rule'));
            box.setAttribute('data-rule', rule.id);
            box.appendChild(el('h3', rule.name));
            box.appendChild(el('p', rule.about, 'em-muted'));
            if (rule.id === 'tags') {
                check(box, 'On', 'excludeTags', 'rule-tags');
                var tags = field(box, 'Tags', el('select'));
                tags.name = 'excluded-tags';
                tags.multiple = true;
                state.tags.concat(f.excludedTags.filter(function (t) { return state.tags.indexOf(t) < 0; })).forEach(function (t) {
                    var option = tags.appendChild(el('option', t));
                    option.value = t;
                    option.selected = f.excludedTags.indexOf(t) >= 0;
                });
                tags.addEventListener('change', function () {
                    f.excludedTags = Array.prototype.filter.call(tags.options, function (o) { return o.selected; }).map(function (o) { return o.value; });
                });
                if (!state.tags.length) box.appendChild(el('p', 'No item on this server has a tag yet.', 'em-muted'));
            } else if (rule.id === 'ceiling') {
                check(box, 'On', 'ceilingOn', 'rule-ceiling');
                field(box, 'Everyone', ratingSelect('ceiling-everyone', f.everyoneCeiling, function (v) { f.everyoneCeiling = v; }));
                state.audiences.forEach(function (a) {
                    field(box, a.Name, ratingSelect('ceiling-' + a.Id, f.audienceCeilings[a.Id], function (v) { f.audienceCeilings[a.Id] = v; }));
                });
                box.appendChild(el('p', 'A user\'s first audience in the Audiences order with a ceiling sets it; otherwise Everyone\'s does.', 'em-muted'));
            } else if (rule.id === 'feature-art') {
                check(box, 'On: a backdrop', 'featureArt', 'rule-feature-art');
                check(box, 'Also a logo', 'requireLogo', 'rule-logo');
            } else if (rule.id === 'poster') {
                check(box, 'On', 'poster', 'rule-poster');
            } else if (rule.id === 'versions') {
                check(box, 'On', 'versions', 'rule-versions');
            } else if (rule.id === 'continue-cleanup') {
                check(box, 'On', 'continueCleanup', 'rule-continue-cleanup');
                var days = field(box, 'Last played more than (days)', el('input'));
                days.type = 'number';
                days.min = 1;
                days.name = 'cleanup-days';
                days.value = f.cleanupDays;
                days.addEventListener('change', function () { f.cleanupDays = days.value; });
                var percent = field(box, 'Played past (%)', el('input'));
                percent.type = 'number';
                percent.min = 1;
                percent.max = 100;
                percent.name = 'cleanup-percent';
                percent.value = f.cleanupPercent;
                percent.addEventListener('change', function () { f.cleanupPercent = percent.value; });
            }
            box.appendChild(el('p', optOutLine(state.optOuts[rule.id] || 0), 'em-rule-optouts'));
        });
        root.appendChild(button('Save', 'em-rules-save', function () {
            call('PUT', 'ExperienceManager/Admin/Rules', toRules(state.form)).then(function (r) {
                if (!r.ok) {
                    state.error = (r.body && r.body.Error) || 'The rules could not be saved (' + r.status + ').';
                } else {
                    took(r.body);
                    state.error = '';
                    state.message = 'Saved. Home follows the new rules now.';
                }
                draw();
            });
        }));
    }

    window.ExperienceManagerRules = {
        draw: function (panel) {
            root = panel.appendChild(el('div', null, 'em-rules'));
            state.message = '';
            state.error = '';
            var status = root.appendChild(el('p', 'Loading…', 'em-muted'));
            Promise.all([
                call('GET', 'ExperienceManager/Admin/Rules'),
                call('GET', 'ExperienceManager/Admin/Audiences'),
                call('GET', 'Items/Filters2?Recursive=true&UserId=' + ApiClient.getCurrentUserId())
            ]).then(function (r) {
                if (!r[0].ok) throw new Error('rules');
                took(r[0].body);
                state.audiences = r[1].ok ? r[1].body.Audiences : [];
                state.tags = r[2].ok ? (r[2].body.Tags || []) : [];
                status.remove();
                draw();
            }).then(null, function () {
                status.textContent = 'The rules could not be loaded. Reload the page to try again.';
            });
        }
    };
})();
