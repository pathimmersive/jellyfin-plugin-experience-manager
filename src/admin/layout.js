// The admin page's Home layout editor: Everyone's sections, or a group's with what it inherits, edited as a
// working copy and saved in one request. admin.html loads it from /ExperienceManager/admin-layout.js; every
// route it calls is for administrators only. The pure helpers at the top are also loaded by the tests under node.
(function () {
    'use strict';

    var TYPES = [
        { type: 'featured', name: 'Featured', about: 'The banner at the top: new films with their artwork.' },
        { type: 'mymedia', name: 'My Media', about: 'The libraries the user can see.' },
        { type: 'continue', name: 'Continue Watching', about: 'What the user started and has not finished.' },
        { type: 'nextup', name: 'Next Up', about: 'The next episode of each show the user is watching.' },
        { type: 'latest-libraries', name: 'Latest for each library', about: 'The newest titles, one row for each film and show library.' },
        { type: 'latest', name: 'Latest in one library', about: 'The newest titles in a library you choose.' },
        { type: 'query', name: 'Titles by genre or tag', about: 'Films and shows with the genres or tags you choose.' },
        { type: 'collection', name: 'A collection', about: 'A collection or playlist, in its own order.' }
    ];

    var MARKS = { inherited: 'Inherited', changed: 'Changed in ', added: 'Only in ', removed: 'Removed in ' };

    // The content rules a section can opt out of, as Rules > Content rules names them.
    var RULES = [
        { id: 'tags', name: 'Exclude tags' }, { id: 'ceiling', name: 'Rating ceiling for featuring' }, { id: 'feature-art', name: 'Artwork for features' },
        { id: 'poster', name: 'Require a poster' }, { id: 'versions', name: 'Collapse versions' }, { id: 'continue-cleanup', name: 'Continue Watching clean-up' }
    ];

    // A section's opt-outs after the admin ticks or unticks a rule it follows.
    function rulesOff(current, rule, follows) {
        var list = (current || []).filter(function (r) { return r !== rule; });
        if (!follows) list.push(rule);
        return list;
    }

    function typeOf(type) {
        for (var i = 0; i < TYPES.length; i++) if (TYPES[i].type === type) return TYPES[i];
        return { type: type, name: type, about: '' };
    }

    function titleOf(section) {
        return section.Title || typeOf(section.Type).name;
    }

    function move(list, from, to) {
        var copy = list.slice();
        var item = copy.splice(from, 1)[0];
        copy.splice(to, 0, item);
        return copy;
    }

    // What a screen reader hears as a section is picked up, moved, put down or put back.
    function announce(title, index, count, verb) {
        var where = 'position ' + (index + 1) + ' of ' + count;
        if (verb === 'picked') return title + ' picked up, ' + where + '. The arrow keys move it, Space puts it down, Escape puts it back.';
        if (verb === 'dropped') return title + ' put down, ' + where + '.';
        if (verb === 'cancelled') return title + ' put back, ' + where + '.';
        return title + ', ' + where;
    }

    // The keyboard: Space or Enter picks a section up and puts it down, the arrows move it, Escape puts it back.
    function keyAction(key, picked) {
        if (key === ' ' || key === 'Spacebar' || key === 'Enter') return picked ? 'drop' : 'pick';
        if (!picked) return null;
        if (key === 'ArrowUp' || key === 'Up') return 'up';
        if (key === 'ArrowDown' || key === 'Down') return 'down';
        if (key === 'Escape' || key === 'Esc') return 'cancel';
        return null;
    }

    function copy(value) {
        return JSON.parse(JSON.stringify(value));
    }

    // A group's entries as the server stores them: every row in order, with the group's own sections.
    function entries(rows) {
        return rows.map(function (r) {
            var entry = { Id: r.section.Id, Mode: r.mark };
            if (r.mark === 'changed' || r.mark === 'added') entry.Section = copy(r.section);
            return entry;
        });
    }

    // Editing an inherited section makes it the group's own.
    function makeOwn(row) {
        if (row.mark === 'inherited') row.mark = 'changed';
        return row;
    }

    function useEveryones(row, everyone) {
        row.mark = 'inherited';
        row.section = copy(everyone);
        return row;
    }

    // Removing: a group keeps a server section listed as removed so it can be restored; anything else goes.
    function remove(rows, index, group) {
        var row = rows[index];
        if (group && row.mark !== 'added') {
            row.mark = 'removed';
            return rows;
        }
        return rows.slice(0, index).concat(rows.slice(index + 1));
    }

    function uniqueId(base, rows) {
        var taken = {};
        rows.forEach(function (r) { taken[r.section.Id.toLowerCase()] = true; });
        var id = base;
        for (var n = 2; taken[id]; n++) id = base + '-' + n;
        return id;
    }

    function slug(text) {
        return String(text || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'section';
    }

    function newSection(type, rows) {
        var base = type === 'query' ? 'genre-row' : type === 'collection' ? 'collection-row' : type === 'latest-libraries' ? 'latest' : type;
        var section = { Id: uniqueId(base, rows), Type: type };
        if (type === 'query') section.Config = { Genres: [], ItemTypes: 'Movie,Series' };
        return section;
    }

    function hasLatestLibraries(rows) {
        return rows.some(function (r) { return r.section.Type === 'latest-libraries' && r.mark !== 'removed'; });
    }

    // The unsaved changes, counted against what is stored, for the banner.
    function changes(original, rows) {
        var before = {};
        original.forEach(function (r) { before[r.section.Id] = r; });
        var after = {};
        rows.forEach(function (r) { after[r.section.Id] = r; });
        var counts = { moved: 0, added: 0, removed: 0, edited: 0 };
        rows.forEach(function (r) {
            var was = before[r.section.Id];
            if (!was) counts.added++;
            else if (r.mark === 'removed' && was.mark !== 'removed') counts.removed++;
            else if (JSON.stringify(r) !== JSON.stringify(was)) counts.edited++;
        });
        original.forEach(function (r) { if (!after[r.section.Id]) counts.removed++; });
        var keptBefore = original.map(function (r) { return r.section.Id; }).filter(function (id) { return after[id]; });
        var keptAfter = rows.map(function (r) { return r.section.Id; }).filter(function (id) { return before[id]; });
        // The sections moved: all but the longest run still in their old order.
        var positions = keptAfter.map(function (id) { return keptBefore.indexOf(id); });
        var longest = [];
        positions.forEach(function (p) {
            var i = 0;
            while (i < longest.length && longest[i] < p) i++;
            longest[i] = p;
        });
        counts.moved = positions.length - longest.length;
        var parts = [];
        ['moved', 'added', 'removed', 'edited'].forEach(function (k) { if (counts[k]) parts.push(counts[k] + ' ' + k); });
        counts.total = counts.moved + counts.added + counts.removed + counts.edited;
        counts.text = counts.total ? counts.total + (counts.total === 1 ? ' unsaved change: ' : ' unsaved changes: ') + parts.join(', ') : '';
        return counts;
    }

    var pure = {
        TYPES: TYPES, typeOf: typeOf, titleOf: titleOf, move: move, announce: announce, keyAction: keyAction, entries: entries,
        makeOwn: makeOwn, useEveryones: useEveryones, remove: remove, newSection: newSection, hasLatestLibraries: hasLatestLibraries,
        changes: changes, slug: slug, RULES: RULES, rulesOff: rulesOff
    };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = pure;
        return;
    }

    // ---- The page ----

    var ADMIN = 'ExperienceManager/Admin/';
    var state = {
        layout: 'everyone', audiences: [], everyone: [], original: [], rows: [], badges: [], selected: null, picked: null,
        libraries: [], genres: [], tags: [], collections: [], error: '', message: '', adding: false
    };
    var root = null;
    var live = null;

    function el(tag, text, className) {
        var node = document.createElement(tag);
        if (text != null) node.textContent = text;
        if (className) node.className = className;
        return node;
    }

    function button(text, className, onClick) {
        var b = el('button', text, 'raised button-alt em-prog-button' + (className ? ' ' + className : ''));
        b.type = 'button';
        b.addEventListener('click', function (ev) { ev.stopPropagation(); onClick(ev); });
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
                    if (r.ok && method !== 'GET') document.dispatchEvent(new CustomEvent('em-draft-changed'));
                    return { ok: r.ok, status: r.status, body: parsed };
                });
            });
    }

    function group() {
        return state.layout !== 'everyone';
    }

    function groupName() {
        var found = state.audiences.filter(function (a) { return a.Id === state.layout; })[0];
        return found ? found.Name : state.layout;
    }

    function sectionOf(s) {
        var section = { Id: s.Id, Type: s.Type };
        if (s.Title) section.Title = s.Title;
        if (s.Config) section.Config = s.Config;
        if (s.Off) section.Off = true;
        return section;
    }

    // Loads the chosen layout and its badges; the working copy starts as what is stored.
    function loadLayout() {
        var everyone = call('GET', ADMIN + 'Pages/home');
        var chosen = group() ? call('GET', ADMIN + 'Audiences/' + encodeURIComponent(state.layout) + '/Pages/home') : everyone;
        var badges = call('GET', ADMIN + 'Pages/home/Badges' + (group() ? '?audience=' + encodeURIComponent(state.layout) : ''));
        return Promise.all([everyone, chosen, badges]).then(function (r) {
            if (!r[0].ok || !r[1].ok) throw new Error('layout');
            state.everyone = r[0].body.Sections.map(sectionOf);
            state.original = group()
                ? r[1].body.Sections.map(function (s) { return { section: sectionOf(s), mark: s.Mark }; })
                : state.everyone.map(function (s) { return { section: copy(s), mark: 'everyone' }; });
            state.rows = copy(state.original);
            state.badges = r[2].ok ? r[2].body.Badges : [];
            state.selected = null;
            state.picked = null;
            state.error = '';
        });
    }

    function loadSources() {
        var userId = ApiClient.getCurrentUserId();
        return Promise.all([
            call('GET', ADMIN + 'Audiences'),
            call('GET', 'Library/VirtualFolders'),
            call('GET', 'Items/Filters2?Recursive=true&UserId=' + userId),
            call('GET', 'Items?Recursive=true&IncludeItemTypes=BoxSet&SortBy=SortName&UserId=' + userId)
        ]).then(function (r) {
            state.audiences = r[0].ok ? r[0].body.Audiences : [];
            state.libraries = r[1].ok ? r[1].body : [];
            state.genres = r[2].ok ? (r[2].body.Genres || []).map(function (g) { return g.Name; }) : [];
            state.tags = r[2].ok ? (r[2].body.Tags || []) : [];
            state.collections = r[3].ok ? r[3].body.Items : [];
        });
    }

    function say(text) {
        if (live) live.textContent = text;
    }

    function dirty() {
        return changes(state.original, state.rows).total > 0;
    }

    function draw(focusId) {
        if (!root) return;
        root.textContent = '';
        root.appendChild(el('p', group()
            ? 'Members of ' + groupName() + ' see this layout. It inherits Everyone\'s; only what you change here is stored for the group.'
            : 'Everyone sees this layout, unless an audience they are in has its own.'));
        live = root.appendChild(el('div', '', 'em-sr'));
        live.setAttribute('aria-live', 'polite');
        live.setAttribute('data-live', '');
        drawToolbar();
        var summary = changes(state.original, state.rows);
        if (summary.total) {
            var banner = root.appendChild(el('div', null, 'em-lay-banner'));
            banner.appendChild(el('span', summary.text, 'em-lay-unsaved'));
            banner.appendChild(button('Save', 'em-lay-save', save));
            banner.appendChild(button('Discard', 'em-lay-discard', function () { loadLayout().then(function () { state.message = 'Changes discarded.'; draw(); }); }));
        }
        if (state.error) root.appendChild(el('p', state.error, 'em-aud-error')).setAttribute('role', 'alert');
        if (state.message) root.appendChild(el('p', state.message, 'em-muted em-lay-message'));
        drawList();
        drawEventAdded();
        if (state.adding) drawAdd();
        if (state.selected != null && state.rows[state.selected]) drawSettings(state.rows[state.selected]);
        if (focusId) {
            var row = root.querySelector('.em-lay-row[data-section="' + focusId + '"]');
            if (row) row.focus();
        }
    }

    function drawToolbar() {
        var bar = root.appendChild(el('div', null, 'em-lay-toolbar'));
        var label = bar.appendChild(el('label', null, 'em-prog-switch'));
        label.appendChild(el('span', 'Layout'));
        var picker = label.appendChild(el('select'));
        picker.name = 'layout';
        [{ Id: 'everyone', Name: 'Everyone' }].concat(state.audiences).forEach(function (a) {
            var option = picker.appendChild(el('option', a.Name));
            option.value = a.Id;
            option.selected = a.Id === state.layout;
        });
        picker.disabled = dirty();
        if (picker.disabled) picker.title = 'Save or discard your changes first';
        picker.addEventListener('change', function () {
            state.layout = picker.value;
            state.message = '';
            loadLayout().then(function () { draw(); });
        });
        bar.appendChild(button('Add a section', 'em-lay-add-open', function () { state.adding = !state.adding; draw(); }));
    }

    function badgesOf(id) {
        return state.badges.filter(function (b) { return b.Id.toLowerCase() === id.toLowerCase(); });
    }

    function drawList() {
        var list = root.appendChild(el('ol', null, 'em-lay-list'));
        list.setAttribute('aria-label', 'Sections, in order');
        var everyoneById = {};
        state.everyone.forEach(function (s) { everyoneById[s.Id] = s; });
        state.rows.forEach(function (r, index) {
            var s = r.section;
            var row = list.appendChild(el('li', null, 'em-lay-row' + (s.Off ? ' em-off' : '') + (r.mark === 'removed' ? ' em-removed' : '') + (state.picked && state.picked.id === s.Id ? ' em-picked' : '')));
            row.tabIndex = 0;
            row.setAttribute('data-section', s.Id);
            row.setAttribute('data-mark', r.mark);
            if (state.selected === index) row.setAttribute('aria-selected', 'true');
            row.draggable = true;
            row.addEventListener('dragstart', function (ev) { ev.dataTransfer.setData('text/plain', String(index)); ev.dataTransfer.effectAllowed = 'move'; });
            row.addEventListener('dragover', function (ev) { ev.preventDefault(); ev.dataTransfer.dropEffect = 'move'; });
            row.addEventListener('drop', function (ev) {
                ev.preventDefault();
                var from = parseInt(ev.dataTransfer.getData('text/plain'), 10);
                if (!isNaN(from) && from !== index) { state.rows = move(state.rows, from, index); state.selected = null; draw(); }
            });
            row.addEventListener('keydown', function (ev) { if (ev.target === row) onKey(ev, index); });
            row.addEventListener('click', function () { state.selected = index; draw(); });
            row.appendChild(el('span', '⠿', 'em-prog-handle')).setAttribute('aria-hidden', 'true');
            var text = row.appendChild(el('div', null, 'em-lay-text'));
            text.appendChild(el('div', titleOf(s), 'em-lay-title'));
            text.appendChild(el('div', typeOf(s.Type).name + (s.Off ? ' · off' : ''), 'em-lay-type'));
            if (group()) text.appendChild(el('div', MARKS[r.mark] + (r.mark === 'inherited' ? '' : groupName()), 'em-lay-mark'));
            badgesOf(s.Id).forEach(function (b) { text.appendChild(el('span', b.Text, 'em-lay-badge' + (b.Kind === 'source-missing' ? ' em-missing' : ''))); });

            if (r.mark !== 'removed') {
                var switchLabel = row.appendChild(el('label', null, 'em-prog-switch'));
                var on = switchLabel.appendChild(el('input'));
                on.type = 'checkbox';
                on.checked = !s.Off;
                on.setAttribute('data-on', s.Id);
                on.addEventListener('click', function (ev) { ev.stopPropagation(); });
                on.addEventListener('change', function () {
                    if (group()) makeOwn(r);
                    if (on.checked) delete s.Off; else s.Off = true;
                    draw(s.Id);
                });
                switchLabel.appendChild(el('span', 'On'));
            }
            var up = row.appendChild(button('↑', 'em-lay-up', function () { state.rows = move(state.rows, index, index - 1); state.selected = null; draw(s.Id); say(announce(titleOf(s), index - 1, state.rows.length)); }));
            up.setAttribute('aria-label', 'Move ' + titleOf(s) + ' up');
            up.disabled = index === 0;
            var down = row.appendChild(button('↓', 'em-lay-down', function () { state.rows = move(state.rows, index, index + 1); state.selected = null; draw(s.Id); say(announce(titleOf(s), index + 1, state.rows.length)); }));
            down.setAttribute('aria-label', 'Move ' + titleOf(s) + ' down');
            down.disabled = index === state.rows.length - 1;
            if (r.mark === 'removed') {
                row.appendChild(button('Restore', 'em-lay-restore', function () { r.mark = 'inherited'; draw(s.Id); }));
            } else {
                if (r.mark === 'changed' && everyoneById[s.Id]) {
                    row.appendChild(button('Use Everyone\'s', 'em-lay-everyones', function () { useEveryones(r, everyoneById[s.Id]); draw(s.Id); }));
                }
                row.appendChild(button('Settings', 'em-lay-settings', function () { state.selected = index; draw(); }));
                row.appendChild(button('Remove', 'em-lay-remove', function () { state.rows = remove(state.rows, index, group()); state.selected = null; draw(); }));
            }
        });
    }

    // Sections an event adds now are not in the layout; they are named under it.
    function drawEventAdded() {
        var ids = {};
        state.rows.forEach(function (r) { ids[r.section.Id.toLowerCase()] = true; });
        state.badges.filter(function (b) { return b.Kind === 'event' && !ids[b.Id.toLowerCase()]; }).forEach(function (b) {
            var line = root.appendChild(el('p', (b.Title || b.Id) + ' · ' + b.Text, 'em-lay-event-added em-muted'));
            line.setAttribute('data-section', b.Id);
        });
    }

    function onKey(ev, index) {
        var picked = state.picked;
        var action = keyAction(ev.key, !!picked);
        if (!action) return;
        ev.preventDefault();
        var s = state.rows[index].section;
        if (action === 'pick') {
            state.picked = { id: s.Id, from: index };
            draw(s.Id);
            say(announce(titleOf(s), index, state.rows.length, 'picked'));
        } else if (action === 'up' || action === 'down') {
            var to = action === 'up' ? index - 1 : index + 1;
            if (to < 0 || to >= state.rows.length) return;
            state.rows = move(state.rows, index, to);
            draw(s.Id);
            say(announce(titleOf(s), to, state.rows.length));
        } else if (action === 'drop') {
            state.picked = null;
            draw(s.Id);
            say(announce(titleOf(s), index, state.rows.length, 'dropped'));
        } else if (action === 'cancel') {
            state.rows = move(state.rows, index, picked.from);
            state.picked = null;
            draw(s.Id);
            say(announce(titleOf(s), picked.from, state.rows.length, 'cancelled'));
        }
    }

    function field(parent, labelText, input) {
        var label = parent.appendChild(el('label', null, 'em-prog-field'));
        label.appendChild(el('span', labelText));
        label.appendChild(input);
        return input;
    }

    function input(type, name, value) {
        var node = el('input');
        node.type = type;
        node.name = name;
        if (value != null) node.value = value;
        return node;
    }

    function select(name, options, value, multiple) {
        var node = el('select');
        node.name = name;
        node.multiple = !!multiple;
        options.forEach(function (o) {
            var option = node.appendChild(el('option', o.text));
            option.value = o.value;
            if (multiple ? (value || []).indexOf(o.value) >= 0 : o.value === value) option.selected = true;
        });
        return node;
    }

    function selected(node) {
        return Array.prototype.filter.call(node.options, function (o) { return o.selected; }).map(function (o) { return o.value; });
    }

    function configValue(config, key) {
        if (!config) return undefined;
        for (var k in config) {
            if (Object.prototype.hasOwnProperty.call(config, k) && k.toLowerCase() === key.toLowerCase()) return config[k];
        }
        return undefined;
    }

    function setConfig(section, key, value) {
        section.Config = section.Config || {};
        for (var k in section.Config) if (k.toLowerCase() === key.toLowerCase()) delete section.Config[k];
        if (value !== undefined) section.Config[key] = value;
    }

    // Each change marks the section as the group's own, then redraws keeping the panel open.
    function edited(row, keep) {
        var wasOwn = row.mark !== 'inherited';
        if (group()) makeOwn(row);
        var banner = root.querySelector('.em-lay-unsaved');
        if (keep && banner && wasOwn) {
            banner.textContent = changes(state.original, state.rows).text;
            return;
        }
        // The banner or the section's mark has to appear: redraw, and keep typing where the admin was.
        var active = document.activeElement && document.activeElement.name;
        draw();
        var again = active && root.querySelector('.em-lay-panel [name="' + active + '"]');
        if (again) {
            again.focus();
            if (again.type === 'text') again.setSelectionRange(again.value.length, again.value.length);
        }
    }

    function drawSettings(row) {
        var s = row.section;
        var panel = root.appendChild(el('form', null, 'em-lay-panel'));
        panel.addEventListener('submit', function (x) { x.preventDefault(); });
        panel.appendChild(el('h3', 'Settings: ' + titleOf(s)));
        if (group() && row.mark === 'inherited') panel.appendChild(el('p', 'Changing it makes it ' + groupName() + '\'s own; Everyone\'s stays as it is.', 'em-muted'));
        field(panel, 'Title', input('text', 'title', s.Title || '')).addEventListener('input', function (x) {
            if (x.target.value) s.Title = x.target.value; else delete s.Title;
            edited(row, true);
        });
        if (s.Type === 'query') {
            var genres = field(panel, 'Genres', select('genres', state.genres.map(function (g) { return { value: g, text: g }; }), configValue(s.Config, 'Genres') || [], true));
            genres.addEventListener('change', function () { setConfig(s, 'Genres', selected(genres)); edited(row, true); });
            var tags = field(panel, 'Tags', select('tags', state.tags.map(function (g) { return { value: g, text: g }; }), configValue(s.Config, 'Tags') || [], true));
            tags.addEventListener('change', function () { setConfig(s, 'Tags', selected(tags)); edited(row, true); });
        }
        if (s.Type === 'query' || s.Type === 'latest') {
            var libraries = (s.Type === 'query' ? [{ value: '', text: 'Every library' }] : []).concat(state.libraries
                .filter(function (l) { return s.Type === 'query' || l.CollectionType === 'movies' || l.CollectionType === 'tvshows'; })
                .map(function (l) { return { value: String(l.ItemId).replace(/-/g, '').toLowerCase(), text: l.Name }; }));
            var current = String(configValue(s.Config, 'Library') || '').replace(/-/g, '').toLowerCase();
            if (s.Type === 'latest' && !current && libraries.length) { setConfig(s, 'Library', libraries[0].value); current = libraries[0].value; }
            var library = field(panel, 'Library', select('library', libraries, current));
            library.addEventListener('change', function () { setConfig(s, 'Library', library.value || undefined); edited(row, true); });
        }
        if (s.Type === 'query') {
            var kinds = field(panel, 'Lists', select('item-types', [{ value: 'Movie,Series', text: 'Films and shows' }, { value: 'Movie', text: 'Films' }, { value: 'Series', text: 'Shows' }], configValue(s.Config, 'ItemTypes') || 'Movie,Series'));
            kinds.addEventListener('change', function () { setConfig(s, 'ItemTypes', kinds.value); edited(row, true); });
        }
        if (s.Type === 'collection') {
            var collections = state.collections.map(function (c) { return { value: String(c.Id).replace(/-/g, '').toLowerCase(), text: c.Name }; });
            if (!collections.length) panel.appendChild(el('p', 'This server has no collections yet.', 'em-muted'));
            var chosen = String(configValue(s.Config, 'Collection') || '').replace(/-/g, '').toLowerCase();
            if (!chosen && collections.length) { setConfig(s, 'Collection', collections[0].value); chosen = collections[0].value; }
            var pick = field(panel, 'Collection', select('collection', collections, chosen));
            pick.addEventListener('change', function () { setConfig(s, 'Collection', pick.value); edited(row, true); });
        }
        if (s.Type === 'query' || s.Type === 'collection') {
            var hide = field(panel, 'Hide watched', input('checkbox', 'hide-watched'));
            hide.checked = !!configValue(s.Config, 'HideWatched');
            hide.addEventListener('change', function () { setConfig(s, 'HideWatched', hide.checked || undefined); edited(row, true); });
        }
        var follows = panel.appendChild(el('fieldset', null, 'em-lay-rules'));
        follows.appendChild(el('legend', 'Content rules this section follows'));
        RULES.forEach(function (rule) {
            var label = follows.appendChild(el('label'));
            var box = label.appendChild(input('checkbox', 'rule'));
            box.value = rule.id;
            box.setAttribute('data-rule', rule.id);
            box.checked = (configValue(s.Config, 'RulesOff') || []).indexOf(rule.id) < 0;
            box.addEventListener('change', function () {
                var off = rulesOff(configValue(s.Config, 'RulesOff'), rule.id, box.checked);
                setConfig(s, 'RulesOff', off.length ? off : undefined);
                edited(row, true);
            });
            label.appendChild(el('span', ' ' + rule.name));
        });
        var schedule = panel.appendChild(el('p', 'Always shown. ', 'em-muted'));
        schedule.appendChild(button('Add it to an event…', 'em-lay-to-event', function () {
            var nav = document.querySelector('#experienceManagerPage .em-nav-page[data-page="programming"]');
            if (nav) nav.click();
        }));
        panel.appendChild(button('Close', 'em-lay-close', function () { state.selected = null; draw(); }));
    }

    function drawAdd() {
        var box = root.appendChild(el('div', null, 'em-lay-add'));
        var after = state.selected != null && state.rows[state.selected] ? state.selected : state.rows.length - 1;
        box.appendChild(el('h3', 'Add a section' + (state.rows[after] ? ' after ' + titleOf(state.rows[after].section) : '')));
        var list = box.appendChild(el('ul', null, 'em-lay-types'));
        TYPES.forEach(function (t) {
            var item = list.appendChild(el('li'));
            var b = item.appendChild(button(t.name, 'em-lay-add-type', function () {
                var section = newSection(t.type, state.rows);
                state.rows.splice(after + 1, 0, { section: section, mark: group() ? 'added' : 'everyone' });
                state.adding = false;
                state.selected = after + 1;
                draw(section.Id);
                say(announce(titleOf(section), after + 1, state.rows.length) + ' added');
            }));
            b.setAttribute('data-type', t.type);
            if (t.type === 'latest-libraries' && hasLatestLibraries(state.rows)) {
                b.disabled = true;
                b.title = 'A layout holds it once';
            }
            item.appendChild(el('span', ' ' + t.about, 'em-muted'));
        });
    }

    function save() {
        var request = group()
            ? call('PUT', ADMIN + 'Audiences/' + encodeURIComponent(state.layout) + '/Pages/home', { Entries: entries(state.rows) })
            : call('PUT', ADMIN + 'Pages/home', { Sections: state.rows.map(function (r) { return r.section; }) });
        request.then(function (r) {
            if (!r.ok) {
                state.error = (r.body && r.body.Error) || 'The layout could not be saved (' + r.status + ').';
                draw();
                return;
            }
            loadLayout().then(function () {
                state.message = 'Saved ' + (group() ? groupName() + '\'s' : 'Everyone\'s') + ' layout.';
                draw();
            });
        });
    }

    window.ExperienceManagerLayout = {
        draw: function (panel) {
            root = panel.appendChild(el('div', null, 'em-lay'));
            state.message = '';
            state.adding = false;
            var status = root.appendChild(el('p', 'Loading…', 'em-muted'));
            loadSources().then(loadLayout).then(function () {
                status.remove();
                draw();
            }, function () {
                status.textContent = 'The layout could not be loaded. Reload the page to try again.';
            });
        }
    };
})();
