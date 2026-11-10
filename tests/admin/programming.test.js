// Tests for the admin Programming page's pure helpers: what the calendar draws and how it words events.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const p = require('../../src/admin/programming.js');

const yearly = (start, end) => ({ Kind: 'yearly', Start: start, End: end });

test('a yearly event across the new year is drawn as two runs in the year', () => {
    assert.deepEqual(p.spans(yearly('12-20', '01-02'), 2026), [{ start: 0, end: 1 }, { start: 353, end: 364 }]);
    assert.deepEqual(p.spans(yearly('12-01', '12-26'), 2026), [{ start: 334, end: 359 }]);
});

test('a once event is clipped to the year and ends the day before when it ends at midnight', () => {
    const eve = { Kind: 'once', Start: '2026-12-24T18:00', End: '2026-12-25T00:00' };
    assert.deepEqual(p.spans(eve, 2026), [{ start: 357, end: 357 }]);
    assert.deepEqual(p.spans(eve, 2027), []);
    const long = { Kind: 'once', Start: '2026-12-30T00:00', End: '2027-01-02T12:00' };
    assert.deepEqual(p.spans(long, 2026), [{ start: 363, end: 364 }]);
    assert.deepEqual(p.spans(long, 2027), [{ start: 0, end: 1 }]);
});

test('a weekly event spans the year, marked weekly, and is on only its days in the month view', () => {
    const night = { Kind: 'weekly', Days: ['Friday'], From: '18:00', To: '23:00' };
    assert.deepEqual(p.spans(night, 2026), [{ start: 0, end: 364, weekly: true }]);
    assert.equal(p.activeOn(night, 2026, 11, 13), true);
    assert.equal(p.activeOn(night, 2026, 11, 14), false);
    assert.equal(p.activeOn(yearly('12-20', '01-02'), 2027, 1, 2), true);
    assert.equal(p.activeOn(yearly('12-20', '01-02'), 2027, 1, 3), false);
});

test('the year has one lane for Featured, then one per section an event changes, in the order first changed', () => {
    const halloween = { Id: 'h', Changes: [{ Kind: 'insert', Section: { Id: 'spooky', Type: 'query', Title: 'Spooky Season' } }, { Kind: 'hide', Target: 'mymedia' }] };
    const night = { Id: 'n', Changes: [{ Kind: 'featured', Section: { Id: 'x', Type: 'collection' } }, { Kind: 'hide', Target: 'mymedia' }] };
    const lanes = p.lanes([halloween, night], { mymedia: 'My Media' });
    assert.deepEqual(lanes.map(l => [l.id, l.title, l.events.map(e => e.Id).join()]), [
        ['featured', 'Featured', 'n'],
        ['spooky', 'Spooky Season', 'h'],
        ['mymedia', 'My Media', 'h,n'],
    ]);
});

test('a month is laid out Sunday first, padded to whole weeks', () => {
    const november = p.monthGrid(2026, 11);
    assert.deepEqual(november[0], [1, 2, 3, 4, 5, 6, 7]);
    assert.equal(november.length, 5);
    assert.deepEqual(p.monthGrid(2026, 12)[0], [null, null, 1, 2, 3, 4, 5]);
});

test('the templates are the seven fixed-date holidays, names and yearly dates only', () => {
    assert.deepEqual(p.TEMPLATES.map(t => t.name), ["New Year's Day", "Valentine's Day", "St Patrick's Day", 'Independence Day', 'Halloween', 'Christmas', "New Year's Eve"]);
    for (const t of p.TEMPLATES) assert.match(`${t.start} ${t.end}`, /^\d{2}-\d{2} \d{2}-\d{2}$/);
});

test('schedules and changes read as plain sentences', () => {
    assert.equal(p.describeSchedule(yearly('12-01', '12-26')), 'Every year, 1 Dec to 26 Dec');
    assert.equal(p.describeSchedule(yearly('02-14', '02-14')), 'Every year on 14 Feb');
    assert.equal(p.describeSchedule({ Kind: 'weekly', Days: ['friday', 'Monday'], From: '18:00', To: '23:00' }), 'Every week on Mondays, Fridays, 18:00 to 23:00');
    assert.equal(p.describeSchedule({ Kind: 'once', Start: '2026-12-24T18:00', End: '2026-12-25T00:00' }), '24 Dec 2026 18:00 to 25 Dec 2026 00:00');
    const titles = { nextup: 'Next Up', box: 'Holiday Picks' };
    assert.equal(p.describeChange({ Kind: 'hide', Target: 'nextup' }, titles), 'Hide Next Up');
    assert.equal(p.describeChange({ Kind: 'featured', Section: { Type: 'collection', Config: { Collection: 'box' } } }, titles), 'Featured: Holiday Picks');
    assert.equal(p.describeChange({ Kind: 'insert', After: 'nextup', Section: { Type: 'query', Title: 'Spooky Season', Config: { Genres: ['Horror'] } } }, titles), 'Add Spooky Season (Horror) after Next Up');
});

test('dragging an event to another place moves only it, and Latest for each library becomes one choice per library', () => {
    assert.deepEqual(p.move(['a', 'b', 'c'], 2, 0), ['c', 'a', 'b']);
    assert.deepEqual(p.move(['a', 'b', 'c'], 0, 1), ['b', 'a', 'c']);
    const choices = p.sectionChoices(
        [{ Id: 'featured', Type: 'featured' }, { Id: 'nextup', Type: 'nextup', Title: 'Up next' }, { Id: 'latest', Type: 'latest-libraries' }],
        [{ Name: 'Movies', ItemId: 'F137A2DD-21BB-C1B9-9AA5-C0F6BF02A805', CollectionType: 'movies' }, { Name: 'Music', ItemId: 'x', CollectionType: 'music' }]);
    assert.deepEqual(choices, [
        { id: 'featured', title: 'Featured' },
        { id: 'nextup', title: 'Up next' },
        { id: 'latest-f137a2dd21bbc1b99aa5c0f6bf02a805', title: 'Latest Movies' },
    ]);
    assert.equal(p.slug("St Patrick's Day!"), 'st-patrick-s-day');
});
