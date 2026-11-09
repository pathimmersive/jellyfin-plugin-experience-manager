'use strict';
// The home client's pure parts. Run with: node --test tests/home/

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const home = require('../../src/home/home.js');

const repo = path.join(__dirname, '..', '..');
const read = file => fs.readFileSync(path.join(repo, file), 'utf8');

test('rows are as tall as the design says, per layout, with room for a focused card under the title', () => {
    const continueRow = { Id: 'continue', Display: { Shape: 'landscape' } };
    const latestRow = { Id: 'latest-x', Display: { Shape: 'portrait' } };
    const myMedia = { Id: 'mymedia', Display: { Shape: 'landscape' } };
    assert.equal(home.rowHeight(home.SIZES.tv, continueRow), 48 + 12 + 166 + 62 + 14);
    assert.equal(home.rowHeight(home.SIZES.web, latestRow), 40 + 3 + 225 + 30 + 14);
    assert.deepEqual(home.cardSize(home.SIZES.phone, myMedia), [264, 148]);
    assert.deepEqual(home.cardSize(home.SIZES.phone, continueRow), [240, 135]);
});

test('the layout comes from jellyfin-web\'s own classes', () => {
    const classes = names => ({ contains: name => names.includes(name) });
    assert.equal(home.layoutOf(classes(['layout-tv'])), 'tv');
    assert.equal(home.layoutOf(classes(['layout-mobile'])), 'phone');
    assert.equal(home.layoutOf(classes(['layout-desktop'])), 'web');
});

test('images are asked for at the size they are drawn, at most twice for dense screens', () => {
    assert.equal(home.imageUrl('/jf', 'abc', 'Primary', 't 1', 160, 3), '/jf/Items/abc/Images/Primary?tag=t%201&maxWidth=320&quality=80');
    assert.equal(home.imageUrl('', 'abc', 'Thumb', 'x', 296, 1), '/Items/abc/Images/Thumb?tag=x&maxWidth=296&quality=80');
});

test('cards pick the image that suits their shape', () => {
    const episode = { Id: 'e', Type: 'Episode', ImageTags: { Primary: 'p' }, ParentBackdropItemId: 's', ParentBackdropImageTags: ['b'] };
    assert.deepEqual(home.cardImage(episode, 'landscape'), ['Primary', 'p', 'e']);
    const film = { Id: 'f', Type: 'Movie', ImageTags: { Primary: 'p', Thumb: 't' }, BackdropImageTags: ['b'] };
    assert.deepEqual(home.cardImage(film, 'landscape'), ['Thumb', 't', 'f']);
    assert.deepEqual(home.cardImage(film, 'portrait'), ['Primary', 'p', 'f']);
    assert.equal(home.cardImage({ Id: 'n', ImageTags: {} }, 'portrait'), null);
    assert.equal(home.blurHashOf({ ImageBlurHashes: { Thumb: { t: 'LEHV6nWB2yk8pyo0adR*.7kCMdnj' } } }, ['Thumb', 't', 'f']), 'LEHV6nWB2yk8pyo0adR*.7kCMdnj');
});

test('captions and the feature\'s meta line read as the design shows them', () => {
    assert.deepEqual(home.captionOf({ Type: 'Episode', SeriesName: 'Show', Name: 'Pilot', ParentIndexNumber: 1, IndexNumber: 2 }), ['Show', 'S1:E2 · Pilot']);
    assert.deepEqual(home.captionOf({ Type: 'Movie', Name: 'Film' }), ['Film']);
    assert.equal(home.metaOf({ ProductionYear: 2024, OfficialRating: 'PG-13', CommunityRating: 7.46, Genres: ['Drama', 'Crime', 'War'], RunTimeTicks: 7500 * 10000000 }),
        '2024 · PG-13 · ★ 7.5 · Drama, Crime · 2h 5m');
});

test('links stay inside jellyfin-web', () => {
    assert.equal(home.detailsHash('abc', 'srv'), '#/details?id=abc&serverId=srv');
    assert.equal(home.libraryHash({ Id: 'm', CollectionType: 'movies' }, 's'), '#/movies?topParentId=m&collectionType=movies&serverId=s');
    assert.equal(home.libraryHash({ Id: 'b', CollectionType: 'books' }, null), '#/list?parentId=b');
});

