// The admin page's Programming page: Home's events on a year or month calendar, as an ordered list, and in an
// editor with Preview. admin.html loads it from /ExperienceManager/admin-programming.js; every route it calls
// is for administrators only. The pure helpers at the top are also loaded by the tests under node.
(function () {
    'use strict';

    var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    var MONTH_NAMES = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
    var DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

    // Holidays on the same date every year: names and dates only, the admin picks what changes.
    var TEMPLATES = [
        { name: 'New Year\'s Day', start: '01-01', end: '01-01' },
        { name: 'Valentine\'s Day', start: '02-14', end: '02-14' },
        { name: 'St Patrick\'s Day', start: '03-17', end: '03-17' },
        { name: 'Independence Day', start: '07-04', end: '07-04' },
        { name: 'Halloween', start: '10-01', end: '10-31' },
        { name: 'Christmas', start: '12-01', end: '12-26' },
        { name: 'New Year\'s Eve', start: '12-31', end: '12-31' }
    ];

    var CHANGE_LABELS = { swap: 'Swap a section\'s source', featured: 'Override Featured', insert: 'Add a section', hide: 'Hide a section' };

    function pad(n) {
        return (n < 10 ? '0' : '') + n;
    }

    function isLeap(year) {
        return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
    }

    function daysInMonth(year, month) {
        return [31, isLeap(year) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
    }

    function daysInYear(year) {
        return isLeap(year) ? 366 : 365;
    }

    // Days before a date in its year: 1 January is 0.
    function dayOfYear(year, month, day) {
        var n = day - 1;
        for (var m = 1; m < month; m++) n += daysInMonth(year, m);
        return n;
    }

    function parseLocal(text) {
        var m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(text || '');
        return m ? { year: +m[1], month: +m[2], day: +m[3], hour: +m[4], minute: +m[5] } : null;
    }

    function parseDay(text) {
        var m = /^(\d{2})-(\d{2})$/.exec(text || '');
        return m ? { month: +m[1], day: +m[2] } : null;
    }

    function formatLocal(date) {
        return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) + 'T' + pad(date.getHours()) + ':' + pad(date.getMinutes());
    }

    // February 29 falls on March 1 in other years, as the server reads it.
    function dayIndex(year, md) {
        if (md.month === 2 && md.day === 29 && !isLeap(year)) return dayOfYear(year, 3, 1);
        return dayOfYear(year, md.month, Math.min(md.day, daysInMonth(year, md.month)));
    }

    // The days of a year an event touches, as runs of day indexes (inclusive). A weekly event is drawn across
    // the whole year and marked weekly, since it touches some days of every week.
    function spans(schedule, year) {
        var last = daysInYear(year) - 1;
        if (schedule.Kind === 'weekly') return [{ start: 0, end: last, weekly: true }];
        if (schedule.Kind === 'yearly') {
            var first = parseDay(schedule.Start);
            var final = parseDay(schedule.End);
            if (!first || !final) return [];
            var a = dayIndex(year, first);
            var b = dayIndex(year, final);
            return a <= b ? [{ start: a, end: b }] : [{ start: 0, end: b }, { start: a, end: last }];
        }
        if (schedule.Kind === 'once') {
            var from = parseLocal(schedule.Start);
            var to = parseLocal(schedule.End);
            if (!from || !to) return [];
            var startDay = from.year < year ? 0 : from.year > year ? Infinity : dayOfYear(year, from.month, from.day);
            // The end is not included: an event ending at midnight ends the day before.
            var endMidnight = to.hour === 0 && to.minute === 0;
            var endDay = to.year > year ? last : to.year < year ? -1 : dayOfYear(year, to.month, to.day) - (endMidnight ? 1 : 0);
            return startDay <= endDay && startDay <= last ? [{ start: startDay, end: Math.min(endDay, last) }] : [];
        }
        return [];
    }

    // Whether an event touches a day at all.
    function activeOn(schedule, year, month, day) {
        if (schedule.Kind === 'weekly') {
            var weekday = new Date(year, month - 1, day).getDay();
            return (schedule.Days || []).some(function (d) { return d.toLowerCase() === DAYS[weekday].toLowerCase(); });
        }
        var index = dayOfYear(year, month, day);
        return spans(schedule, year).some(function (s) { return index >= s.start && index <= s.end; });
    }

    // The section an event change touches: Featured, a section it swaps or hides, or the one it adds.
    function laneOf(change) {
        if (change.Kind === 'featured') return change.Target || 'featured';
        if (change.Kind === 'insert') return change.Section ? change.Section.Id : null;
        return change.Target || null;
    }

    // One lane for Featured, then one for each section an event changes, in the order they are first changed;
    // each lane lists the events that change it.
    function lanes(events, titles) {
        var list = [{ id: 'featured', title: titles.featured || 'Featured', events: [] }];
        var byId = { featured: list[0] };
        events.forEach(function (e) {
            (e.Changes || []).forEach(function (c) {
                var id = laneOf(c);
                if (!id) return;
                var lane = byId[id];
                if (!lane) {
                    var added = c.Kind === 'insert' && c.Section ? (c.Section.Title || sourceText(c.Section, {})) : null;
                    lane = byId[id] = { id: id, title: titles[id] || added || id, events: [] };
                    list.push(lane);
                }
                if (lane.events.indexOf(e) < 0) lane.events.push(e);
            });
        });
        return list;
    }

    // A month as weeks of day numbers, Sunday first; null pads the first and last weeks.
    function monthGrid(year, month) {
        var weeks = [];
        var week = [];
        var first = new Date(year, month - 1, 1).getDay();
        for (var i = 0; i < first; i++) week.push(null);
        for (var d = 1; d <= daysInMonth(year, month); d++) {
            week.push(d);
            if (week.length === 7) { weeks.push(week); week = []; }
        }
        if (week.length) {
            while (week.length < 7) week.push(null);
            weeks.push(week);
        }
        return weeks;
    }

    function dayText(md) {
        return md ? md.day + ' ' + MONTHS[md.month - 1] : '?';
    }

    function localText(t) {
        return t ? t.day + ' ' + MONTHS[t.month - 1] + ' ' + t.year + ' ' + pad(t.hour) + ':' + pad(t.minute) : '?';
    }

    function describeSchedule(schedule) {
        if (schedule.Kind === 'once') return localText(parseLocal(schedule.Start)) + ' to ' + localText(parseLocal(schedule.End));
        if (schedule.Kind === 'yearly') {
            return schedule.Start === schedule.End
                ? 'Every year on ' + dayText(parseDay(schedule.Start))
                : 'Every year, ' + dayText(parseDay(schedule.Start)) + ' to ' + dayText(parseDay(schedule.End));
        }
        if (schedule.Kind === 'weekly') {
            var days = DAYS.filter(function (d) {
                return (schedule.Days || []).some(function (x) { return x.toLowerCase() === d.toLowerCase(); });
            }).map(function (d) { return d + 's'; });
            return 'Every week on ' + days.join(', ') + ', ' + schedule.From + ' to ' + schedule.To;
        }
        return schedule.Kind;
    }

    function configValue(config, key) {
        if (!config) return undefined;
        for (var k in config) {
            if (Object.prototype.hasOwnProperty.call(config, k) && k.toLowerCase() === key.toLowerCase()) return config[k];
        }
        return undefined;
    }

    // What a section shows, in a few words.
    function sourceText(section, names) {
        if (!section) return '?';
        var config = section.Config;
        if (section.Type === 'query') {
            var parts = (configValue(config, 'Genres') || []).concat(configValue(config, 'Tags') || []);
            return (section.Title ? section.Title + ' (' : '') + parts.join(' or ') + (section.Title ? ')' : '');
        }
        if (section.Type === 'collection') {
            var id = configValue(config, 'Collection');
            return section.Title || names[id] || 'a collection';
        }
        return section.Title || names[section.Id] || section.Type;
    }

    function describeChange(change, titles) {
        var name = function (id) { return titles[id] || id; };
        if (change.Kind === 'hide') return 'Hide ' + name(change.Target);
        if (change.Kind === 'swap') return 'Swap ' + name(change.Target) + ' for ' + sourceText(change.Section, titles);
        if (change.Kind === 'featured') return 'Featured: ' + sourceText(change.Section, titles);
        if (change.Kind === 'insert') return 'Add ' + sourceText(change.Section, titles) + (change.After ? ' after ' + name(change.After) : ' after the featured banner');
        return change.Kind;
    }

    function slug(text) {
        var s = String(text || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
        return s || 'new-section';
    }

    // The list with one item moved: what dragging an event to another place in the order does.
    function move(list, from, to) {
        var copy = list.slice();
        var item = copy.splice(from, 1)[0];
        copy.splice(to, 0, item);
        return copy;
    }

    // The sections an event can name, as the admin's Home shows them: Latest for each library becomes one
    // section per movie and show library.
    function sectionChoices(stored, libraries) {
        var builtIn = { featured: 'Featured', mymedia: 'My Media', 'continue': 'Continue Watching', nextup: 'Next Up' };
        var list = [];
        (stored || []).forEach(function (s) {
            if (s.Type === 'latest-libraries') {
                (libraries || []).forEach(function (l) {
                    if (l.CollectionType === 'movies' || l.CollectionType === 'tvshows') {
                        list.push({ id: 'latest-' + String(l.ItemId).replace(/-/g, '').toLowerCase(), title: 'Latest ' + l.Name });
                    }
                });
            } else {
                list.push({ id: s.Id, title: s.Title || builtIn[s.Type] || s.Id });
            }
        });
        return list;
    }

    var pure = {
        TEMPLATES: TEMPLATES, spans: spans, activeOn: activeOn, lanes: lanes, monthGrid: monthGrid, describeSchedule: describeSchedule,
        describeChange: describeChange, sourceText: sourceText, slug: slug, move: move, sectionChoices: sectionChoices,
        dayOfYear: dayOfYear, daysInYear: daysInYear, formatLocal: formatLocal
    };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = pure;
        return;
    }

    // ---- The page ----

    var BASE = 'ExperienceManager/Admin/';
    var state = { view: 'year', date: new Date(), events: [], conflicts: [], stored: [], libraries: [], genres: [], tags: [], collections: [], editing: null, message: '' };
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

    // Every call returns { ok, status, body }; a refusal carries the server's reason in body.Error.
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

    function titles() {
        var map = {};
        sectionChoices(state.stored, state.libraries).forEach(function (s) { map[s.id] = s.title; });
        state.collections.forEach(function (c) { map[c.Id] = c.Name; map[String(c.Id).replace(/-/g, '')] = c.Name; });
        state.events.forEach(function (e) {
            (e.Changes || []).forEach(function (c) { if (c.Kind === 'insert' && c.Section) map[c.Section.Id] = c.Section.Title || sourceText(c.Section, map); });
        });
        return map;
    }

    function isPhone() {
        return window.matchMedia && window.matchMedia('(max-width: 600px)').matches;
    }

    function load() {
        var userId = ApiClient.getCurrentUserId();
        return Promise.all([
            call('GET', BASE + 'Events'),
            call('GET', BASE + 'Pages/home'),
            call('GET', 'Library/VirtualFolders'),
            call('GET', 'Items/Filters2?Recursive=true&UserId=' + userId),
            call('GET', 'Items?Recursive=true&IncludeItemTypes=BoxSet&SortBy=SortName&UserId=' + userId)
        ]).then(function (r) {
            if (!r[0].ok || !r[1].ok) throw new Error('events');
            state.events = r[0].body.Events;
            state.conflicts = r[0].body.Conflicts;
            state.stored = r[1].body.Sections;
            state.libraries = r[2].ok ? r[2].body : [];
            state.genres = r[3].ok ? (r[3].body.Genres || []).map(function (g) { return g.Name; }) : [];
            state.tags = r[3].ok ? (r[3].body.Tags || []) : [];
            state.collections = r[4].ok ? r[4].body.Items : [];
        });
    }

    function reloadEvents() {
        return call('GET', BASE + 'Events').then(function (r) {
            if (r.ok) {
                state.events = r.body.Events;
                state.conflicts = r.body.Conflicts;
            }
            draw();
        });
    }

    function draw() {
        if (!root) return;
        root.textContent = '';
        root.appendChild(el('p', 'Events change Home for a while and never edit its layout. An event higher in the list wins where two change the same section at the same time.'));
        if (state.message) root.appendChild(el('p', state.message, 'em-prog-message'));
        drawToolbar();
        if (!state.events.length || state.showTemplates) drawTemplates();
        if (isPhone()) drawMonth(true);
        else if (state.view === 'year') drawYear();
        else drawMonth(false);
        drawList();
        if (state.editing) drawEditor();
    }

    function drawToolbar() {
        var bar = root.appendChild(el('div', null, 'em-prog-toolbar'));
        var year = state.date.getFullYear();
        var month = state.date.getMonth();
        if (!isPhone()) {
            ['year', 'month'].forEach(function (v) {
                var b = bar.appendChild(button(v === 'year' ? 'Year' : 'Month', 'em-prog-view', function () { state.view = v; draw(); }));
                b.setAttribute('data-view', v);
                if (state.view === v) b.setAttribute('aria-pressed', 'true');
            });
        }
        var monthView = isPhone() || state.view === 'month';
        bar.appendChild(button('‹', 'em-prog-prev', function () {
            state.date = monthView ? new Date(year, month - 1, 1) : new Date(year - 1, month, 1);
            draw();
        })).setAttribute('aria-label', 'Previous');
        bar.appendChild(el('span', monthView ? MONTH_NAMES[month] + ' ' + year : String(year), 'em-prog-label'));
        bar.appendChild(button('›', 'em-prog-next', function () {
            state.date = monthView ? new Date(year, month + 1, 1) : new Date(year + 1, month, 1);
            draw();
        })).setAttribute('aria-label', 'Next');
        bar.appendChild(button('Today', 'em-prog-today', function () { state.date = new Date(); draw(); }));
        bar.appendChild(button('New event', 'em-prog-new', function () { edit(null); }));
        bar.appendChild(button('New event from a template', 'em-prog-from-template', function () { state.showTemplates = !state.showTemplates; draw(); }));
    }

    function drawTemplates() {
        var box = root.appendChild(el('div', null, 'em-prog-templates'));
        box.appendChild(el('h3', state.events.length ? 'Start from a holiday' : 'No events yet. Start from a holiday:'));
        TEMPLATES.forEach(function (t) {
            var b = box.appendChild(button(t.name + ' · ' + describeSchedule({ Kind: 'yearly', Start: t.start, End: t.end }).replace('Every year, ', '').replace('Every year on ', ''), 'em-prog-template', function () {
                state.showTemplates = false;
                edit({ Id: '', Name: t.name, Published: false, Audience: 'everyone', Schedule: { Kind: 'yearly', Start: t.start, End: t.end }, Changes: [] });
            }));
            b.setAttribute('data-template', t.name);
        });
    }

    function bar(e, span, year) {
        var node = el('div', e.Name, 'em-prog-bar' + (e.Published ? ' em-published' : ' em-draft') + (span.weekly ? ' em-weekly' : ''));
        var days = daysInYear(year);
        node.style.left = (span.start / days * 100) + '%';
        node.style.width = Math.max((span.end - span.start + 1) / days * 100, 0.6) + '%';
        node.title = e.Name + (e.Published ? '' : ' (draft)') + ': ' + describeSchedule(e.Schedule);
        node.setAttribute('data-event', e.Id);
        node.addEventListener('click', function () { edit(e); });
        return node;
    }

    function drawYear() {
        var year = state.date.getFullYear();
        var box = root.appendChild(el('div', null, 'em-prog-year'));
        var head = box.appendChild(el('div', null, 'em-prog-lane em-prog-months'));
        head.appendChild(el('div', '', 'em-prog-lane-title'));
        var months = head.appendChild(el('div', null, 'em-prog-track'));
        for (var m = 1; m <= 12; m++) {
            var cell = months.appendChild(el('div', MONTHS[m - 1], 'em-prog-month'));
            cell.style.left = (dayOfYear(year, m, 1) / daysInYear(year) * 100) + '%';
        }
        lanes(state.events, titles()).forEach(function (lane) {
            var row = box.appendChild(el('div', null, 'em-prog-lane'));
            row.setAttribute('data-lane', lane.id);
            row.appendChild(el('div', lane.title, 'em-prog-lane-title'));
            var track = row.appendChild(el('div', null, 'em-prog-track'));
            // Each event in a lane has its own row, so bars that overlap stay readable.
            row.style.minHeight = (0.7 + 1.7 * Math.max(1, lane.events.length)) + 'em';
            lane.events.forEach(function (e, i) {
                spans(e.Schedule, year).forEach(function (s) {
                    var node = track.appendChild(bar(e, s, year));
                    node.style.top = (0.35 + 1.7 * i) + 'em';
                });
            });
            if (!lane.events.length) track.appendChild(el('span', 'No event changes Featured.', 'em-muted em-prog-empty'));
        });
        var today = new Date();
        if (today.getFullYear() === year) {
            var marker = box.appendChild(el('div', null, 'em-prog-today-marker'));
            marker.style.left = 'calc(10em + (100% - 10em) * ' + (dayOfYear(year, today.getMonth() + 1, today.getDate()) / daysInYear(year)) + ')';
            marker.title = 'Today';
        }
    }

    function drawMonth(compact) {
        var year = state.date.getFullYear();
        var month = state.date.getMonth() + 1;
        var table = root.appendChild(el('table', null, 'em-prog-monthgrid' + (compact ? ' em-compact' : '')));
        var head = table.appendChild(el('thead')).appendChild(el('tr'));
        DAYS.forEach(function (d) { head.appendChild(el('th', compact ? d.charAt(0) : d.slice(0, 3))); });
        var body = table.appendChild(el('tbody'));
        var today = new Date();
        monthGrid(year, month).forEach(function (week) {
            var tr = body.appendChild(el('tr'));
            week.forEach(function (day) {
                var td = tr.appendChild(el('td'));
                if (day == null) return;
                td.setAttribute('data-day', day);
                if (today.getFullYear() === year && today.getMonth() + 1 === month && today.getDate() === day) td.className = 'em-prog-is-today';
                td.appendChild(el('div', String(day), 'em-prog-daynum'));
                state.events.forEach(function (e) {
                    if (!activeOn(e.Schedule, year, month, day)) return;
                    var chip = td.appendChild(el(compact ? 'span' : 'div', compact ? '' : e.Name, 'em-prog-chip' + (e.Published ? ' em-published' : ' em-draft')));
                    chip.title = e.Name + (e.Published ? '' : ' (draft)');
                    chip.setAttribute('data-event', e.Id);
                    chip.addEventListener('click', function () { edit(e); });
                });
            });
        });
    }

    function saveOrder(ids) {
        return call('PUT', BASE + 'Events/Order', { Ids: ids }).then(function (r) {
            state.message = r.ok ? '' : (r.body && r.body.Error) || 'The new order could not be saved.';
            return reloadEvents();
        });
    }

    function drawList() {
        root.appendChild(el('h3', 'Events, highest first'));
        var list = root.appendChild(el('ol', null, 'em-prog-list'));
        var ids = state.events.map(function (e) { return e.Id; });
        state.events.forEach(function (e, index) {
            var row = list.appendChild(el('li', null, 'em-prog-row' + (e.Published ? ' em-published' : ' em-draft')));
            row.setAttribute('data-event', e.Id);
            row.draggable = true;
            row.addEventListener('dragstart', function (ev) { ev.dataTransfer.setData('text/plain', String(index)); ev.dataTransfer.effectAllowed = 'move'; });
            row.addEventListener('dragover', function (ev) { ev.preventDefault(); ev.dataTransfer.dropEffect = 'move'; });
            row.addEventListener('drop', function (ev) {
                ev.preventDefault();
                var from = parseInt(ev.dataTransfer.getData('text/plain'), 10);
                if (!isNaN(from) && from !== index) saveOrder(move(ids, from, index));
            });
            row.appendChild(el('span', '⠿', 'em-prog-handle')).setAttribute('aria-hidden', 'true');
            var text = row.appendChild(el('div', null, 'em-prog-row-text'));
            text.appendChild(el('div', e.Name + (e.Published ? '' : ' · draft'), 'em-prog-row-name'));
            text.appendChild(el('div', describeSchedule(e.Schedule), 'em-muted em-prog-row-when'));
            var label = row.appendChild(el('label', null, 'em-prog-switch'));
            var box = label.appendChild(el('input'));
            box.type = 'checkbox';
            box.checked = e.Published;
            box.setAttribute('data-publish', e.Id);
            label.appendChild(el('span', 'Published'));
            box.addEventListener('change', function () {
                call('POST', BASE + 'Events/' + encodeURIComponent(e.Id) + (box.checked ? '/Publish' : '/Unpublish')).then(function (r) {
                    state.message = r.ok ? '' : 'The event could not be ' + (box.checked ? 'published.' : 'unpublished.');
                    reloadEvents();
                });
            });
            var moves = row.appendChild(el('span', null, 'em-prog-moves'));
            var up = moves.appendChild(button('↑', 'em-prog-up', function () { saveOrder(move(ids, index, index - 1)); }));
            up.setAttribute('aria-label', 'Move ' + e.Name + ' up');
            up.disabled = index === 0;
            var down = moves.appendChild(button('↓', 'em-prog-down', function () { saveOrder(move(ids, index, index + 1)); }));
            down.setAttribute('aria-label', 'Move ' + e.Name + ' down');
            down.disabled = index === state.events.length - 1;
            row.appendChild(button('Edit', 'em-prog-edit', function () { edit(e); }));
        });
        if (!state.events.length) root.appendChild(el('p', 'No events.', 'em-muted'));
        state.conflicts.forEach(function (line) { root.appendChild(el('p', line, 'em-prog-conflict')); });
    }

    function edit(e) {
        state.editing = JSON.parse(JSON.stringify(e || { Id: '', Name: '', Published: false, Audience: 'everyone', Schedule: { Kind: 'yearly', Start: '12-01', End: '12-26' }, Changes: [] }));
        state.editingNew = !e || !e.Id;
        state.editorError = '';
        state.preview = null;
        draw();
        var name = root.querySelector('.em-prog-editor input[name="name"]');
        if (name) name.focus();
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

    function monthDay(parent, labelText, name, value, onChange) {
        var md = parseDay(value) || { month: 1, day: 1 };
        var wrap = parent.appendChild(el('span', null, 'em-prog-monthday'));
        wrap.appendChild(el('span', labelText));
        var month = wrap.appendChild(select(name + '-month', MONTH_NAMES.map(function (n, i) { return { value: pad(i + 1), text: n }; }), pad(md.month)));
        var day = wrap.appendChild(input('number', name + '-day', md.day));
        day.min = 1;
        day.max = 31;
        function update() { onChange(month.value + '-' + pad(Math.max(1, Math.min(31, parseInt(day.value, 10) || 1)))); }
        month.addEventListener('change', update);
        day.addEventListener('change', update);
    }

    function drawSchedule(form, ev) {
        var s = ev.Schedule;
        var box = form.appendChild(el('fieldset', null, 'em-prog-schedule'));
        box.appendChild(el('legend', 'When'));
        var kind = field(box, 'Repeats', select('kind', [{ value: 'once', text: 'Once' }, { value: 'yearly', text: 'Every year' }, { value: 'weekly', text: 'Every week' }], s.Kind));
        kind.addEventListener('change', function () {
            var now = new Date();
            ev.Schedule = kind.value === 'once'
                ? { Kind: 'once', Start: formatLocal(now).slice(0, 11) + '00:00', End: formatLocal(new Date(now.getTime() + 86400000)).slice(0, 11) + '00:00' }
                : kind.value === 'yearly' ? { Kind: 'yearly', Start: '12-01', End: '12-26' } : { Kind: 'weekly', Days: ['Friday'], From: '18:00', To: '23:00' };
            draw();
        });
        if (s.Kind === 'once') {
            field(box, 'Starts', input('datetime-local', 'start', s.Start)).addEventListener('change', function (x) { s.Start = x.target.value; });
            field(box, 'Ends', input('datetime-local', 'end', s.End)).addEventListener('change', function (x) { s.End = x.target.value; });
        } else if (s.Kind === 'yearly') {
            monthDay(box, 'From', 'start', s.Start, function (v) { s.Start = v; });
            monthDay(box, 'to', 'end', s.End, function (v) { s.End = v; });
        } else if (s.Kind === 'weekly') {
            var days = box.appendChild(el('div', null, 'em-prog-days'));
            DAYS.slice(1).concat(DAYS.slice(0, 1)).forEach(function (d) {
                var label = days.appendChild(el('label'));
                var box2 = label.appendChild(input('checkbox', 'day', d));
                box2.checked = (s.Days || []).some(function (x) { return x.toLowerCase() === d.toLowerCase(); });
                box2.addEventListener('change', function () {
                    s.Days = selectedDays(days);
                });
                label.appendChild(el('span', d.slice(0, 3)));
            });
            field(box, 'From', input('time', 'from', s.From)).addEventListener('change', function (x) { s.From = x.target.value; });
            field(box, 'To', input('time', 'to', s.To)).addEventListener('change', function (x) { s.To = x.target.value; });
        }
    }

    function selectedDays(box) {
        return Array.prototype.filter.call(box.querySelectorAll('input'), function (i) { return i.checked; }).map(function (i) { return i.value; });
    }

    // One source picker for every change: a genre or tag query, or a collection.
    function drawSource(parent, change, needsId) {
        var section = change.Section || (change.Section = { Id: '', Type: 'query', Title: '', Config: { Genres: [], ItemTypes: 'Movie,Series' } });
        var box = parent.appendChild(el('div', null, 'em-prog-source'));
        var type = field(box, 'Shows', select('source-type', [{ value: 'query', text: 'Titles by genre or tag' }, { value: 'collection', text: 'A collection' }], section.Type));
        type.addEventListener('change', function () {
            section.Type = type.value;
            section.Config = type.value === 'query' ? { Genres: [], ItemTypes: 'Movie,Series' } : { Collection: state.collections.length ? state.collections[0].Id : '' };
            draw();
        });
        var config = section.Config || (section.Config = {});
        field(box, 'Title', input('text', 'source-title', section.Title || '')).addEventListener('input', function (x) {
            section.Title = x.target.value;
            if (needsId && change.autoId !== false) section.Id = slug(x.target.value);
        });
        if (section.Type === 'query') {
            var genres = field(box, 'Genres', select('genres', state.genres.map(function (g) { return { value: g, text: g }; }), configValue(config, 'Genres') || [], true));
            genres.addEventListener('change', function () { config.Genres = selected(genres); delete config.genres; });
            var tags = field(box, 'Tags', select('tags', state.tags.map(function (g) { return { value: g, text: g }; }), configValue(config, 'Tags') || [], true));
            tags.addEventListener('change', function () { config.Tags = selected(tags); delete config.tags; });
            var libraries = [{ value: '', text: 'Every library' }].concat(state.libraries.map(function (l) { return { value: String(l.ItemId), text: l.Name }; }));
            var library = field(box, 'Library', select('library', libraries, configValue(config, 'Library') || ''));
            library.addEventListener('change', function () { if (library.value) config.Library = library.value; else delete config.Library; });
            var kinds = field(box, 'Lists', select('item-types', [{ value: 'Movie,Series', text: 'Films and shows' }, { value: 'Movie', text: 'Films' }, { value: 'Series', text: 'Shows' }], configValue(config, 'ItemTypes') || 'Movie,Series'));
            kinds.addEventListener('change', function () { config.ItemTypes = kinds.value; });
            var hide = field(box, 'Hide watched', input('checkbox', 'hide-watched'));
            hide.checked = !!configValue(config, 'HideWatched');
            hide.addEventListener('change', function () { config.HideWatched = hide.checked; });
        } else {
            var collections = state.collections.map(function (c) { return { value: c.Id, text: c.Name }; });
            if (!collections.length) box.appendChild(el('p', 'This server has no collections yet.', 'em-muted'));
            var pick = field(box, 'Collection', select('collection', collections, configValue(config, 'Collection')));
            pick.addEventListener('change', function () { config.Collection = pick.value; });
        }
    }

    function drawChanges(form, ev) {
        var box = form.appendChild(el('fieldset', null, 'em-prog-changes'));
        box.appendChild(el('legend', 'What changes'));
        var choices = sectionChoices(state.stored, state.libraries).map(function (s) { return { value: s.id, text: s.title }; });
        ev.Changes.forEach(function (change, index) {
            var row = box.appendChild(el('div', null, 'em-prog-change'));
            row.setAttribute('data-change', index);
            var kind = field(row, 'Change', select('change-kind', Object.keys(CHANGE_LABELS).map(function (k) { return { value: k, text: CHANGE_LABELS[k] }; }), change.Kind));
            kind.addEventListener('change', function () { ev.Changes[index] = { Kind: kind.value }; draw(); });
            if (change.Kind === 'swap' || change.Kind === 'hide') {
                if (!change.Target && choices.length) change.Target = choices[0].value;
                var target = field(row, 'Section', select('target', choices, change.Target));
                target.addEventListener('change', function () { change.Target = target.value; });
            }
            if (change.Kind === 'insert') {
                var after = field(row, 'After', select('after', [{ value: '', text: 'The featured banner' }].concat(choices), change.After || ''));
                after.addEventListener('change', function () { change.After = after.value || null; });
            }
            if (change.Kind !== 'hide') drawSource(row, change, change.Kind === 'insert');
            row.appendChild(button('Remove this change', 'em-prog-remove-change', function () { ev.Changes.splice(index, 1); draw(); }));
        });
        box.appendChild(button('Add a change', 'em-prog-add-change', function () { ev.Changes.push({ Kind: 'insert', After: 'nextup' }); draw(); }));
    }

    // The event as the server takes it: an added section's id comes from its title, a swap's from its target.
    function eventToSend(ev) {
        var copy = JSON.parse(JSON.stringify(ev));
        copy.Changes.forEach(function (c) {
            delete c.autoId;
            if (c.Kind === 'hide') delete c.Section;
            if (c.Section) {
                if (c.Kind === 'insert') c.Section.Id = c.Section.Id || slug(c.Section.Title || sourceText(c.Section, {}));
                else c.Section.Id = c.Target || 'featured';
                if (!c.Section.Title) delete c.Section.Title;
            }
            if (c.Kind !== 'insert') delete c.After;
        });
        return copy;
    }

    function drawEditor() {
        var ev = state.editing;
        var form = root.appendChild(el('form', null, 'em-prog-editor'));
        form.addEventListener('submit', function (x) { x.preventDefault(); });
        form.appendChild(el('h3', state.editingNew ? 'New event' : 'Edit ' + ev.Name));
        field(form, 'Name', input('text', 'name', ev.Name)).addEventListener('input', function (x) { ev.Name = x.target.value; });
        drawSchedule(form, ev);
        var audience = field(form, 'Audience', input('text', 'audience', 'Everyone'));
        audience.disabled = true;
        form.appendChild(el('p', 'Groups come later; every event is for everyone until then.', 'em-muted'));
        drawChanges(form, ev);
        if (state.editorError) form.appendChild(el('p', state.editorError, 'em-prog-error')).setAttribute('role', 'alert');

        var actions = form.appendChild(el('div', null, 'em-prog-actions'));
        actions.appendChild(button('Save', 'em-prog-save', function () {
            var body = eventToSend(ev);
            var request = state.editingNew ? call('POST', BASE + 'Events', body) : call('PUT', BASE + 'Events/' + encodeURIComponent(ev.Id), body);
            request.then(function (r) {
                if (!r.ok) {
                    state.editorError = (r.body && r.body.Error) || 'The event could not be saved (' + r.status + ').';
                    draw();
                    return;
                }
                state.editing = null;
                state.message = 'Saved ' + r.body.Name + '.';
                reloadEvents();
            });
        }));
        actions.appendChild(button('Cancel', 'em-prog-cancel', function () { state.editing = null; draw(); }));
        if (!state.editingNew) {
            actions.appendChild(button('Delete', 'em-prog-delete', function () {
                call('DELETE', BASE + 'Events/' + encodeURIComponent(ev.Id)).then(function () { state.editing = null; reloadEvents(); });
            }));
        }

        var preview = form.appendChild(el('fieldset', null, 'em-prog-preview'));
        preview.appendChild(el('legend', 'Preview Home'));
        var at = field(preview, 'On', input('datetime-local', 'preview-at', state.previewAt || formatLocal(new Date())));
        at.addEventListener('change', function () { state.previewAt = at.value; });
        preview.appendChild(button('Preview', 'em-prog-run-preview', function () {
            state.previewAt = at.value;
            var body = eventToSend(ev);
            if (!body.Id) body.Id = slug(body.Name) + '-preview';
            call('POST', BASE + 'Pages/home/Preview', { At: at.value, Event: body }).then(function (r) {
                state.preview = r.ok ? r.body : { error: (r.body && r.body.Error) || 'The preview could not be made.' };
                draw();
            });
        }));
        if (state.preview) drawPreview(preview, state.preview);
    }

    function drawPreview(parent, p) {
        if (p.error) {
            parent.appendChild(el('p', p.error, 'em-prog-error'));
            return;
        }
        parent.appendChild(el('p', p.Events.length ? 'Events on: ' + p.Events.join(', ') : 'No event is on then.', 'em-muted'));
        var verbs = { insert: 'added by', swap: 'swapped by', featured: 'featured by' };
        var list = parent.appendChild(el('ol', null, 'em-prog-preview-list'));
        p.Sections.forEach(function (s) {
            var item = list.appendChild(el('li', s.Title + (s.Display === 'feature' ? ' (banner)' : '')));
            item.setAttribute('data-section', s.Id);
            if (s.Event) item.appendChild(el('span', ' · ' + verbs[s.Change] + ' ' + s.Event, 'em-prog-mark'));
        });
        p.Hidden.forEach(function (h) {
            var item = parent.appendChild(el('p', h.Title + ' · hidden by ' + h.Event, 'em-prog-hidden'));
            item.setAttribute('data-section', h.Id);
        });
    }

    window.ExperienceManagerProgramming = {
        draw: function (panel) {
            root = panel.appendChild(el('div', null, 'em-prog'));
            var status = root.appendChild(el('p', 'Loading…', 'em-muted'));
            load().then(function () {
                status.remove();
                draw();
            }, function () {
                status.textContent = 'The events could not be loaded. Reload the page to try again.';
            });
        },
        redraw: draw
    };
})();
