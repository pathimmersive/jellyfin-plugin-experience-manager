// Tests for the admin Home layout editor's pure helpers.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const l = require('../../src/admin/layout.js');

const row = (id, type, mark = 'everyone', extra = {}) => ({ section: { Id: id, Type: type, ...extra }, mark });

test('the keyboard picks a section up, moves it with the arrows, puts it down or puts it back', () => {
    assert.equal(l.keyAction(' ', false), 'pick');
    assert.equal(l.keyAction('Enter', false), 'pick');
    assert.equal(l.keyAction('ArrowUp', false), null);
    assert.equal(l.keyAction('ArrowUp', true), 'up');
    assert.equal(l.keyAction('ArrowDown', true), 'down');
    assert.equal(l.keyAction(' ', true), 'drop');
    assert.equal(l.keyAction('Escape', true), 'cancel');
    assert.equal(l.keyAction('a', true), null);
});

test('each move is read out with the section and its position', () => {
    assert.equal(l.announce('Next Up', 1, 6), 'Next Up, position 2 of 6');
    assert.match(l.announce('Next Up', 3, 6, 'picked'), /^Next Up picked up, position 4 of 6\. The arrow keys move it/);
    assert.equal(l.announce('Next Up', 1, 6, 'dropped'), 'Next Up put down, position 2 of 6.');
    assert.equal(l.announce('Next Up', 3, 6, 'cancelled'), 'Next Up put back, position 4 of 6.');
});

test('a group stores every row in its order, with its own sections, and removing keeps a server section to restore', () => {
    const rows = [row('featured', 'featured', 'inherited'), row('nextup', 'nextup', 'inherited'), row('cartoons', 'query', 'added', { Title: 'Cartoons' })];
    l.makeOwn(rows[1]);
    rows[1].section.Title = 'Up next';
    assert.deepEqual(l.entries(rows), [
        { Id: 'featured', Mode: 'inherited' },
        { Id: 'nextup', Mode: 'changed', Section: { Id: 'nextup', Type: 'nextup', Title: 'Up next' } },
        { Id: 'cartoons', Mode: 'added', Section: { Id: 'cartoons', Type: 'query', Title: 'Cartoons' } },
    ]);
    const removed = l.remove(rows, 0, true);
    assert.equal(removed[0].mark, 'removed');
    assert.equal(l.remove(removed, 2, true).length, 2);
    assert.equal(l.remove([row('a', 'nextup')], 0, false).length, 0);
    l.useEveryones(rows[1], { Id: 'nextup', Type: 'nextup' });
    assert.deepEqual(rows[1], { section: { Id: 'nextup', Type: 'nextup' }, mark: 'inherited' });
});

test('the banner counts what moved, was added, removed or edited', () => {
    const original = [row('featured', 'featured'), row('mymedia', 'mymedia'), row('nextup', 'nextup')];
    const rows = [row('nextup', 'nextup', 'everyone', { Title: 'Up next' }), row('featured', 'featured'), row('genre-row', 'query')];
    const c = l.changes(original, rows);
    assert.equal(c.text, '4 unsaved changes: 1 moved, 1 added, 1 removed, 1 edited');
    const four = ['a', 'b', 'c', 'd'].map(id => row(id, 'nextup'));
    assert.equal(l.changes(four, l.move(four, 3, 1)).text, '1 unsaved change: 1 moved');
    assert.equal(l.changes(original, original).total, 0);
});

test('a new section gets an id of its own, and Latest for each library is held once', () => {
    const rows = [row('nextup', 'nextup'), row('latest', 'latest-libraries')];
    assert.equal(l.newSection('nextup', rows).Id, 'nextup-2');
    assert.deepEqual(l.newSection('query', rows), { Id: 'genre-row', Type: 'query', Config: { Genres: [], ItemTypes: 'Movie,Series' } });
    assert.equal(l.hasLatestLibraries(rows), true);
    assert.equal(l.hasLatestLibraries([row('latest', 'latest-libraries', 'removed')]), false);
    assert.deepEqual(l.newSection('announcement', rows), { Id: 'announcement', Type: 'announcement', Config: { Text: '', Dismissible: true } });
    assert.deepEqual(l.TYPES.map(t => t.type), ['featured', 'mymedia', 'continue', 'nextup', 'latest-libraries', 'latest', 'query', 'collection', 'announcement']);
    assert.ok(l.TYPES.every(t => t.about.length > 10));
});
