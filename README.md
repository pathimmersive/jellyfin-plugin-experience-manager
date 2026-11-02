# Experience Manager

Experience Manager is a Jellyfin server plugin that gives server admins control over what their users see on the home screen. Admins arrange a featured banner and rows of content, choose who sees which layout, and schedule changes for events and seasons, all from one admin page. It aims to replace a set of separate plugins with one that is fast on older TVs and does not break when Jellyfin updates.

**Experience Manager is an unofficial, third-party plugin.** It is not made, endorsed or supported by the Jellyfin project, and it does not use the Jellyfin logo.

## Status

Early development; there is no release yet and the plugin does nothing useful so far. It builds, installs, and adds an empty admin page with its own entry in the Dashboard sidebar.

## Requirements

- Jellyfin 12.0 or later. The build targets 12.2.

## Building and testing

You need the .NET 10 SDK and bash.

```bash
dotnet test                            # builds the plugin and runs its tests
bash scripts/check-community-files.sh  # community files
bash scripts/coverage.sh               # the tests, with a coverage summary
```

These also need Docker. CI runs them on every pull request.
- `bash scripts/smoke-test.sh` loads the plugin into a real Jellyfin server and checks what it reports and serves: the plugin active with its image, and its admin page in the Dashboard sidebar.
- `bash scripts/repo-install-check.sh` installs the plugin the way an admin does, from a plugin repository served from a local container. With `--upgrade`, it then upgrades to a newer version from the same repository.

## Questions, contributing and security

Ask questions and share ideas in [Discussions](https://github.com/pathimmersive/jellyfin-plugin-experience-manager/discussions), and report bugs as [issues](https://github.com/pathimmersive/jellyfin-plugin-experience-manager/issues/new/choose). See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately, as [SECURITY.md](SECURITY.md) describes. Changes are listed in [CHANGELOG.md](CHANGELOG.md).

## License

Experience Manager is licensed under the GNU General Public License v3.0 or later; see [LICENSE](LICENSE). Third-party code keeps its own license; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
