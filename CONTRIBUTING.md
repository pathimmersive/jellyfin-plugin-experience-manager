# Contributing to Experience Manager

Thanks for your interest. Experience Manager is in early development, so please open an issue or start a discussion about a change before starting on a pull request.

## Asking, suggesting and reporting

- **Questions** about installing, setting up or using Experience Manager go in [Discussions → Q&A](https://github.com/pathimmersive/jellyfin-plugin-experience-manager/discussions/categories/q-a).
- **Ideas** can be talked through in [Discussions → Ideas](https://github.com/pathimmersive/jellyfin-plugin-experience-manager/discussions/categories/ideas) before a feature request.
- **Bugs and feature requests** are issues, opened with their forms.

For a bug, use the bug report form. Include the Jellyfin server version, the Experience Manager version or commit, the client app and device, and the steps that show the problem. Security problems go through private reporting instead; see [SECURITY.md](SECURITY.md).

## Making a change

1. Fork the repository and branch from `main`, naming the branch `<type>/<short-name>`, for example `fix/hero-focus`.
2. Build and test as the [README](README.md#building-and-testing) describes. A change should pass `dotnet test`, `bash scripts/check-community-files.sh` and, for server changes, `bash scripts/smoke-test.sh`.
   - A change to packaging should also pass `bash scripts/repo-install-check.sh` and `bash scripts/repo-install-check.sh --upgrade`.
   - Note anything checked by hand, with the client, device and Jellyfin version.
   - `bash scripts/coverage.sh` runs the tests and prints their coverage, as CI does. A new behaviour should come with a test, but no coverage percentage fails a build.
3. Write the pull request title as a [Conventional Commits](https://www.conventionalcommits.org/) subject: `feat:`, `fix:`, `docs:`, `test:`, `ci:` or `chore:`, then what changed, in 72 characters or fewer.
4. Fill in the pull request template: what changed and why, and how it was checked. Link a related issue with `Fixes #N`.
5. For a change users would notice, add a line under **Unreleased** in [CHANGELOG.md](CHANGELOG.md), in the section that fits (Added, Changed, Fixed, Removed, Security).
6. CI must pass before the change can merge. Pull requests are squash-merged into one commit on `main`, so give their commits Conventional Commits messages too.

## Releases

There are no releases yet. Releases will be cut by a maintainer, and only when there is something worth shipping. Nothing is released from `main` automatically.

## Third-party code

Only code under licenses compatible with GPL-3.0 can be added. New third-party code needs an entry in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) with its version, copyright line and full license text.

## License

By contributing you agree that your contribution is licensed under the GNU General Public License v3.0 or later, the license of this project.

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
