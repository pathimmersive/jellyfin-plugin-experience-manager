// The admin page's Audiences page: audiences in precedence order, their members, and New users' days.
// admin.html loads it from /ExperienceManager/admin-audiences.js; every route it calls is for administrators
// only. The pure helpers at the top are also loaded by the tests under node.
(function () {
    'use strict';

    // How many members a row names before it says how many more there are.
    var PREVIEW = 3;

    function memberLine(audience) {
        var names = audience.Members.map(function (m) { return m.Name; });
        if (audience.Kind === 'new-users') {
            return names.length ? names.length + ' new now: ' + preview(names) : 'Nobody is new right now.';
        }
        if (!names.length) return 'No members yet.';
        return names.length + (names.length === 1 ? ' member: ' : ' members: ') + preview(names);
    }

    function preview(names) {
        return names.slice(0, PREVIEW).join(', ') + (names.length > PREVIEW ? ' and ' + (names.length - PREVIEW) + ' more' : '');
    }

    function move(list, from, to) {
        var copy = list.slice();
        var item = copy.splice(from, 1)[0];
        copy.splice(to, 0, item);
        return copy;
    }

    var pure = { memberLine: memberLine, move: move, PREVIEW: PREVIEW };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = pure;
        return;
    }

    // ---- The page ----

    var BASE = 'ExperienceManager/Admin/Audiences';
    var state = { audiences: [], users: [], editing: null, error: '', message: '' };
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

    // Every change answers with the whole list, so the page redraws from what the server now holds.
    function apply(request, done) {
        return request.then(function (r) {
            if (r.ok) {
                state.audiences = r.body.Audiences;
                state.users = r.body.Users;
                state.error = '';
                if (done) done();
            } else {
                state.error = (r.body && r.body.Error) || 'The change could not be saved (' + r.status + ').';
            }
            draw();
        });
    }

    function draw() {
        if (!root) return;
        root.textContent = '';
        root.appendChild(el('p', 'People see the Home layout of the first audience they are in, in this order. Events can be for an audience too.'));
        if (state.error && !state.editing) root.appendChild(el('p', state.error, 'em-aud-error')).setAttribute('role', 'alert');
        if (state.message) root.appendChild(el('p', state.message, 'em-muted'));
        root.appendChild(button('New audience', 'em-aud-new', function () { state.editing = { Id: '', Name: '', Members: [] }; state.error = ''; draw(); }));
        drawList();
        if (state.editing) drawForm();
    }

    function drawList() {
        var list = root.appendChild(el('ol', null, 'em-aud-list'));
        var ids = state.audiences.map(function (a) { return a.Id; });
        state.audiences.forEach(function (a, index) {
            var row = list.appendChild(el('li', null, 'em-aud-row'));
            row.setAttribute('data-audience', a.Id);
            row.draggable = true;
            row.addEventListener('dragstart', function (ev) { ev.dataTransfer.setData('text/plain', String(index)); ev.dataTransfer.effectAllowed = 'move'; });
            row.addEventListener('dragover', function (ev) { ev.preventDefault(); ev.dataTransfer.dropEffect = 'move'; });
            row.addEventListener('drop', function (ev) {
                ev.preventDefault();
                var from = parseInt(ev.dataTransfer.getData('text/plain'), 10);
                if (!isNaN(from) && from !== index) apply(call('PUT', BASE + '/Order', { Ids: move(ids, from, index) }));
            });
            row.appendChild(el('span', '⠿', 'em-prog-handle')).setAttribute('aria-hidden', 'true');
            var text = row.appendChild(el('div', null, 'em-aud-text'));
            var name = text.appendChild(el('div', a.Name, 'em-aud-name'));
            if (a.HasLayout) name.appendChild(el('span', ' Own Home layout', 'em-aud-badge'));
            text.appendChild(el('div', a.Kind === 'new-users' ? 'Built in: accounts created in their first days' : 'Hand-picked', 'em-aud-kind'));
            text.appendChild(el('div', memberLine(a), 'em-aud-members'));
            if (a.Kind === 'new-users') {
                var label = row.appendChild(el('label', null, 'em-prog-switch'));
                label.appendChild(el('span', 'Days'));
                var days = label.appendChild(el('input', null, 'em-aud-days'));
                days.type = 'number';
                days.min = 1;
                days.max = 3650;
                days.value = a.Days;
                days.name = 'days';
                days.addEventListener('change', function () {
                    apply(call('PUT', BASE + '/' + encodeURIComponent(a.Id), { Days: parseInt(days.value, 10) }), function () { state.message = 'New users now counts ' + days.value + ' days.'; });
                });
            }
            var up = row.appendChild(button('↑', 'em-aud-up', function () { apply(call('PUT', BASE + '/Order', { Ids: move(ids, index, index - 1) })); }));
            up.setAttribute('aria-label', 'Move ' + a.Name + ' up');
            up.disabled = index === 0;
            var down = row.appendChild(button('↓', 'em-aud-down', function () { apply(call('PUT', BASE + '/Order', { Ids: move(ids, index, index + 1) })); }));
            down.setAttribute('aria-label', 'Move ' + a.Name + ' down');
            down.disabled = index === state.audiences.length - 1;
            if (a.Kind !== 'new-users') {
                row.appendChild(button('Edit', 'em-aud-edit', function () {
                    state.editing = { Id: a.Id, Name: a.Name, Members: a.Members.map(function (m) { return m.Id; }) };
                    state.error = '';
                    draw();
                }));
                row.appendChild(button('Delete', 'em-aud-delete', function () { apply(call('DELETE', BASE + '/' + encodeURIComponent(a.Id))); }));
            }
        });
    }

    function drawForm() {
        var a = state.editing;
        var form = root.appendChild(el('form', null, 'em-aud-form'));
        form.addEventListener('submit', function (x) { x.preventDefault(); });
        form.appendChild(el('h3', a.Id ? 'Edit ' + a.Name : 'New audience'));
        var label = form.appendChild(el('label', null, 'em-prog-field'));
        label.appendChild(el('span', 'Name'));
        var name = label.appendChild(el('input'));
        name.type = 'text';
        name.name = 'name';
        name.value = a.Name;
        name.addEventListener('input', function () { a.Name = name.value; });
        form.appendChild(el('p', 'Members', 'em-aud-name'));
        var users = form.appendChild(el('div', null, 'em-aud-users'));
        state.users.forEach(function (u) {
            var box = users.appendChild(el('label'));
            var check = box.appendChild(el('input'));
            check.type = 'checkbox';
            check.value = u.Id;
            check.setAttribute('data-user', u.Name);
            check.checked = a.Members.indexOf(u.Id) >= 0;
            check.addEventListener('change', function () {
                a.Members = a.Members.filter(function (id) { return id !== u.Id; });
                if (check.checked) a.Members.push(u.Id);
            });
            box.appendChild(el('span', ' ' + u.Name));
        });
        if (state.error) form.appendChild(el('p', state.error, 'em-aud-error')).setAttribute('role', 'alert');
        var actions = form.appendChild(el('div', null, 'em-prog-actions'));
        actions.appendChild(button('Save', 'em-aud-save', function () {
            var body = { Name: a.Name, Members: a.Members };
            var request = a.Id ? call('PUT', BASE + '/' + encodeURIComponent(a.Id), body) : call('POST', BASE, body);
            apply(request, function () { state.message = 'Saved ' + a.Name + '.'; state.editing = null; });
        }));
        actions.appendChild(button('Cancel', 'em-aud-cancel', function () { state.editing = null; state.error = ''; draw(); }));
        name.focus();
    }

    window.ExperienceManagerAudiences = {
        draw: function (panel) {
            root = panel.appendChild(el('div', null, 'em-aud'));
            state.editing = null;
            state.message = '';
            var status = root.appendChild(el('p', 'Loading…', 'em-muted'));
            call('GET', BASE).then(function (r) {
                if (!r.ok) throw new Error('audiences');
                status.remove();
                state.audiences = r.body.Audiences;
                state.users = r.body.Users;
                draw();
            }).then(null, function () {
                status.textContent = 'The audiences could not be loaded. Reload the page to try again.';
            });
        }
    };
})();
