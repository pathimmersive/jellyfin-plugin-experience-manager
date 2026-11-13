// The admin page's Operate > Health and performance: every health finding, grouped, each with a link to where it is
// fixed, and the slowest sections. admin.html loads it from /ExperienceManager/admin-health.js; every route it calls
// is for administrators only. The pure helpers at the top are also loaded by the tests under node.
(function () {
    'use strict';

    var GROUPS = [
        { kinds: ['source-missing'], title: 'Sources missing' },
        { kinds: ['empty-for', 'empty-for-some'], title: 'Empty for an audience' },
        { kinds: ['starts-soon'], title: 'Events starting soon' },
        { kinds: ['slow'], title: 'Slow sections' }
    ];

    // The findings under each heading, leaving out the headings with none.
    function grouped(findings) {
        return GROUPS.map(function (g) {
            return { title: g.title, findings: (findings || []).filter(function (f) { return g.kinds.indexOf(f.Kind) >= 0; }) };
        }).filter(function (g) { return g.findings.length; });
    }

    // Where the link goes: the page, who the context bar shows, and the section or event to open.
    function linkOf(finding) {
        if (finding.Page === 'programming') return { page: 'programming', who: null, focus: { event: finding.EventId } };
        return { page: 'layout', who: finding.AudienceId ? 'audience:' + finding.AudienceId : 'everyone', focus: { section: finding.SectionId } };
    }

    function summary(findings) {
        var n = (findings || []).length;
        return n ? n + (n === 1 ? ' thing needs' : ' things need') + ' attention.' : 'Nothing needs attention.';
    }

    var pure = { GROUPS: GROUPS, grouped: grouped, linkOf: linkOf, summary: summary };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = pure;
        return;
    }

    // ---- The page ----

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

    function load(refresh) {
        return fetch(ApiClient.getUrl('ExperienceManager/Admin/Health' + (refresh ? '?refresh=true' : '')), {
            headers: { Authorization: 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
        }).then(function (r) { if (!r.ok) throw new Error('health'); return r.json(); });
    }

    function open(finding) {
        var link = linkOf(finding);
        try { sessionStorage.setItem('em-focus', JSON.stringify(link.focus)); } catch (e) { /* opens without focus */ }
        if (link.who && window.ExperienceManagerContext) window.ExperienceManagerContext.set({ who: link.who });
        var nav = document.querySelector('#experienceManagerPage .em-nav-page[data-page="' + link.page + '"]');
        if (nav) nav.click();
    }

    function draw(report) {
        root.textContent = '';
        var head = root.appendChild(el('div', null, 'em-lay-toolbar'));
        head.appendChild(el('p', summary(report.Findings), 'em-hl-summary'));
        head.appendChild(button('Check again', 'em-hl-refresh', function () { show(true); }));
        root.appendChild(el('p', 'Checked ' + new Date(report.Checked).toLocaleTimeString() + '. Events are checked a week ahead; the draft is what is checked.', 'em-muted'));
        grouped(report.Findings).forEach(function (g) {
            root.appendChild(el('h3', g.title, 'em-hl-group'));
            var list = root.appendChild(el('ul', null, 'em-hl-list'));
            g.findings.forEach(function (f) {
                var item = list.appendChild(el('li', null, 'em-hl-finding'));
                item.setAttribute('data-kind', f.Kind);
                if (f.SectionId) item.setAttribute('data-section', f.SectionId);
                if (f.EventId) item.setAttribute('data-event', f.EventId);
                if (f.AudienceId) item.setAttribute('data-audience', f.AudienceId);
                var text = item.appendChild(el('div', null, 'em-hl-text'));
                text.appendChild(el('strong', f.Title));
                text.appendChild(el('span', ' · ' + f.Text));
                text.appendChild(el('div', f.Where, 'em-muted'));
                item.appendChild(button(f.Page === 'programming' ? 'Open the event' : 'Open the section', 'em-hl-open', function () { open(f); }));
            });
        });
        root.appendChild(el('h3', 'Slowest sections', 'em-hl-group'));
        if (!report.Slowest.length) {
            root.appendChild(el('p', 'Not enough requests yet to judge.', 'em-muted'));
        } else {
            var table = root.appendChild(el('table', null, 'em-hl-speeds'));
            var top = table.appendChild(el('tr'));
            ['Section', 'p95', 'Requests'].forEach(function (h) { top.appendChild(el('th', h)); });
            report.Slowest.forEach(function (s) {
                var row = table.appendChild(el('tr'));
                row.setAttribute('data-section', s.Id);
                row.appendChild(el('td', s.Title || s.Id));
                row.appendChild(el('td', Math.round(s.P95) + ' ms'));
                row.appendChild(el('td', String(s.Requests)));
            });
        }
        root.appendChild(el('p', 'Load times on each app come once the home screen reports them.', 'em-muted'));
    }

    function show(refresh) {
        var status = root.appendChild(el('p', 'Checking…', 'em-muted'));
        load(refresh).then(draw, function () { status.textContent = 'The checks could not be run. Reload the page to try again.'; });
    }

    window.ExperienceManagerHealth = {
        draw: function (panel) {
            root = panel.appendChild(el('div', null, 'em-hl'));
            show(false);
        }
    };
})();
