# Changelog

All notable changes to Experience Manager are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow the plugin's version number.

## [Unreleased]

### Added

- An admin page with its own entry in the Dashboard sidebar, grouped as Experience, People, Rules, Reach and Operate. Pages not built yet say so.
- Reach → Native clients: every device registered with the server, by user and app, and what Experience Manager reaches on each app today and what is planned. Server apps such as Seerr are listed apart. Its admin-only route is not part of the public API.
- Reach adapters: every way Experience Manager reaches an app goes through one contract, and Native clients lists each adapter with what it found. The home screen is the first, on and ready; the Moonfin, Wholphin and Streamyfin adapters, stock home sections and maintained collections are listed as not built yet, and cannot be switched on. Every adapter but the home screen is off by default, stored per server in the plugin's settings.
- Installation from a Jellyfin plugin repository.
- A plugin image in Dashboard → Plugins and in the plugin repository's catalog.
- Experience Manager's home screen in jellyfin-web, in place of its own home rows:
  - a featured banner with Play, More info and favourite, rotating every 8 seconds;
  - My Media, Continue Watching, Next Up and Latest rows;
  - placeholders at their final size, and lower rows loaded as they come into view.

  It works with a TV remote and runs on TVs as old as webOS 5. If the plugin's API fails, Jellyfin's own home shows instead. Users' home settings are never changed.
- A public home API, `/ExperienceManager/v1/`, for Experience Manager's own home screen and for other clients:
  - the Home page's sections in order: featured films, My Media, Continue Watching, Next Up, and Latest for each movie and show library;
  - each section's items as Jellyfin's own item type, paged;
  - `/ExperienceManager/Info` for discovery.
- The home API's contract and an integration guide for client authors, in docs/api/, MIT licensed. Tests keep the contract matching what the plugin serves.
- Scheduled events: named sets of changes that overlay Home for a while and never edit it. An event can swap a section's source, show another source in the featured banner, add a section (a library query on genres or tags, or a collection) or hide one. It runs once, every year (across the new year too) or every week, in the viewer's local time. Events form an ordered list: where two change the same section at the same time, the higher one wins, and the event list says so. Only published events apply; a fresh install holds Halloween and Christmas as drafts. Seasonal rows show watched titles unless Hide watched is on. Administrators manage events through admin-only routes, not part of the public API. The home API takes the viewer's `utcOffset`, which the home screen sends.
- Home stored as a page in the plugin's own SQLite database, under the server's data folder, so later pages and scheduled changes build on it. A fresh install starts with the layout above. Administrators can read and replace a page's sections through an admin-only route, which is not part of the public API; a change shows on the next page call with a new layout version.
- A shared cache behind the home page, kept up to date as libraries and watch progress change, and warmed by a scheduled task when the server starts.
