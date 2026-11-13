// Tests for the admin Health and performance page's pure helpers, and the layout editor's health badges.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const h = require('../../src/admin/health.js');
const l = require('../../src/admin/layout.js');

const finding = (Kind, extra) => Object.assign({ Kind, Title: 't', Text: 'x', Where: 'w', Page: 'layout' }, extra);

test('findings are grouped under their headings, in order, leaving out empty ones', () => {
    const groups = h.grouped([finding('slow'), finding('empty-for-some'), finding('source-missing'), finding('empty-for')]);
    assert.deepEqual(groups.map(g => [g.title, g.findings.map(f => f.Kind)]), [
        ['Sources missing', ['source-missing']],
        ['Empty for an audience', ['empty-for-some', 'empty-for']],
        ['Slow sections', ['slow']]
    ]);
});

test('a finding links to the section in its layout, or to its event', () => {
    assert.deepEqual(h.linkOf(finding('empty-for', { AudienceId: 'kids', SectionId: 'picks' })), { page: 'layout', who: 'audience:kids', focus: { section: 'picks' } });
    assert.deepEqual(h.linkOf(finding('source-missing', { SectionId: 'box' })), { page: 'layout', who: 'everyone', focus: { section: 'box' } });
    assert.deepEqual(h.linkOf(finding('starts-soon', { Page: 'programming', EventId: 'xmas' })), { page: 'programming', who: null, focus: { event: 'xmas' } });
});

test('the summary counts the findings', () => {
    assert.equal(h.summary([]), 'Nothing needs attention.');
    assert.equal(h.summary([finding('slow')]), '1 thing needs attention.');
    assert.equal(h.summary([finding('slow'), finding('slow')]), '2 things need attention.');
});

test('a row shows Empty for its own audience only, and Slow anywhere, Latest for each library for any library', () => {
    const findings = [
        finding('empty-for', { AudienceId: 'kids', SectionId: 'picks', Text: 'Empty for Kids' }),
        finding('empty-for-some', { AudienceId: 'kids', SectionId: 'picks' }),
        finding('slow', { SectionId: 'latest-abc', Text: 'Slow: 300 ms at p95' }),
        finding('source-missing', { SectionId: 'picks' })
    ];
    const picks = { Id: 'picks', Type: 'query' };
    assert.deepEqual(l.healthOf(findings, picks, 'kids').map(f => f.Text), ['Empty for Kids']);
    assert.deepEqual(l.healthOf(findings, picks, 'everyone'), []);
    assert.deepEqual(l.healthOf(findings, picks, 'teens'), []);
    assert.deepEqual(l.healthOf(findings, { Id: 'latest', Type: 'latest-libraries' }, 'everyone').map(f => f.Text), ['Slow: 300 ms at p95']);
});
