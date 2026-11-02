# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through GitHub instead: on this repository's **Security** tab, choose **Report a vulnerability**. Only the maintainers can see the report.

Include what is affected, the Jellyfin server version, the Experience Manager version or commit, the client app and device, and the steps to reproduce it. You will get a reply in the report, and the fix and its advisory are coordinated there.

## Supported versions

Experience Manager has no release yet. Fixes go into `main`; once releases exist, the latest release is supported.

## Scope

In scope:
- a user seeing items their library access or parental controls should hide;
- a non-administrator changing the plugin's settings or reading another user's data through the plugin;
- the plugin changing server data it should not touch.

Problems in Jellyfin itself, or in other plugins, belong with those projects.