test('the home settings are overridden in memory and the real values go back on save', () => {
    const real = { Id: 'x', CustomPrefs: { homesection0: 'resume', homesection1: 'none', tvhome: 'x', other: 'kept' } };
    const { prefs, original } = home.overrideHomeSettings(real);
    for (let i = 0; i < 10; i++) assert.equal(prefs.CustomPrefs['homesection' + i], 'none');
    assert.equal(prefs.CustomPrefs.other, 'kept');
    assert.equal(real.CustomPrefs.homesection0, 'resume', 'the original object is untouched');

    // jellyfin-web saves the settings it was given, with one unrelated change
    const outgoing = JSON.parse(JSON.stringify(prefs));
    outgoing.CustomPrefs.other = 'changed';
    const saved = home.restoreHomeSettings(outgoing, original);
    assert.equal(saved.CustomPrefs.homesection0, 'resume');
    assert.equal(saved.CustomPrefs.homesection1, 'none');
    assert.equal('homesection5' in saved.CustomPrefs, false, 'a setting the user never had is not invented');
    assert.equal(saved.CustomPrefs.other, 'changed');

    // a row the user changes on the settings page keeps the new value
    outgoing.CustomPrefs.homesection2 = 'latestmedia';
    assert.equal(home.restoreHomeSettings(outgoing, original).CustomPrefs.homesection2, 'latestmedia');
});

test('only the user settings request is touched', () => {
    assert.equal(home.isUserSettings('/jf/DisplayPreferences/usersettings?userId=u&client=emby'), true);
    assert.equal(home.isUserSettings('/DisplayPreferences/usersettings'), true);
    assert.equal(home.isUserSettings('/DisplayPreferences/home?userId=u'), false);
    assert.equal(home.isUserSettings('/Users/u/Items/Resume'), false);
});

test('BlurHashes decode to their average colour', () => {
    const hash = 'LEHV6nWB2yk8pyo0adR*.7kCMdnj';
    const w = 32, h = 32;
    const pixels = home.decodeBlurHash(hash, w, h);
    assert.equal(pixels.length, w * h * 4);
    const dc = home.decode83(hash.substring(2, 6));
    const average = [0, 1, 2].map(c => {
        let sum = 0;
        for (let p = 0; p < w * h; p++) sum += pixels[p * 4 + c];
        return sum / (w * h);
    });
    const expected = [dc >> 16, (dc >> 8) & 255, dc & 255];
    average.forEach((v, c) => assert.ok(Math.abs(v - expected[c]) < 25, `channel ${c}: ${v} vs ${expected[c]}`));
    assert.throws(() => home.decodeBlurHash('LEHV6nWB2yk8', w, h));
    assert.throws(() => home.decodeBlurHash('not a hash!', w, h));
});

test('the client reads the home screen from the public v1 API only', () => {
    const routes = read('src/home/home.js').match(/\/ExperienceManager\/[^'"?]*/g);
    assert.ok(routes.length > 0);
    for (const route of routes) assert.match(route, /^\/ExperienceManager\/v1\/(Pages|Sections)\//, route);
});

// The oldest LG TVs in use run Chromium 68 (webOS 5). These are the language and platform
// features the client must not use, with the Chromium version that brought each.
const withoutComments = text => text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

test('the script uses nothing newer than ES5 and Chromium 68', () => {
    const code = withoutComments(read('src/home/home.js'));
    const banned = {
        'arrow functions': /=>/,
        'let or const': /\b(let|const)\s/,
        'template strings': /`/,
        classes: /\bclass\s+[A-Za-z]/,
        'spread or rest': /\.\.\./,
        'optional chaining': /\?\.(?![0-9])/,
        'nullish coalescing': /\?\?/,
        'async functions': /\b(async|await)\s/,
        'Array.prototype.flat (69)': /\.flat(Map)?\(/,
        'String.prototype.replaceAll (85)': /\.replaceAll\(/,
        'globalThis (71)': /\bglobalThis\b/,
        'queueMicrotask (71)': /\bqueueMicrotask\b/,
        'structuredClone (98)': /\bstructuredClone\b/,
        'Promise.allSettled (76)': /allSettled/,
        'String.prototype.matchAll (73)': /\.matchAll\(/,
        'Object.fromEntries (73)': /fromEntries/,
        'native lazy images (77)': /\.loading\s*=|loading="lazy"/
    };
    for (const [name, pattern] of Object.entries(banned)) assert.doesNotMatch(code, pattern, name);
});

test('the stylesheet uses nothing newer than Chromium 68', () => {
    const css = withoutComments(read('src/home/home.css'));
    const banned = {
        'gap (flex gap is Chromium 84)': /(^|[\s;{])(row-|column-)?gap\s*:/m,
        'aspect-ratio (88)': /aspect-ratio/,
        'inset (87)': /(^|[\s;{])inset\s*:/m,
        ':focus-visible (86)': /:focus-visible/,
        ':is() and :where() (88)': /:(is|where)\(/,
        'backdrop-filter (76)': /backdrop-filter/
    };
    for (const [name, pattern] of Object.entries(banned)) assert.doesNotMatch(css, pattern, name);
});
