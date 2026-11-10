// Tests for the admin Content rules page's pure helpers, and the layout editor's opt-outs.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const r = require('../../src/admin/rules.js');
const l = require('../../src/admin/layout.js');

test('the page lists the six rules, and the layout editor offers the same ones to opt out of', () => {
    assert.deepEqual(r.RULES.map(x => x.id), ['tags', 'ceiling', 'feature-art', 'poster', 'versions', 'continue-cleanup']);
    assert.deepEqual(l.RULES.map(x => x.id), r.RULES.map(x => x.id));
    assert.deepEqual(l.RULES.map(x => x.name), r.RULES.map(x => x.name));
});

test('each rule says how many sections opted out', () => {
    assert.equal(r.optOutLine(0), 'Every section follows it.');
    assert.equal(r.optOutLine(1), '1 section opted out.');
    assert.equal(r.optOutLine(3), '3 sections opted out.');
});

test('the form becomes the rules the server takes, with empty ceilings and numbers left out', () => {
    const rules = r.toRules({ excludeTags: true, excludedTags: ['Hidden'], ceilingOn: true, everyoneCeiling: '', audienceCeilings: { kids: 'PG', teens: '' }, featureArt: true, poster: true, versions: false, continueCleanup: true, cleanupDays: '30', cleanupPercent: '' });
    assert.deepEqual(rules, { ExcludeTags: true, ExcludedTags: ['Hidden'], CeilingOn: true, EveryoneCeiling: null, AudienceCeilings: { kids: 'PG' }, FeatureArt: true, RequireLogo: false, Poster: true, Versions: false, ContinueCleanup: true, CleanupDays: 30, CleanupPercent: null });
});

test('unticking a rule in a section adds it to the section\'s opt-outs, ticking it takes it away', () => {
    assert.deepEqual(l.rulesOff(undefined, 'poster', false), ['poster']);
    assert.deepEqual(l.rulesOff(['poster', 'tags'], 'poster', true), ['tags']);
    assert.deepEqual(l.rulesOff(['tags'], 'tags', false), ['tags']);
});
