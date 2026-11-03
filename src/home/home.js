/* Experience Manager home client.
   Loaded into jellyfin-web by the plugin as index.html is served. It draws the Home tab from the
   plugin's public home API in place of jellyfin-web's own home rows, and falls back to them when
   the API fails.

   Plain ES5 with no build step: the oldest LG TVs in use run Chromium 68 (webOS 5). No arrow
   functions, classes, template strings, let or const, and no browser feature newer than Chromium 68.

   Under Node (the tests) it only exports its pure parts; in a browser it starts. */
(function (root) {
    'use strict';

    var MARKS = ['em-route', 'em-skeleton', 'em-hero', 'em-first-row', 'em-last-row'];
    var HOME_SETTINGS = 10;
    var ROTATE_MS = 8000;
    var SETTINGS_WAIT_MS = 1000;
    var INLINE_SECTIONS = 4;
    var FALLBACK_FLAG = 'em-home-fallback';
    var BOOT_QUIET_MS = 250;
    var BOOT_STATUS_MS = 2500;
    var BOOT_SLOW_MS = 8000;
    var BOOT_MS = 20000;
    // Where jellyfin-web puts Home's top edge, per layout, until the page is there to measure.
    var BOOT_TOP = { tv: 134, web: 48, phone: 48 };

    // Sizes in px per layout, from the design's Platforms and sizes table. room: the space a focused
    // card's ring (and, on a TV, its growth) needs around it inside its row; clear: the space added
    // below a row's title so that ring stays clear of the title's text.
    var SIZES = {
        tv: {
            feature: 372, pad: 42, titleSize: 30, titleBlock: 48, gap: 16, rowGap: 14, ring: 10, room: 18, clear: 12,
            landscape: [296, 166], tile: [296, 166], portrait: [160, 240],
            caption: [34, 62], captionSize: [20, 16], logo: 52, overview: [20, 640], button: [20, 52], message: 64
        },
        web: {
            feature: 460, pad: 42, titleSize: 22, titleBlock: 40, gap: 10, rowGap: 14, ring: 6, room: 8, clear: 3,
            landscape: [278, 156], tile: [278, 156], portrait: [150, 225],
            caption: [30, 52], captionSize: [15, 13], logo: 48, overview: [16, 560], button: [15, 42], message: 56
        },
        phone: {
            feature: 380, pad: 13, titleSize: 21, titleBlock: 38, gap: 8, rowGap: 12, ring: 4, room: 6, clear: 2,
            landscape: [240, 135], tile: [264, 148], portrait: [118, 177],
            caption: [28, 48], captionSize: [14, 12], logo: 34, overview: [14, 360], button: [15, 44], message: 52
        }
    };

    // ---------------------------------------------------------------------------------------------
    // Pure parts, tested under Node.

    function layoutOf(classList) {
        if (classList.contains('layout-tv')) return 'tv';
        if (classList.contains('layout-mobile')) return 'phone';
        return 'web';
    }

    // The card size for a section: My Media's library tiles have their own size.
    function cardSize(sizes, section) {
        if (section.Id === 'mymedia') return sizes.tile;
        return section.Display.Shape === 'portrait' ? sizes.portrait : sizes.landscape;
    }

    // Episodes show the series and the episode on two lines; everything else one line.
    function captionLines(section) {
        return section.Id === 'continue' || section.Id === 'nextup' ? 2 : 1;
    }

    function rowHeight(sizes, section) {
        return sizes.titleBlock + sizes.clear + cardSize(sizes, section)[1] + sizes.caption[captionLines(section) - 1] + sizes.rowGap;
    }

    function imageUrl(base, itemId, type, tag, width, dpr) {
        var scale = Math.min(dpr || 1, 2);
        return base + '/Items/' + itemId + '/Images/' + type + '?tag=' + encodeURIComponent(tag)
            + '&maxWidth=' + Math.round(width * scale) + '&quality=80';
    }

    // [type, tag, itemId] of the image a card shows, or null.
    function cardImage(item, shape) {
        var tags = item.ImageTags || {};
        if (shape === 'landscape') {
            if (item.Type === 'Episode' && tags.Primary) return ['Primary', tags.Primary, item.Id];
            if (tags.Thumb) return ['Thumb', tags.Thumb, item.Id];
            if (item.BackdropImageTags && item.BackdropImageTags.length) return ['Backdrop', item.BackdropImageTags[0], item.Id];
            if (item.ParentThumbItemId && item.ParentThumbImageTag) return ['Thumb', item.ParentThumbImageTag, item.ParentThumbItemId];
            if (item.ParentBackdropImageTags && item.ParentBackdropImageTags.length) return ['Backdrop', item.ParentBackdropImageTags[0], item.ParentBackdropItemId];
        }
        if (tags.Primary) return ['Primary', tags.Primary, item.Id];
        return null;
    }

    function blurHashOf(item, image) {
        var hashes = item.ImageBlurHashes && item.ImageBlurHashes[image[0]];
        return (hashes && hashes[image[1]]) || null;
    }

    function captionOf(item) {
        if (item.Type === 'Episode') {
            var number = item.ParentIndexNumber != null && item.IndexNumber != null
                ? 'S' + item.ParentIndexNumber + ':E' + item.IndexNumber + ' · ' : '';
            return [item.SeriesName || item.Name, number + item.Name];
        }
        return [item.Name];
    }

    function runtime(ticks) {
        if (!ticks) return '';
        var minutes = Math.round(ticks / 600000000);
        var h = Math.floor(minutes / 60);
        var m = minutes % 60;
        return h ? h + 'h ' + m + 'm' : m + 'm';
    }

    function metaOf(item) {
        var parts = [];
        if (item.ProductionYear) parts.push(String(item.ProductionYear));
        if (item.OfficialRating) parts.push(item.OfficialRating);
        if (item.CommunityRating) parts.push('★ ' + (Math.round(item.CommunityRating * 10) / 10));
        if (item.Genres && item.Genres.length) parts.push(item.Genres.slice(0, 2).join(', '));
        if (item.RunTimeTicks) parts.push(runtime(item.RunTimeTicks));
        return parts.join(' · ');
    }

    function detailsHash(itemId, serverId) {
        return '#/details?id=' + itemId + (serverId ? '&serverId=' + serverId : '');
    }

    function libraryHash(item, serverId) {
        var server = serverId ? '&serverId=' + serverId : '';
        if (item.CollectionType === 'movies') return '#/movies?topParentId=' + item.Id + '&collectionType=movies' + server;
        if (item.CollectionType === 'tvshows') return '#/tv?topParentId=' + item.Id + '&collectionType=tvshows' + server;
        if (item.CollectionType === 'music') return '#/music?topParentId=' + item.Id + '&collectionType=music' + server;
        return '#/list?parentId=' + item.Id + server;
    }

    // The home settings jellyfin-web reads, with every home row set to none, and the values they had.
    function overrideHomeSettings(prefs) {
        var copy = JSON.parse(JSON.stringify(prefs || {}));
        copy.CustomPrefs = copy.CustomPrefs || {};
        var original = {};
        for (var i = 0; i < HOME_SETTINGS; i++) {
            var key = 'homesection' + i;
            original[key] = Object.prototype.hasOwnProperty.call(copy.CustomPrefs, key) ? copy.CustomPrefs[key] : null;
            copy.CustomPrefs[key] = 'none';
        }
        return { prefs: copy, original: original };
    }

    // Settings on their way to the server: any home row still 'none' from the override gets its real value back.
    function restoreHomeSettings(prefs, original) {
        var copy = JSON.parse(JSON.stringify(prefs || {}));
        if (!copy.CustomPrefs || !original) return copy;
        for (var i = 0; i < HOME_SETTINGS; i++) {
            var key = 'homesection' + i;
            if (copy.CustomPrefs[key] === 'none' && original[key] !== 'none') {
                if (original[key] === null) delete copy.CustomPrefs[key];
                else copy.CustomPrefs[key] = original[key];
            }
        }
        return copy;
    }

    function isUserSettings(url) {
        return /\/DisplayPreferences\/usersettings(\?|$)/i.test(url);
    }

    // BlurHash, decoded from its published description (https://github.com/woltapp/blurhash).
    var B83 = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~';

    function decode83(text) {
        var value = 0;
        for (var i = 0; i < text.length; i++) {
            var digit = B83.indexOf(text.charAt(i));
            if (digit < 0) throw new Error('not a BlurHash');
            value = value * 83 + digit;
        }
        return value;
    }

    function toLinear(v) {
        var c = v / 255;
        return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    }

    function toSrgb(v) {
        var c = Math.max(0, Math.min(1, v));
        return c <= 0.0031308 ? Math.round(c * 12.92 * 255 + 0.5) : Math.round((1.055 * Math.pow(c, 1 / 2.4) - 0.055) * 255 + 0.5);
    }

    function signPow(v, e) {
        return (v < 0 ? -1 : 1) * Math.pow(Math.abs(v), e);
    }

    // RGBA pixels of a BlurHash at width x height.
    function decodeBlurHash(hash, width, height) {
        if (!hash || hash.length < 6) throw new Error('not a BlurHash');
        var size = decode83(hash.charAt(0));
        var ny = Math.floor(size / 9) + 1;
        var nx = (size % 9) + 1;
        if (hash.length !== 4 + 2 * nx * ny) throw new Error('not a BlurHash');
        var max = (decode83(hash.charAt(1)) + 1) / 166;
        var colors = [];
        for (var c = 0; c < nx * ny; c++) {
            if (c === 0) {
                var dc = decode83(hash.substring(2, 6));
                colors.push([toLinear(dc >> 16), toLinear((dc >> 8) & 255), toLinear(dc & 255)]);
            } else {
                var ac = decode83(hash.substring(4 + c * 2, 6 + c * 2));
                colors.push([
                    signPow((Math.floor(ac / 361) - 9) / 9, 2) * max,
                    signPow(((Math.floor(ac / 19) % 19) - 9) / 9, 2) * max,
                    signPow(((ac % 19) - 9) / 9, 2) * max
                ]);
            }
        }
        var pixels = new Uint8ClampedArray(width * height * 4);
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var r = 0, g = 0, b = 0;
                for (var j = 0; j < ny; j++) {
                    for (var i = 0; i < nx; i++) {
                        var basis = Math.cos(Math.PI * x * i / width) * Math.cos(Math.PI * y * j / height);
                        var color = colors[i + j * nx];
                        r += color[0] * basis;
                        g += color[1] * basis;
                        b += color[2] * basis;
                    }
                }
                var p = 4 * (x + y * width);
                pixels[p] = toSrgb(r);
                pixels[p + 1] = toSrgb(g);
                pixels[p + 2] = toSrgb(b);
                pixels[p + 3] = 255;
            }
        }
        return pixels;
    }

    var pure = {
        SIZES: SIZES, MARKS: MARKS, layoutOf: layoutOf, cardSize: cardSize, captionLines: captionLines, rowHeight: rowHeight,
        imageUrl: imageUrl, cardImage: cardImage, blurHashOf: blurHashOf, captionOf: captionOf, metaOf: metaOf, runtime: runtime,
        detailsHash: detailsHash, libraryHash: libraryHash, overrideHomeSettings: overrideHomeSettings,
        restoreHomeSettings: restoreHomeSettings, isUserSettings: isUserSettings, decodeBlurHash: decodeBlurHash, decode83: decode83
    };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = pure;
        return;
    }

    start(root);

    // ---------------------------------------------------------------------------------------------
    // The browser.

    function start(win) {
        var doc = win.document;
        var base = win.location.pathname.replace(/\/web(\/.*)?$/, '');
        var state = {
            page: null,          // the current page call, a promise
            pageFailed: false,
            overridden: false,   // whether jellyfin-web was given the overridden home settings
            original: {},        // each user's real home settings, by user id
            root: null,
            visit: 0,
            rotation: null,
            boot: null           // the loading screen shown while jellyfin-web starts on Home
        };

        function fallbackActive() {
            try { return win.sessionStorage.getItem(FALLBACK_FLAG) === '1'; } catch (e) { return false; }
        }

        function mark(name) {
            try { if (!win.performance.getEntriesByName(name).length) win.performance.mark(name); } catch (e) { /* no marks */ }
        }

        function clearMarks() {
            try { for (var i = 0; i < MARKS.length; i++) win.performance.clearMarks(MARKS[i]); } catch (e) { /* no marks */ }
        }

        // The account signed in to this server, from jellyfin-web's own saved credentials.
        function credentials() {
            try {
                var saved = JSON.parse(win.localStorage.getItem('jellyfin_credentials') || '{}');
                var servers = saved.Servers || [];
                var here = win.location.origin + base;
                var best = null;
                for (var i = 0; i < servers.length; i++) {
                    var s = servers[i];
                    if (!s.AccessToken || !s.UserId) continue;
                    if (s.ManualAddress === here || s.LocalAddress === here || s.RemoteAddress === here) return s;
                    if (!best || (s.DateLastAccessed || 0) > (best.DateLastAccessed || 0)) best = s;
                }
                return best;
            } catch (e) {
                return null;
            }
        }

        function api(path, options) {
            var cred = credentials();
            if (!cred) return Promise.reject(new Error('not signed in'));
            var init = options || {};
            init.headers = { Authorization: 'MediaBrowser Token="' + cred.AccessToken + '"' };
            return realFetch.call(win, base + path, init).then(function (response) {
                if (!response.ok) throw new Error(path + ': ' + response.status);
                return response.status === 204 ? null : response.json();
            });
        }

        function loadPage() {
            if (!state.page) {
                state.pageFailed = false;
                state.page = api('/ExperienceManager/v1/Pages/home?include=items&count=' + INLINE_SECTIONS);
                state.page.then(null, function () { state.pageFailed = true; });
            }
            return state.page;
        }

        // --- jellyfin-web's own home rows ------------------------------------------------------
        // jellyfin-web reads the user's home settings both through fetch (its older API client)
        // and through XMLHttpRequest (its SDK); both are covered.

        // Waits briefly for the page call: if the API answers, jellyfin-web gets no home rows to load.
        // If it has not answered in time, the override goes ahead, and a later failure reloads once
        // without it.
        function apiHealthy() {
            if (fallbackActive() || !credentials()) return Promise.resolve(false);
            var ready = loadPage();
            return new Promise(function (resolve) {
                ready.then(function () { resolve(true); }, function () { resolve(false); });
                win.setTimeout(function () { resolve(!state.pageFailed); }, SETTINGS_WAIT_MS);
            });
        }

        function userOf(url) {
            var match = /[?&]userId=([^&]+)/i.exec(url);
            var cred = credentials();
            return match ? decodeURIComponent(match[1]).replace(/-/g, '').toLowerCase() : (cred ? String(cred.UserId).replace(/-/g, '').toLowerCase() : '');
        }

        function overrideText(url, text) {
            try {
                var changed = overrideHomeSettings(JSON.parse(text));
                state.original[userOf(url)] = changed.original;
                state.overridden = true;
                return JSON.stringify(changed.prefs);
            } catch (e) {
                return text;
            }
        }

        function restoreText(url, text) {
            var original = state.original[userOf(url)];
            if (!original) return text;
            try { return JSON.stringify(restoreHomeSettings(JSON.parse(text), original)); } catch (e) { return text; }
        }

        var realFetch = win.fetch;
        if (realFetch && !fallbackActive()) {
            win.fetch = function (input, init) {
                var url = typeof input === 'string' ? input : (input && input.url) || '';
                var method = String((init && init.method) || (input && input.method) || 'GET').toUpperCase();
                if (!isUserSettings(url)) return realFetch.apply(win, arguments);
                if (method === 'GET') {
                    var sent = realFetch.apply(win, arguments);
                    return Promise.all([apiHealthy(), sent]).then(function (results) {
                        var response = results[1];
                        if (!results[0] || !response.ok) return response;
                        return response.clone().text().then(function (text) {
                            var headers = new win.Headers(response.headers);
                            headers.delete('content-length');
                            return new win.Response(overrideText(url, text), { status: response.status, statusText: response.statusText, headers: headers });
                        });
                    });
                }
                if (method === 'POST' && init && typeof init.body === 'string') {
                    var copy = {};
                    for (var key in init) if (Object.prototype.hasOwnProperty.call(init, key)) copy[key] = init[key];
                    copy.body = restoreText(url, init.body);
                    return realFetch.call(win, input, copy);
                }
                return realFetch.apply(win, arguments);
            };
        }

        var XHR = win.XMLHttpRequest;
        if (XHR && !fallbackActive()) {
            var proto = XHR.prototype;
            var realOpen = proto.open;
            var realSend = proto.send;
            var textOf = Object.getOwnPropertyDescriptor(proto, 'responseText');
            var responseOf = Object.getOwnPropertyDescriptor(proto, 'response');
            proto.open = function (method, url) {
                this.emUrl = String(url);
                this.emSettings = isUserSettings(this.emUrl) ? String(method).toUpperCase() : null;
                return realOpen.apply(this, arguments);
            };
            proto.send = function (body) {
                var xhr = this;
                var args = arguments;
                if (xhr.emSettings === 'POST' && typeof body === 'string') return realSend.call(xhr, restoreText(xhr.emUrl, body));
                if (xhr.emSettings !== 'GET' || !textOf || !responseOf) return realSend.apply(xhr, args);
                apiHealthy().then(function (ok) {
                    if (ok) overrideXhr(xhr);
                    realSend.apply(xhr, args);
                });
            };
        }

        // The response jellyfin-web reads from this request carries the overridden settings.
        function overrideXhr(xhr) {
            var changed = null;
            var done = function () { return xhr.readyState === 4 && xhr.status >= 200 && xhr.status < 300; };
            var text = function (raw) {
                if (changed === null) changed = overrideText(xhr.emUrl, raw);
                return changed;
            };
            Object.defineProperty(xhr, 'responseText', {
                configurable: true,
                get: function () {
                    var raw = textOf.get.call(xhr);
                    return done() ? text(raw) : raw;
                }
            });
            Object.defineProperty(xhr, 'response', {
                configurable: true,
                get: function () {
                    var raw = responseOf.get.call(xhr);
                    if (!done()) return raw;
                    if (typeof raw === 'string') return text(raw);
                    if (raw && typeof raw === 'object' && xhr.responseType === 'json') return JSON.parse(text(JSON.stringify(raw)));
                    return raw;
                }
            });
        }

        // The API failed after jellyfin-web was told to show no rows: reload once without the override.
        function fallBack() {
            stopRotation();
            hideBoot(false);
            if (state.root && state.root.parentNode) state.root.parentNode.removeChild(state.root);
            state.root = null;
            doc.documentElement.classList.remove('em-active');
            if (state.overridden) {
                try { win.sessionStorage.setItem(FALLBACK_FLAG, '1'); } catch (e) { /* cannot remember; reload anyway */ }
                win.location.reload();
            }
        }

        // --- the page --------------------------------------------------------------------------

        function el(tag, className, text) {
            var e = doc.createElement(tag);
            if (className) e.className = className;
            if (text != null && text !== '') e.textContent = text;
            return e;
        }

        function px(n) { return n + 'px'; }

        function paintBlurHash(container, hash) {
            if (!hash) return;
            try {
                var w = 32, h = 18;
                var canvas = el('canvas', 'em-blur');
                canvas.width = w;
                canvas.height = h;
                var ctx = canvas.getContext('2d');
                var data = ctx.createImageData(w, h);
                data.data.set(decodeBlurHash(hash, w, h));
                ctx.putImageData(data, 0, 0);
                container.appendChild(canvas);
            } catch (e) { /* no placeholder colour, the grey stays */ }
        }

        function image(container, src, onload) {
            var img = el('img', 'em-img');
            img.alt = '';
            img.decoding = 'async';
            img.onload = function () {
                img.className = 'em-img em-loaded';
                if (onload) onload();
            };
            img.onerror = function () { if (onload) onload(); };
            img.src = src;
            // Already in the browser's cache (the loading screen drew it): shown at once, no fade.
            if (img.complete && img.naturalWidth) img.className = 'em-img em-loaded';
            container.appendChild(img);
            return img;
        }

        function sectionTitle(sizes, text) {
            var title = el('h2', 'em-row-title', text);
            // The text keeps its place; the space below it leaves room for a focused card's ring.
            title.style.height = px(sizes.titleBlock + sizes.clear);
            title.style.lineHeight = px(sizes.titleBlock);
            title.style.fontSize = px(sizes.titleSize);
            return title;
        }

        function skeleton(sizes) {
            var frame = el('div', 'em-page em-skeleton');
            var feature = el('div', 'em-feature em-placeholder');
            feature.style.height = px(sizes.feature);
            frame.appendChild(feature);
            // Featured is followed by My Media, Continue Watching, Next Up and Latest, the page's fixed order.
            var guesses = [{ Id: 'mymedia', Display: { Shape: 'landscape' } }, { Id: 'continue', Display: { Shape: 'landscape' } }, { Id: 'latest', Display: { Shape: 'portrait' } }];
            for (var i = 0; i < guesses.length; i++) frame.appendChild(placeholderRow(sizes, guesses[i], ''));
            frame.appendChild(el('div', 'em-sweep'));
            return frame;
        }

        function placeholderRow(sizes, section, title) {
            var row = el('section', 'em-row');
            row.style.height = px(rowHeight(sizes, section));
            row.style.paddingLeft = px(sizes.pad);
            row.appendChild(sectionTitle(sizes, title));
            var strip = el('div', 'em-strip');
            var size = cardSize(sizes, section);
            for (var i = 0; i < 8; i++) {
                var card = el('div', 'em-card em-placeholder');
                card.style.width = px(size[0]);
                card.style.height = px(size[1]);
                card.style.marginRight = px(sizes.gap);
                strip.appendChild(card);
            }
            row.appendChild(strip);
            return row;
        }

        function message(sizes, text) {
            var box = el('div', 'em-row-message', text);
            box.style.height = px(sizes.message);
            box.style.lineHeight = px(sizes.message);
            return box;
        }

        // inert: drawn for the loading screen, neither a link nor focusable.
        function card(sizes, section, item, serverId, onImage, inert) {
            var size = cardSize(sizes, section);
            var shape = section.Id === 'mymedia' ? 'landscape' : section.Display.Shape;
            var link = el(inert ? 'div' : 'a', inert ? 'em-card' : 'em-card em-focusable focusable');
            if (!inert) link.href = section.Id === 'mymedia' ? libraryHash(item, serverId) : detailsHash(item.Id, serverId);
            link.style.width = px(size[0]);
            link.style.marginRight = px(sizes.gap);
            var art = el('div', 'em-card-art');
            art.style.height = px(size[1]);
            var picked = cardImage(item, shape);
            if (picked) {
                paintBlurHash(art, blurHashOf(item, picked));
                image(art, imageUrl(base, picked[2], picked[0], picked[1], size[0], win.devicePixelRatio), onImage);
            } else {
                art.appendChild(el('div', 'em-card-name', item.Name));
                if (onImage) onImage();
            }
            if (item.UserData && item.UserData.PlayedPercentage) {
                var bar = el('div', 'em-progress');
                var fill = el('div');
                fill.style.width = Math.round(item.UserData.PlayedPercentage) + '%';
                bar.appendChild(fill);
                art.appendChild(bar);
            }
            link.appendChild(art);
            var lines = captionOf(item);
            var caption = el('div', 'em-caption');
            caption.style.height = px(sizes.caption[captionLines(section) - 1]);
            for (var i = 0; i < captionLines(section); i++) {
                var line = el('div', i === 0 ? 'em-caption-1' : 'em-caption-2', lines[i] || '');
                line.style.fontSize = px(sizes.captionSize[i]);
                caption.appendChild(line);
            }
            link.appendChild(caption);
            return link;
        }

        function fillRow(row, sizes, section, items, serverId, onFirstImage, inert) {
            var strip = el('div', 'em-strip');
            // The loading screen draws only what fits across the screen.
            var count = inert ? Math.min(items.length, Math.ceil(win.innerWidth / (cardSize(sizes, section)[0] + sizes.gap))) : items.length;
            for (var i = 0; i < count; i++) {
                strip.appendChild(card(sizes, section, items[i], serverId, i === 0 ? onFirstImage : null, inert));
            }
            row.appendChild(strip);
        }

        function rowFor(sizes, section, serverId, onFirstImage, inert) {
            var row = el('section', 'em-row');
            row.setAttribute('data-section', section.Id);
            row.style.paddingLeft = px(sizes.pad);
            row.appendChild(sectionTitle(sizes, section.Title));
            if (section.Error) {
                row.appendChild(message(sizes, section.Title + ' is unavailable right now. Everything else on Home is unaffected.'));
                return row;
            }
            row.style.height = px(rowHeight(sizes, section));
            fillRow(row, sizes, section, section.Items.Items, serverId, onFirstImage, inert);
            return row;
        }

        // A row below the first screen: drawn at its final height, filled when it nears the screen.
        function lazyRow(sizes, section, serverId, observer) {
            var row = placeholderRow(sizes, section, section.Title);
            row.setAttribute('data-section', section.Id);
            row.emSection = section;
            observer.observe(row);
            return row;
        }

        function loadLazyRow(row, sizes, serverId) {
            var section = row.emSection;
            row.emSection = null;
            api('/ExperienceManager/v1/Sections/' + encodeURIComponent(section.Id) + '/Items?startIndex=0&limit=16').then(function (result) {
                var strip = row.querySelector('.em-strip');
                if (strip) row.removeChild(strip);
                if (!result.Items.length) {
                    row.style.display = 'none';
                    return;
                }
                fillRow(row, sizes, section, result.Items, serverId, null);
            }, function () {
                var strip = row.querySelector('.em-strip');
                if (strip) row.removeChild(strip);
                row.style.height = '';
                row.appendChild(message(sizes, section.Title + ' is unavailable right now. Everything else on Home is unaffected.'));
            });
        }

        // --- the Billboard ---------------------------------------------------------------------

        function stopRotation() {
            if (state.rotation) {
                win.clearInterval(state.rotation.timer);
                state.rotation = null;
            }
        }

        // preview: drawn for the loading screen, still and inert; preview.onArt runs once its backdrop shows.
        function feature(sizes, section, serverId, layout, preview) {
            var items = section.Items.Items;
            var tag = preview ? 'div' : 'button';
            var focusable = preview ? '' : ' em-focusable focusable';
            var box = el('section', 'em-feature');
            box.setAttribute('data-section', section.Id);
            box.style.height = px(sizes.feature);
            var art = el('div', 'em-feature-art');
            box.appendChild(art);
            box.appendChild(el('div', 'em-scrim'));
            var info = el('div', 'em-feature-info');
            info.style.left = info.style.right = px(sizes.pad);
            box.appendChild(info);

            var buttons = el('div', 'em-feature-buttons');
            var play = el(tag, 'em-button em-primary' + focusable, 'Play');
            var more = el(tag, 'em-button' + focusable, 'More info');
            var favourite = el(tag, 'em-button em-icon' + focusable);
            var all = [play, more, favourite];
            for (var b = 0; b < all.length; b++) {
                if (!preview) all[b].type = 'button';
                all[b].style.fontSize = px(sizes.button[0]);
                all[b].style.height = px(sizes.button[1]);
            }
            favourite.setAttribute('aria-label', 'Favourite');
            buttons.appendChild(play);
            buttons.appendChild(more);
            buttons.appendChild(favourite);

            var dots = el('div', 'em-dots');
            var dotEls = [];
            for (var d = 0; d < items.length; d++) {
                var dot = el('span', 'em-dot');
                dot.appendChild(el('span', 'em-dot-fill'));
                dots.appendChild(dot);
                dotEls.push(dot);
            }
            var pause = null;
            if (layout !== 'tv' && items.length > 1) {
                pause = el(tag, 'em-pause' + focusable);
                if (!preview) pause.type = 'button';
                pause.setAttribute('aria-label', 'Pause');
                dots.appendChild(pause);
            }

            // Only the text changes per slide: the buttons stay put, so a focused one keeps focus.
            var text = el('div', 'em-feature-text');
            info.appendChild(text);
            info.appendChild(buttons);
            info.appendChild(dots);

            var index = 0;
            var heroMarked = false;
            var rotation = { timer: null, elapsed: 0, focused: false, paused: false };

            function show(i) {
                index = (i + items.length) % items.length;
                var item = items[index];
                var backdrop = (item.BackdropImageTags || [])[0];
                art.innerHTML = '';
                if (backdrop) {
                    paintBlurHash(art, blurHashOf(item, ['Backdrop', backdrop]));
                    image(art, imageUrl(base, item.Id, 'Backdrop', backdrop, Math.min(win.innerWidth, 1920), win.devicePixelRatio), heroShown);
                } else {
                    heroShown();
                }
                // the next slide's backdrop, so rotation shows it at once
                var next = items[(index + 1) % items.length];
                if (next && next !== item && (next.BackdropImageTags || [])[0]) {
                    new win.Image().src = imageUrl(base, next.Id, 'Backdrop', next.BackdropImageTags[0], Math.min(win.innerWidth, 1920), win.devicePixelRatio);
                }

                text.innerHTML = '';
                if (item.ImageTags && item.ImageTags.Logo) {
                    var logo = el('img', 'em-logo');
                    logo.alt = item.Name;
                    logo.style.height = px(sizes.logo);
                    logo.src = imageUrl(base, item.Id, 'Logo', item.ImageTags.Logo, sizes.logo * 6, win.devicePixelRatio);
                    text.appendChild(logo);
                } else {
                    var title = el('div', 'em-feature-title', item.Name);
                    title.style.fontSize = px(sizes.logo * 0.8);
                    text.appendChild(title);
                }
                text.appendChild(el('div', 'em-meta', metaOf(item)));
                var overview = el('div', 'em-overview', item.Overview || '');
                overview.style.fontSize = px(sizes.overview[0]);
                overview.style.maxWidth = px(sizes.overview[1]);
                text.appendChild(overview);
                favourite.textContent = item.UserData && item.UserData.IsFavorite ? '♥' : '♡';
                favourite.setAttribute('aria-pressed', item.UserData && item.UserData.IsFavorite ? 'true' : 'false');
                for (var k = 0; k < dotEls.length; k++) dotEls[k].className = k === index ? 'em-dot em-dot-on' : 'em-dot';
                rotation.elapsed = 0;
                progress();
            }

            function heroShown() {
                if (heroMarked) return;
                heroMarked = true;
                if (preview) preview.onArt();
                else mark('em-hero');
            }

            function progress() {
                var on = dotEls[index];
                if (on) on.firstChild.style.width = Math.min(100, rotation.elapsed / ROTATE_MS * 100) + '%';
            }

            show(0);
            box.emPlay = play;
            box.emMore = more;
            if (preview) return box;

            play.addEventListener('click', function () { playItem(items[index], serverId); });
            more.addEventListener('click', function () { win.location.hash = detailsHash(items[index].Id, serverId); });
            favourite.addEventListener('click', function () { toggleFavourite(items[index], favourite); });
            if (pause) {
                pause.addEventListener('click', function () {
                    rotation.paused = !rotation.paused;
                    pause.className = rotation.paused ? 'em-pause em-paused em-focusable focusable' : 'em-pause em-focusable focusable';
                    pause.setAttribute('aria-label', rotation.paused ? 'Play slides' : 'Pause');
                });
            }

            // Left on Play and Right on favourite change slides; the rest is jellyfin-web's own focus handling.
            box.addEventListener('keydown', function (e) {
                var key = e.key || '';
                var left = key === 'ArrowLeft' || e.keyCode === 37;
                var right = key === 'ArrowRight' || e.keyCode === 39;
                if ((left && e.target === play) || (right && e.target === favourite)) {
                    e.preventDefault();
                    e.stopPropagation();
                    show(index + (left ? -1 : 1));
                }
            });
            box.addEventListener('focusin', function (e) {
                rotation.focused = true;
                if (e.target === play || e.target === more || e.target === favourite) box.emLast = e.target;
            });
            box.addEventListener('focusout', function () { rotation.focused = false; });

            if (items.length > 1) {
                rotation.timer = win.setInterval(function () {
                    if (rotation.focused || rotation.paused || doc.hidden) return;
                    rotation.elapsed += 100;
                    if (rotation.elapsed >= ROTATE_MS) show(index + 1);
                    else progress();
                }, 100);
                state.rotation = rotation;
            }
            return box;
        }

        // Play goes through Jellyfin's own Play command to this device's session; without one, the details page.
        function playItem(item, serverId) {
            var cred = credentials();
            var deviceId = null;
            try { deviceId = win.localStorage.getItem('_deviceId2'); } catch (e) { /* none */ }
            var details = function () { win.location.hash = detailsHash(item.Id, serverId); };
            if (!cred || !deviceId) { details(); return; }
            api('/Sessions?deviceId=' + encodeURIComponent(deviceId)).then(function (sessions) {
                var mine = null;
                for (var i = 0; i < (sessions || []).length; i++) if (sessions[i].UserId === cred.UserId) mine = sessions[i];
                if (!mine) throw new Error('no session');
                return api('/Sessions/' + mine.Id + '/Playing?playCommand=PlayNow&itemIds=' + item.Id, { method: 'POST' });
            }).then(null, details);
        }

        function toggleFavourite(item, button) {
            var cred = credentials();
            if (!cred) return;
            var on = !(item.UserData && item.UserData.IsFavorite);
            api('/Users/' + cred.UserId + '/FavoriteItems/' + item.Id, { method: on ? 'POST' : 'DELETE' }).then(function (data) {
                item.UserData = data || item.UserData || {};
                item.UserData.IsFavorite = on;
                button.textContent = on ? '♥' : '♡';
                button.setAttribute('aria-pressed', on ? 'true' : 'false');
            }, function () { /* unchanged */ });
        }

        // --- drawing a visit to Home -----------------------------------------------------------

        function render(container, sizes, layout, page) {
            stopRotation();
            var cred = credentials();
            var serverId = cred ? cred.Id : null;
            var frame = el('div', 'em-page');
            var sections = page.Sections || [];
            var inlineRows = 0;
            var loadedRows = 0;
            var firstRowMarked = false;
            var observer = new win.IntersectionObserver(function (entries) {
                for (var i = 0; i < entries.length; i++) {
                    if (entries[i].isIntersecting && entries[i].target.emSection) {
                        observer.unobserve(entries[i].target);
                        loadLazyRow(entries[i].target, sizes, serverId);
                    }
                }
            }, { rootMargin: '0px 0px 400px 0px' });

            function rowImage() {
                if (!firstRowMarked) { firstRowMarked = true; mark('em-first-row'); }
                loadedRows++;
                if (loadedRows >= inlineRows) mark('em-last-row');
            }

            var featureBox = null;
            for (var i = 0; i < sections.length; i++) {
                var section = sections[i];
                if (section.Kind !== 'items') continue;
                if (section.Display.Hint === 'feature') {
                    if (section.Items && section.Items.Items.length) {
                        featureBox = feature(sizes, section, serverId, layout);
                        frame.appendChild(featureBox);
                    } else if (!section.Items) {
                        continue;
                    }
                    continue;
                }
                if (!section.Items && !section.Error) {
                    frame.appendChild(lazyRow(sizes, section, serverId, observer));
                    continue;
                }
                if (section.Items && !section.Items.Items.length) continue;
                if (!section.Error) inlineRows++;
                frame.appendChild(rowFor(sizes, section, serverId, rowImage));
            }
            if (!featureBox) mark('em-hero');
            if (!inlineRows) { mark('em-first-row'); mark('em-last-row'); }

            // Over placeholders the page fades in; under the loading screen, the loading screen fades out.
            if (!state.boot && container.querySelector('.em-skeleton')) frame.className += ' em-reveal';
            container.innerHTML = '';
            container.appendChild(frame);
            return featureBox;
        }

        // On a TV, Home opens on the first Continue Watching card, or on More info. jellyfin-web focuses
        // the first thing on a page as it shows it, so for a moment after opening, and until the
        // user presses a key, focus is put back on the landing place.
        function landFocus(container, featureBox) {
            var first = container.querySelector('[data-section="continue"] .em-card');
            var target = first || (featureBox && featureBox.emMore);
            if (!target) return;
            var until = Date.now() + 1500;
            var pressed = false;
            var onKey = function () { pressed = true; };
            doc.addEventListener('keydown', onKey, true);
            var hold = function () {
                if (pressed || Date.now() > until || !target.parentNode) {
                    doc.removeEventListener('keydown', onKey, true);
                    return;
                }
                if (doc.activeElement !== target) target.focus();
                keepTop(target);
                win.setTimeout(hold, 50);
            };
            hold();
        }

        // jellyfin-web scrolls to what it focuses as a page shows. Home opens at its top, hero in view,
        // whenever the landing place, with its ring, fits on the screen from there.
        function keepTop(target) {
            var scroller = doc.scrollingElement || doc.documentElement;
            if (!scroller.scrollTop) return;
            var art = target.querySelector('.em-card-art') || target;
            if (art.getBoundingClientRect().bottom + scroller.scrollTop + 12 <= win.innerHeight) win.scrollTo(0, 0);
        }

        function homeVisible() {
            var page = doc.getElementById('indexPage');
            var container = page && page.querySelector('.homeSectionsContainer');
            if (!container || page.classList.contains('hide')) return null;
            return container;
        }

        // --- the loading screen ----------------------------------------------------------------
        // When the app starts on Home, jellyfin-web takes a while to draw its page. The page call,
        // started with this script, is usually back long before, so the loading screen draws Home
        // itself, still and inert, and hands over to the live page once jellyfin-web has drawn it:
        //  - the first 250 ms show only the background, so a quick start never looks like loading;
        //  - then placeholders shaped like Home under one soft sweep of light, until the data is back;
        //  - with the data, the real hero and the rows that fit on the screen;
        //  - after 2.5 s, a status line whose bar moves only on real steps: connected, artwork shown,
        //    handing over; after 8 s it says the server is slow;
        //  - once the live page is drawn and its hero image shows, a crossfade.
        // It covers jellyfin-web's own splash and spinner, and never anything else: leaving Home, a
        // dialog, a failed page call or 20 s ends it.

        function onHomeRoute() {
            var hash = win.location.hash;
            return !hash || /^#\/?(home(\.html)?)?([?&]|$)/.test(hash);
        }

        // jellyfin-web sets its layout class a little later; until then, its saved choice or the device.
        function bootLayout() {
            if (/(^|\s)layout-/.test(doc.documentElement.className)) return layoutOf(doc.documentElement.classList);
            var saved = null;
            try { saved = win.localStorage.getItem('layout'); } catch (e) { /* none */ }
            if (saved === 'tv') return 'tv';
            if (saved === 'mobile') return 'phone';
            if (saved === 'desktop') return 'web';
            var browser = win.navigator.userAgent || '';
            if (/Web0S|webOS|Tizen|SMART-TV|SmartTV/i.test(browser)) return 'tv';
            return /Mobi|Android/i.test(browser) ? 'phone' : 'web';
        }

        function addClass(e, name) {
            if ((' ' + e.className + ' ').indexOf(' ' + name + ' ') < 0) e.className += ' ' + name;
        }

        // Home as the page call describes it, down to the bottom of the screen, with nothing to focus.
        function previewPage(boot, sizes, layout, page) {
            var cred = credentials();
            var serverId = cred ? cred.Id : null;
            var frame = el('div', 'em-page');
            var sections = page.Sections || [];
            var height = 0;
            for (var i = 0; i < sections.length && height < win.innerHeight; i++) {
                var section = sections[i];
                if (section.Kind !== 'items') continue;
                if (section.Display.Hint === 'feature') {
                    if (section.Items && section.Items.Items.length) {
                        frame.appendChild(feature(sizes, section, serverId, layout, { onArt: function () { bootStep(boot, 3); } }));
                        height += sizes.feature;
                    }
                    continue;
                }
                if (section.Items && !section.Items.Items.length) continue;
                frame.appendChild(section.Items || section.Error
                    ? rowFor(sizes, section, serverId, null, true)
                    : placeholderRow(sizes, section, section.Title));
                height += rowHeight(sizes, section);
            }
            return frame;
        }

        function drawBoot(boot) {
            var layout = bootLayout();
            var sizes = SIZES[layout];
            var layer = boot.emLayer;
            boot.emLayout = layout;
            boot.emStatus.style.fontSize = px(sizes.captionSize[0]);
            layer.className = 'em-boot-page em-home em-' + layout;
            layer.style.setProperty('--em-ring', px(sizes.ring));
            layer.style.setProperty('--em-room', px(sizes.room));
            layer.style.top = px(boot.emTop || BOOT_TOP[layout]);
            layer.innerHTML = '';
            layer.appendChild(boot.emPage ? previewPage(boot, sizes, layout, boot.emPage) : skeleton(sizes));
            // Real content shows at once; placeholders wait out the quiet moment.
            if (boot.emPage) addClass(boot, 'em-boot-shown');
        }

        var BOOT_STEPS = [0, 0.15, 0.55, 0.8, 1];

        function bootStep(boot, step) {
            if (step <= boot.emStep) return;
            boot.emStep = step;
            boot.emFill.style.transform = 'scaleX(' + BOOT_STEPS[step] + ')';
            bootLabel(boot);
        }

        function bootLabel(boot) {
            var cred = credentials();
            var server = cred && cred.Name ? cred.Name : 'your server';
            boot.emLabel.textContent = Date.now() - boot.emStarted > BOOT_SLOW_MS
                ? 'Still starting. ' + server + ' is taking longer than usual.'
                : boot.emStep < 2 ? 'Connecting to ' + server + '…' : 'Getting your home screen ready…';
        }

        function tickBoot(boot) {
            var elapsed = Date.now() - boot.emStarted;
            if (!onHomeRoute() || doc.querySelector('.dialogContainer .dialog') || elapsed > BOOT_MS) {
                hideBoot(false);
                return;
            }
            if (elapsed >= BOOT_QUIET_MS) addClass(boot, 'em-boot-shown');
            if (elapsed >= BOOT_STATUS_MS) addClass(boot, 'em-boot-status-on');
            if (elapsed >= BOOT_SLOW_MS) bootLabel(boot);
            if (bootLayout() !== boot.emLayout) drawBoot(boot);
            // Lines up with jellyfin-web's page as soon as it is there.
            var page = state.root && state.root.parentNode ? state.root : homeVisible();
            var top = page ? Math.round(page.getBoundingClientRect().top) : 0;
            if (top > 0 && top !== boot.emTop) {
                boot.emTop = top;
                boot.emLayer.style.top = px(top);
            }
        }

        function showBoot() {
            if (state.boot || fallbackActive() || !credentials() || !onHomeRoute()) return;
            if (!doc.body) {
                doc.addEventListener('DOMContentLoaded', showBoot);
                return;
            }
            if (homeVisible()) return;
            var boot = el('div', 'em-boot');
            boot.id = 'em-boot';
            var layer = el('div', 'em-boot-page');
            layer.setAttribute('aria-hidden', 'true');
            var status = el('div', 'em-boot-status');
            status.setAttribute('role', 'status');
            var label = el('div', 'em-boot-label');
            var bar = el('div', 'em-boot-bar');
            var fill = el('div');
            bar.appendChild(fill);
            status.appendChild(label);
            status.appendChild(bar);
            boot.appendChild(layer);
            boot.appendChild(status);
            boot.emLayer = layer;
            boot.emStatus = status;
            boot.emLabel = label;
            boot.emFill = fill;
            boot.emStarted = Date.now();
            boot.emStep = 0;
            drawBoot(boot);
            bootStep(boot, 1);
            doc.body.appendChild(boot);
            doc.documentElement.classList.add('em-booting');
            state.boot = boot;
            loadPage().then(function (page) {
                if (state.boot !== boot) return;
                boot.emPage = page;
                bootStep(boot, 2);
                drawBoot(boot);
            }, function () { hideBoot(false); });
            boot.emTimer = win.setInterval(function () { tickBoot(boot); }, 100);
        }

        // fade: hand over to the drawn page once its hero image shows (600 ms at most), so both sides
        // of the crossfade show the same picture. Otherwise the loading screen just goes.
        function hideBoot(fade) {
            var boot = state.boot;
            if (!boot) return;
            state.boot = null;
            win.clearInterval(boot.emTimer);
            var gone = function () {
                if (boot.parentNode) boot.parentNode.removeChild(boot);
                if (!state.boot) doc.documentElement.classList.remove('em-booting');
            };
            if (!fade) {
                gone();
                return;
            }
            bootStep(boot, 4);
            var waited = 0;
            var wait = win.setInterval(function () {
                waited += 50;
                var hero = state.root && state.root.querySelector('.em-feature .em-img');
                if (waited < 600 && hero && hero.className.indexOf('em-loaded') < 0) return;
                win.clearInterval(wait);
                addClass(boot, 'em-boot-out');
                win.setTimeout(gone, 350);
            }, 50);
        }

        // --- Up and Down into the hero ---------------------------------------------------------
        // jellyfin-web moves focus to whatever sits straight above or below. The hero's buttons sit at
        // its left, so from most header buttons and most cards in the row below, it would skip them.
        win.addEventListener('keydown', function (e) {
            if (e.defaultPrevented || e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
            var down = e.key === 'ArrowDown' || e.keyCode === 40;
            var up = e.key === 'ArrowUp' || e.keyCode === 38;
            var from = doc.activeElement;
            if ((!down && !up) || !from || !from.closest || !state.root || !state.root.parentNode || !homeVisible()) return;
            var feature = state.root.querySelector('.em-feature');
            if (!feature || !feature.emPlay) return;
            var into = null;
            if (down && from.closest('.skinHeader')) into = feature.emPlay;
            else if (up && from.closest('.em-row') && from.closest('.em-row') === feature.nextElementSibling) into = feature.emLast || feature.emPlay;
            if (!into) return;
            e.preventDefault();
            into.focus();
        }, true);

        function mount() {
            var stock = homeVisible();
            if (!stock) return false;
            var visit = ++state.visit;
            clearMarks();
            mark('em-route');
            var layout = layoutOf(doc.documentElement.classList);
            var sizes = SIZES[layout];
            var container = state.root;
            if (!container || !container.parentNode) {
                container = el('div', 'em-home em-' + layout);
                container.id = 'em-home';
                container.style.setProperty('--em-ring', px(sizes.ring));
                container.style.setProperty('--em-room', px(sizes.room));
                stock.parentNode.insertBefore(container, stock);
                state.root = container;
                if (layout === 'tv') {
                    container.addEventListener('mouseover', function (e) {
                        var target = e.target.closest ? e.target.closest('.em-focusable') : null;
                        if (target && doc.activeElement !== target) target.focus();
                    });
                }
                container.addEventListener('focusin', function (e) {
                    var row = e.target.closest ? e.target.closest('.em-row, .em-feature') : null;
                    if (row && row.scrollIntoView) row.scrollIntoView({ block: 'nearest' });
                });
                container.appendChild(skeleton(sizes));
            }
            doc.documentElement.classList.add('em-active');
            mark('em-skeleton');

            // The first visit uses the call started when the script loaded; later visits ask again.
            if (visit > 1) state.page = null;
            loadPage().then(function (page) {
                if (visit !== state.visit) return;
                var json = JSON.stringify(page);
                if (container.emRendered === json) {
                    mark('em-hero'); mark('em-first-row'); mark('em-last-row');
                    hideBoot(true);
                    return;
                }
                container.emRendered = json;
                var featureBox = render(container, sizes, layout, page);
                hideBoot(true);
                if (layout === 'tv') landFocus(container, featureBox);
            }, function () {
                if (visit === state.visit) fallBack();
            });
            return true;
        }

        // Watches for the Home tab without page-wide observers: a short poll after each navigation.
        var timer = null;
        function watch() {
            if (fallbackActive()) return;
            if (timer) win.clearInterval(timer);
            var tries = 0;
            timer = win.setInterval(function () {
                tries++;
                var mounted = mount();
                if (mounted || tries > 200) {
                    win.clearInterval(timer);
                    timer = null;
                    if (!mounted) hideBoot(false);
                }
            }, 30);
        }

        win.addEventListener('hashchange', function () {
            stopRotation();
            if (!onHomeRoute()) hideBoot(false);
            watch();
        });
        if (fallbackActive()) {
            // This session fell back once already; try again on the next app start.
            try { win.sessionStorage.removeItem(FALLBACK_FLAG); } catch (e) { /* stays */ }
            return;
        }
        if (credentials()) {
            loadPage();
            showBoot();
        }
        watch();
    }
})(this);
