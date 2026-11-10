// Tests for the admin Audiences page's pure helpers.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const a = require('../../src/admin/audiences.js');

const members = names => names.map((n, i) => ({ Id: String(i), Name: n }));

test('a group names its first members and says how many more', () => {
    assert.equal(a.memberLine({ Kind: 'group', Members: [] }), 'No members yet.');
    assert.equal(a.memberLine({ Kind: 'group', Members: members(['kid']) }), '1 member: kid');
    assert.equal(a.memberLine({ Kind: 'group', Members: members(['a', 'b', 'c', 'd', 'e']) }), '5 members: a, b, c and 2 more');
});

test('New users names the accounts that are new now', () => {
    assert.equal(a.memberLine({ Kind: 'new-users', Members: [] }), 'Nobody is new right now.');
    assert.equal(a.memberLine({ Kind: 'new-users', Members: members(['newbie']) }), '1 new now: newbie');
});

test('moving an audience changes only its place', () => {
    assert.deepEqual(a.move(['new-users', 'kids', 'family'], 2, 0), ['family', 'new-users', 'kids']);
});
