# Experience Manager home API: integration guide

Experience Manager's home screen is served through a public API, and any Jellyfin client can draw it: the featured banner, My Media, Continue Watching, Next Up, Latest and every other section an admin sets up. This guide takes a client from discovery to a drawn home page. The full contract is [openapi.json](openapi.json) (OpenAPI 3).

The schema and this guide are MIT licensed ([LICENSE](LICENSE)), so client authors can copy from them freely. The plugin itself is GPL-3.0.

The API is early: it may still change before the plugin's first release.

## Before you start

- **Server:** a Jellyfin 12 server with Experience Manager installed.
- **Token:** the same access token the client already uses for Jellyfin's own routes. Every route except discovery answers for the token's user, in Jellyfin's usual `Authorization` header.
- **Items:** sections hold Jellyfin's own items (`BaseItemDto`), exactly as Jellyfin's `/Items` routes return them, so a client's existing card and image code works unchanged.

The examples below use two shell variables: `SERVER`, the server's address (for example `http://localhost:8096`), and `TOKEN`, an access token.

## 1. Discover the plugin

`GET /ExperienceManager/Info` needs no token. A 404 means the plugin is not installed: draw Jellyfin's own home screen instead.

```sh
curl -sf "$SERVER/ExperienceManager/Info"
# {"Version":"0.1.0.0","ApiVersions":["v1"],"Features":["pages","section-items"]}
```

Use the API only when `ApiVersions` contains `v1`.

## 2. Load the home page

`GET /ExperienceManager/v1/Pages/home` returns the user's home page: its sections in order. With `include=items`, the first `count` sections carry their first items too, so the first screen draws from one call.

```sh
curl -sf -H "Authorization: MediaBrowser Token=\"$TOKEN\"" \
  "$SERVER/ExperienceManager/v1/Pages/home?include=items&count=4"
```

Each section has:

| Field | Meaning |
|---|---|
| `Id` | The section's stable id; use it to page its items. |
| `Title` | The title to show above it. |
| `Kind` | The content kind. `items` is a row of Jellyfin items. |
| `Display.Hint` | `feature` for a large banner, `row` for a row of cards. |
| `Display.Shape` | The card shape: `portrait`, `landscape` or `square`. |
| `Rank` | Whether cards show their position, as in a Top 10. |
| `SeeAll` | Where "see all" leads, as a Jellyfin item query, or absent. |
| `Items` | The first items, when the page call inlined them. |
| `Error` | Present when the section's source failed: show a short message or hide the section. |

The page call takes Jellyfin's own item parameters, `fields`, `enableImageTypes`, `imageTypeLimit` and `enableUserData`, and applies them to every inlined section.

The admin can schedule events that change the page for a while, such as a holiday row. Send `utcOffset`, the viewer's offset from UTC in minutes east (`-300` for New York in winter, `60` for Paris), on the page call and on section items, so events start and end in the viewer's local time. Without it, the server's time zone is used.

## 3. Page a section

`GET /ExperienceManager/v1/Sections/{sectionId}/Items` returns one section's items, paged with `startIndex` and `limit` (16 by default, at most 100). Load a section that came without items as it nears the screen, and the next page as the user scrolls along a row.

```sh
curl -sf -H "Authorization: MediaBrowser Token=\"$TOKEN\"" \
  "$SERVER/ExperienceManager/v1/Sections/mymedia/Items?startIndex=0&limit=16"
# {"Items":[...],"TotalRecordCount":3,"StartIndex":0}
```

A 404 means the section is not on this user's page, for example after the admin changed the layout or an event ended: reload the page.

## 4. Draw it

The page carries no section types, only how to show each section, so a client keeps working as admins add new kinds of sections. Three rules keep a simple client correct:

- **An unknown `Kind` is skipped.** Draw `items`; leave out anything else.
- **Features fall back to rows.** A client that cannot draw a `feature` as a banner draws it as a row. Feature styles added later fall back to a plain feature.
- **Unknown fields are ignored.** Fields are added within a version (see Versioning), never removed or changed.

The banner is just a section with the `feature` hint, so a client that only draws rows still shows its items.

Images come from Jellyfin's own image routes, as for any item: `/Items/{itemId}/Images/{type}?tag={tag}&maxWidth={width}`, with the tags in each item's `ImageTags`, `BackdropImageTags` and `ImageBlurHashes`.

"See all" runs as Jellyfin's own `/Items` query with the target's `ParentId`, `IncludeItemTypes`, `SortBy`, `SortOrder` and `Filters`, plus `Recursive=true`:

```sh
curl -sf -H "Authorization: MediaBrowser Token=\"$TOKEN\"" \
  "$SERVER/Items?IncludeItemTypes=Movie&SortBy=DateCreated&SortOrder=Descending&Recursive=true&Limit=20"
```

## 5. Keep it fresh

Both the page and section items carry an `ETag`. Send it back in `If-None-Match`: an unchanged response is a 304 with no body, so the client keeps what it has and redraws nothing.

```sh
etag=$(curl -sf -D - -o /dev/null -H "Authorization: MediaBrowser Token=\"$TOKEN\"" \
  "$SERVER/ExperienceManager/v1/Pages/home" | tr -d '\r' | sed -n 's/^[Ee][Tt][Aa][Gg]: *//p')
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: MediaBrowser Token=\"$TOKEN\"" \
  -H "If-None-Match: $etag" "$SERVER/ExperienceManager/v1/Pages/home"
# 304
```

The page's `LayoutVersion` changes whenever the layout or the events on it change, which tells a client that caches pages between sessions when to drop them.

## Versioning

- Routes carry the API version: `/ExperienceManager/v1/...`. `Info` lists the versions a server serves in `ApiVersions`.
- Within `v1`, changes are additive only: new fields, new section kinds and hints, new routes. Existing fields keep their names and meaning.
- Before a `v2`, the change and a deprecation window are announced in the plugin's changelog.

## Reserved routes

`/ExperienceManager/v1/Me/Preferences`, the user's own home screen preferences, is named in the contract but not yet available: servers answer 404 until a version that serves it, and `Info` will list it in `Features` when it does.

## The contract

[openapi.json](openapi.json) is generated from the API the plugin serves, with descriptions from its code, and the plugin's tests fail if the two ever differ. Jellyfin's `BaseItemDto` is referenced rather than copied: its full schema is in the server's own OpenAPI document at `/api-docs/openapi.json`.
