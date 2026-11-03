# Experience Manager

Experience Manager is a Jellyfin server plugin that gives server admins control over what their users see on the home screen. Admins arrange a featured banner and rows of content, choose who sees which layout, and schedule changes for events and seasons, all from one admin page. It aims to replace a set of separate plugins with one that is fast on older TVs and does not break when Jellyfin updates.

**Experience Manager is an unofficial, third-party plugin.** It is not made, endorsed or supported by the Jellyfin project, and it does not use the Jellyfin logo.

## Status

Early development; there is no release yet. The plugin draws a first version of its home screen in jellyfin-web, from a fixed layout: a featured banner, My Media, Continue Watching, Next Up and Latest. Its admin page is still empty, so nothing can be configured yet.

## Home API

Experience Manager's home screen reads from a public API that other clients can use too. Every route answers for the signed-in user, with their usual Jellyfin token:
- `GET /ExperienceManager/Info` reports the plugin version and the API versions it serves. It needs no token.
- `GET /ExperienceManager/v1/Pages/home` lists the Home page's sections in order. With `include=items`, it also carries their first items.
- `GET /ExperienceManager/v1/Sections/{sectionId}/Items?startIndex=&limit=` pages one section's items.

Items are Jellyfin's own `BaseItemDto`, and the API takes Jellyfin's `fields`, `enableImageTypes`, `imageTypeLimit` and `enableUserData` parameters. The API is early and may still change before the first release.

## Requirements

- Jellyfin 12.0 or later. The build targets 12.2.
- No other plugins. Experience Manager adds its home client to jellyfin-web as the page is served and never changes jellyfin-web's files. It also works alongside the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin, which other plugins use.

## Building and testing

You need the .NET 10 SDK, Node.js 22 or later, and bash.

```bash
dotnet test                            # builds the plugin and runs its tests
node --test "tests/home/*.test.js"     # home client tests
bash scripts/check-community-files.sh  # community files
bash scripts/coverage.sh               # both test suites, with a coverage summary
```

These also need Docker. CI runs them on every pull request.
- `bash scripts/smoke-test.sh` loads the plugin into a real Jellyfin server and checks what it reports and serves. That covers the plugin active with its image, its admin page in the Dashboard sidebar, and the home client added to jellyfin-web's page. The page checks also run alongside File Transformation and under a BaseUrl.
- `bash scripts/repo-install-check.sh` installs the plugin the way an admin does, from a plugin repository served from a local container. With `--upgrade`, it then upgrades to a newer version from the same repository.

## Questions, contributing and security

Ask questions and share ideas in [Discussions](https://github.com/pathimmersive/jellyfin-plugin-experience-manager/discussions), and report bugs as [issues](https://github.com/pathimmersive/jellyfin-plugin-experience-manager/issues/new/choose). See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately, as [SECURITY.md](SECURITY.md) describes. Changes are listed in [CHANGELOG.md](CHANGELOG.md).

## License

Experience Manager is licensed under the GNU General Public License v3.0 or later; see [LICENSE](LICENSE). Third-party code keeps its own license; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
