// The admin page's context bar: who (everyone, an audience or a user), on which device, at which date and time.
// Home layout and Programming follow it, and it is kept for the session. admin.html loads it from
// /ExperienceManager/admin-context.js. The pure helpers at the top are also loaded by the tests under node.
(function () {
    'use strict';

    var DEVICES = [
        { id: 'tv', name: 'TV', width: 960 },
        { id: 'web', name: 'web', width: 1200 },
        { id: 'phone', name: 'phone', width: 390 }
    ];
    var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

    function pad(n) {
        return (n < 10 ? '0' : '') + n;
    }

    function dateText(date) {
        return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate());
    }

    function initial(now) {
        return { who: 'everyone', device: 'tv', date: dateText(now), time: pad(now.getHours()) + ':' + pad(now.getMinutes()) };
    }

    // The moment the bar names, as the server's preview takes it.
    function at(context) {
        return context.date + 'T' + context.time;
    }

    // 'everyone', 'audience:<id>' or 'user:<id>'.
    function kindOf(context) {
        var parts = String(context.who || 'everyone').split(':');
        return { kind: parts[0], id: parts.slice(1).join(':') || null };
    }

    function whoName(context, names) {
        var who = kindOf(context);
        if (who.kind === 'everyone') return 'Everyone';
        return (names && names[context.who]) || who.id;
    }

    function dayName(dateString, today) {
        var parts = dateString.split('-');
        var text = +parts[2] + ' ' + MONTHS[+parts[1] - 1];
        return dateString === today ? 'Today, ' + text : text;
    }

    function label(context, names, today) {
        var device = DEVICES.filter(function (d) { return d.id === context.device; })[0] || DEVICES[0];
        return 'Viewing as ' + whoName(context, names) + ' · on ' + device.name + ' · on ' + dayName(context.date, today) + ', ' + context.time;
    }

    // What the server's preview takes for the bar's viewer.
    function previewFor(context) {
        var who = kindOf(context);
        if (who.kind === 'user') return { UserId: who.id };
        if (who.kind === 'audience') return { Audience: who.id };
        return { Audience: 'everyone' };
    }

    var pure = { DEVICES: DEVICES, initial: initial, at: at, kindOf: kindOf, label: label, previewFor: previewFor, dateText: dateText };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = pure;
        return;
    }

    // ---- The bar ----

    var KEY = 'em-context';
    var cache = { names: {}, audiences: [], users: [], events: [] };

    function el(tag, text, className) {
        var node = document.createElement(tag);
        if (text != null) node.textContent = text;
        if (className) node.className = className;
        return node;
    }

    function get() {
        var context = initial(new Date());
        try {
            var saved = JSON.parse(sessionStorage.getItem(KEY) || 'null');
            if (saved && saved.date && saved.time) context = saved;
        } catch (e) { /* storage unavailable */ }
        return context;
    }

    function set(patch) {
        var context = get();
        Object.keys(patch).forEach(function (k) { context[k] = patch[k]; });
        try { sessionStorage.setItem(KEY, JSON.stringify(context)); } catch (e) { /* storage unavailable */ }
        document.dispatchEvent(new CustomEvent('em-context-changed', { detail: context }));
    }

    function call(path) {
        return fetch(ApiClient.getUrl(path), { headers: { Authorization: 'MediaBrowser Token="' + ApiClient.accessToken() + '"' } })
            .then(function (r) { return r.ok ? r.json() : null; });
    }

    function load() {
        return Promise.all([call('ExperienceManager/Admin/Audiences'), call('ExperienceManager/Admin/Events')]).then(function (r) {
            cache.audiences = r[0] ? r[0].Audiences : [];
            cache.users = r[0] ? r[0].Users : [];
            cache.events = r[1] ? r[1].Events : [];
            cache.names = {};
            cache.audiences.forEach(function (a) { cache.names['audience:' + a.Id] = a.Name; });
            cache.users.forEach(function (u) { cache.names['user:' + u.Id] = u.Name; });
        });
    }

    function control(parent, labelText, node) {
        var wrap = parent.appendChild(el('label', null, 'em-ctx-control'));
        wrap.appendChild(el('span', labelText));
        wrap.appendChild(node);
        return node;
    }

    // A month of days, those with an event on marked with a dot.
    function drawMonth(box, context) {
        box.textContent = '';
        var programming = window.ExperienceManagerProgramming && window.ExperienceManagerProgramming.pure;
        var parts = context.date.split('-');
        var year = +parts[0];
        var month = +parts[1];
        var head = box.appendChild(el('div', null, 'em-ctx-month-head'));
        function shift(by) {
            var d = new Date(year, month - 1 + by, 1);
            set({ date: dateText(d) });
        }
        head.appendChild(el('button', '‹', 'raised button-alt em-prog-button')).addEventListener('click', function () { shift(-1); });
        head.appendChild(el('span', MONTHS[month - 1] + ' ' + year, 'em-ctx-month-label'));
        head.appendChild(el('button', '›', 'raised button-alt em-prog-button')).addEventListener('click', function () { shift(1); });
        Array.prototype.forEach.call(head.querySelectorAll('button'), function (b) { b.type = 'button'; });
        var grid = box.appendChild(el('div', null, 'em-ctx-days'));
        if (!programming) return;
        programming.monthGrid(year, month).forEach(function (week) {
            week.forEach(function (day) {
                var cell = grid.appendChild(el(day ? 'button' : 'span', day ? String(day) : '', 'em-ctx-day'));
                if (!day) return;
                cell.type = 'button';
                var value = year + '-' + pad(month) + '-' + pad(day);
                cell.setAttribute('data-date', value);
                if (value === context.date) cell.setAttribute('aria-pressed', 'true');
                if (cache.events.some(function (e) { return programming.activeOn(e.Schedule, year, month, day); })) {
                    cell.className += ' em-ctx-has-event';
                    cell.title = 'Events on this day';
                }
                cell.addEventListener('click', function () { set({ date: value }); });
            });
        });
    }

    function drawControls(box, context) {
        var who = control(box, 'Viewing as', el('select'));
        who.name = 'ctx-who';
        who.appendChild(el('option', 'Everyone')).value = 'everyone';
        var audiences = who.appendChild(el('optgroup'));
        audiences.label = 'Audiences';
        cache.audiences.forEach(function (a) { audiences.appendChild(el('option', a.Name)).value = 'audience:' + a.Id; });
        var users = who.appendChild(el('optgroup'));
        users.label = 'Users';
        cache.users.forEach(function (u) { users.appendChild(el('option', u.Name)).value = 'user:' + u.Id; });
        who.value = context.who;
        who.addEventListener('change', function () { set({ who: who.value }); });
        var device = control(box, 'on', el('select'));
        device.name = 'ctx-device';
        DEVICES.forEach(function (d) { device.appendChild(el('option', d.name)).value = d.id; });
        device.value = context.device;
        device.addEventListener('change', function () { set({ device: device.value }); });
        var time = control(box, 'at', el('input'));
        time.type = 'time';
        time.name = 'ctx-time';
        time.value = context.time;
        time.addEventListener('change', function () { if (time.value) set({ time: time.value }); });
        drawMonth(box.appendChild(el('div', null, 'em-ctx-month')), context);
    }

    function drawBar(container) {
        function draw() {
            if (!container.isConnected) return;
            var context = get();
            container.textContent = '';
            var bar = container.appendChild(el('div', null, 'em-ctx'));
            var text = label(context, cache.names, dateText(new Date()));
            var chip = bar.appendChild(el('button', text, 'raised button-alt em-prog-button em-ctx-chip'));
            chip.type = 'button';
            chip.setAttribute('aria-expanded', container.getAttribute('data-open') === 'true' ? 'true' : 'false');
            chip.addEventListener('click', function () {
                container.setAttribute('data-open', container.getAttribute('data-open') === 'true' ? 'false' : 'true');
                draw();
            });
            var sheet = bar.appendChild(el('div', null, 'em-ctx-sheet'));
            drawControls(sheet, context);
        }
        load().then(draw, draw);
        document.addEventListener('em-context-changed', draw);
        document.addEventListener('em-draft-changed', function () { load().then(draw); });
    }

    window.ExperienceManagerContext = { get: get, set: set, drawBar: drawBar, pure: pure, names: function () { return cache.names; } };
})();
