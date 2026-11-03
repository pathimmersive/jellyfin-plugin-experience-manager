# Changelog

All notable changes to Experience Manager are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow the plugin's version number.

## [Unreleased]

### Added

- An admin page with its own entry in the Dashboard sidebar. It is empty for now.
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
- A shared cache behind the home page, kept up to date as libraries and watch progress change, and warmed by a scheduled task when the server starts.
