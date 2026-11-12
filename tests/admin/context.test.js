// Tests for the admin context bar's pure helpers.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const c = require('../../src/admin/context.js');

test('the bar starts as Everyone, on TV, now', () => {
    assert.deepEqual(c.initial(new Date(2026, 10, 12, 9, 5)), { who: 'everyone', device: 'tv', date: '2026-11-12', time: '09:05' });
});

test('the bar names who, the device and the moment', () => {
    const names = { 'user:u1': 'Kid', 'audience:kids': 'Kids' };
    assert.equal(c.label({ who: 'everyone', device: 'tv', date: '2026-11-12', time: '20:00' }, names, '2026-11-12'), 'Viewing as Everyone · on TV · on Today, 12 Nov, 20:00');
    assert.equal(c.label({ who: 'user:u1', device: 'phone', date: '2026-12-24', time: '08:30' }, names, '2026-11-12'), 'Viewing as Kid · on phone · on 24 Dec, 08:30');
    assert.equal(c.label({ who: 'audience:kids', device: 'web', date: '2026-11-12', time: '08:30' }, names, '2026-11-12'), 'Viewing as Kids · on web · on Today, 12 Nov, 08:30');
});

test('an unknown name falls back to its id, and an unknown device to TV', () => {
    assert.equal(c.label({ who: 'audience:gone', device: 'fridge', date: '2026-11-12', time: '08:30' }, {}, '2026-01-01'), 'Viewing as gone · on TV · on 12 Nov, 08:30');
});

test('the preview takes a user, an audience, or no audience for Everyone', () => {
    assert.deepEqual(c.previewFor({ who: 'user:u1' }), { UserId: 'u1' });
    assert.deepEqual(c.previewFor({ who: 'audience:kids' }), { Audience: 'kids' });
    assert.deepEqual(c.previewFor({ who: 'everyone' }), { Audience: 'everyone' });
    assert.deepEqual(c.previewFor({}), { Audience: 'everyone' });
});

test('ids with a colon survive, and the moment is the date and time', () => {
    assert.deepEqual(c.kindOf({ who: 'audience:a:b' }), { kind: 'audience', id: 'a:b' });
    assert.equal(c.at({ date: '2026-12-24', time: '18:00' }), '2026-12-24T18:00');
});

test('the preview frames are TV, web and phone', () => {
    assert.deepEqual(c.DEVICES.map(d => [d.id, d.width]), [['tv', 960], ['web', 1200], ['phone', 390]]);
});
