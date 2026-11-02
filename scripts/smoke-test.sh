#!/usr/bin/env bash
# Smoke test: build the plugin, load it into a real Jellyfin 12.x container, and
# check what the server reports and serves:
# - the plugin active with its image, and its admin page in the Dashboard sidebar;
# - the home client's script and stylesheet served, and added to jellyfin-web's
#   index page as it is served.
# The index checks run on their own, with the File Transformation plugin installed
# alongside, and under a BaseUrl. Exits non-zero on the first failed check and
# always removes the container.
#
# Needs: bash, curl, docker, unzip, md5sum, the .NET 10 SDK.
# Override the image with JELLYFIN_IMAGE=jellyfin/jellyfin:<tag>.
set -euo pipefail

IMAGE="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12.2.20261005-225228}"
PLUGIN_NAME="Experience Manager"
PLUGIN_VERSION="0.1.0.0"
PLUGIN_DIR="ExperienceManager_$PLUGIN_VERSION"
PAGE_NAME="ExperienceManager"
ADMIN_USER="smoke"
ADMIN_PW="smoke-$RANDOM$RANDOM"
CLIENT_HEADER='MediaBrowser Client="experience-manager-smoke", Device="smoke", DeviceId="experience-manager-smoke", Version="1.0.0"'
SCRIPT_PATH="ExperienceManager/home.js"
STYLE_PATH="ExperienceManager/home.css"

# File Transformation is not needed; admins may have it for other plugins, so one
# case runs with it installed alongside. The pinned release is built for the
# image's server version, and the MD5 is the one in its publisher's manifest.
FT_VERSION="3.0.2.0"
FT_SERVER_VERSION="12.2.0"
FT_URL="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/download/$FT_VERSION/Release-$FT_SERVER_VERSION.zip"
FT_MD5="76eae3b09a42dba7992820fa8f6a4584"

repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
container="experience-manager-smoke-$$"

cleanup() {
    docker rm -f "$container" >/dev/null 2>&1 || true
    rm -rf "$work"
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "PASS: $*"; }

# Docker Desktop on Windows needs a Windows path for bind mounts.
hostpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }

# First value of a string field in a JSON body. Enough for the flat
# responses checked here; not a JSON parser.
json_str() { grep -o "\"$1\":\"[^\"]*\"" | head -1 | sed "s/^\"$1\":\"\(.*\)\"$/\1/" || true; }

# --- build -----------------------------------------------------------------

project="$repo/src/Jellyfin.Plugin.ExperienceManager/Jellyfin.Plugin.ExperienceManager.csproj"
plugin_dir="$work/plugins/$PLUGIN_DIR"
dotnet publish "$project" -c Release -o "$plugin_dir" --nologo -v quiet >/dev/null || fail "dotnet publish"
[ -f "$plugin_dir/Jellyfin.Plugin.ExperienceManager.dll" ] || fail "plugin assembly missing from publish output"
grep -q '"targetAbi": *"12.0.0.0"' "$plugin_dir/meta.json" || fail "meta.json does not declare targetAbi 12.0.0.0"
pass "plugin built; meta.json declares targetAbi 12.0.0.0"
grep -q '"imagePath": *"icon.png"' "$plugin_dir/meta.json" && [ -s "$plugin_dir/icon.png" ] \
    || fail "the plugin folder does not carry icon.png named by meta.json's imagePath"
pass "the plugin folder carries icon.png, named by meta.json's imagePath"

ft_cache="${XDG_CACHE_HOME:-$HOME/.cache}/experience-manager/FileTransformation-$FT_VERSION.zip"
if [ ! -f "$ft_cache" ] || [ "$(md5sum "$ft_cache" | cut -d' ' -f1)" != "$FT_MD5" ]; then
    mkdir -p "$(dirname "$ft_cache")"
    curl -sSfL -o "$ft_cache" "$FT_URL" || fail "download File Transformation $FT_VERSION"
fi
[ "$(md5sum "$ft_cache" | cut -d' ' -f1)" = "$FT_MD5" ] || fail "File Transformation $FT_VERSION does not match its published MD5"
mkdir -p "$work/ft"
unzip -q -o "$ft_cache" -d "$work/ft"

# --- server ----------------------------------------------------------------

MSYS_NO_PATHCONV=1 docker run -d --name "$container" \
    -p 127.0.0.1::8096 \
    -v "$(hostpath "$plugin_dir"):/config/plugins/$PLUGIN_DIR" \
    -v "$(hostpath "$work/ft"):/experience-manager-ft:ro" \
    "$IMAGE" >/dev/null || fail "docker run $IMAGE"

base_url() { echo "http://127.0.0.1:$(docker port "$container" 8096/tcp | head -1 | sed 's/.*://')$1"; }

wait_ready() {
    local url="$1"
    # While starting, the server answers with an HTML holding page, so a status code
    # alone is not enough; wait for the JSON system info.
    for _ in $(seq 1 120); do
        if curl -sf "$url/System/Info/Public" 2>/dev/null | grep -q '"StartupWizardCompleted"'; then
            return 0
        fi
        sleep 1
    done
    docker logs "$container" 2>&1 | tail -40 >&2
    fail "server at $url did not become ready"
}

restart() {
    docker restart "$container" >/dev/null || fail "docker restart"
    wait_ready "$(base_url "$1")"
}

base="$(base_url "")"
wait_ready "$base"
pass "server ready on $IMAGE"

post_json() { curl -sf -X POST -H 'Content-Type: application/json' -H "Authorization: $CLIENT_HEADER" "$@"; }
post_json "$base/Startup/Configuration" -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null || fail "startup configuration"
curl -sf -H "Authorization: $CLIENT_HEADER" "$base/Startup/User" >/dev/null || fail "startup user (get)"
post_json "$base/Startup/User" -d "{\"Name\":\"$ADMIN_USER\",\"Password\":\"$ADMIN_PW\"}" >/dev/null || fail "startup user (set)"
post_json "$base/Startup/Complete" >/dev/null || fail "startup complete"

token="$(post_json "$base/Users/AuthenticateByName" -d "{\"Username\":\"$ADMIN_USER\",\"Pw\":\"$ADMIN_PW\"}" | json_str AccessToken)"
[ -n "$token" ] || fail "no access token from /Users/AuthenticateByName"
auth="Authorization: $CLIENT_HEADER, Token=\"$token\""

get_status() { curl -s -o "$work/body" -w '%{http_code}' "$@"; }

# --- checks ----------------------------------------------------------------

check_plugin_active() {
    local prefix="$1" name="$2"
    code="$(get_status -H "$auth" "$prefix/Plugins")"
    [ "$code" = "200" ] || { docker logs "$container" 2>&1 | grep -A8 -E "ERR|Error" | tail -30 >&2; fail "GET /Plugins returned $code"; }
    entry="$(sed 's/},{/}\n{/g' "$work/body" | grep "\"Name\":\"$name\"" || true)"
    [ -n "$entry" ] || fail "$name not listed by /Plugins"
    [ "$(echo "$entry" | json_str Status)" = "Active" ] || fail "$name status is '$(echo "$entry" | json_str Status)', expected Active"
    pass "/Plugins lists $name as Active"
}

check_no_create_error() {
    # Read the whole log first: grep -q stops early, and under pipefail the cut-off
    # docker logs would fail the check.
    docker logs "$container" > "$work/server.log" 2>&1
    ! grep -q 'Error creating Jellyfin.Plugin.ExperienceManager' "$work/server.log" || fail "the server log has an error creating the plugin"
    pass "the server log has no error creating the plugin"
}

# A file the plugin serves for the home client: anonymous, its type, the file itself,
# no-cache, and an ETag that gets 304.
check_served() {
    local prefix="$1" path="$2" file="$3" type="$4"
    code="$(curl -s -D "$work/headers" -o "$work/served-file" -w '%{http_code}' "$prefix/$path")"
    [ "$code" = "200" ] || fail "anonymous GET $prefix/$path returned $code"
    grep -i -q "^content-type: *$type" "$work/headers" || fail "GET $prefix/$path is not $type"
    cmp -s "$work/served-file" "$repo/$file" || fail "GET $prefix/$path is not $file"
    grep -i -q '^cache-control: *no-cache' "$work/headers" || fail "GET $prefix/$path has no Cache-Control: no-cache"
    etag="$(grep -i '^etag:' "$work/headers" | sed 's/^[^:]*: *//' | tr -d '\r')"
    [ -n "$etag" ] || fail "GET $prefix/$path has no ETag"
    # curl creates the output file only when a body arrives, so start from none.
    rm -f "$work/not-modified"
    code="$(curl -s -o "$work/not-modified" -w '%{http_code}' -H "If-None-Match: $etag" "$prefix/$path")"
    [ "$code" = "304" ] || fail "GET $prefix/$path with its ETag returned $code, expected 304"
    [ ! -s "$work/not-modified" ] || fail "the 304 for $prefix/$path has a body"
    pass "anonymous GET $prefix/$path serves $file as $type, no-cache, with an ETag that gets 304"
}

check_client_served() {
    check_served "$1" "$SCRIPT_PATH" src/home/home.js text/javascript
    check_served "$1" "$STYLE_PATH" src/home/home.css text/css
}

SCRIPT_PATTERN='<script async src="../ExperienceManager/home.js[^"]*"></script>'
STYLE_PATTERN='<link rel="stylesheet" href="../ExperienceManager/home.css[^"]*">'

# The served index page carries exactly one stylesheet and one script tag, for this
# version, right before </head>.
check_web_changed() {
    local prefix="$1" label="$2" count
    curl -sf -o "$work/served" "$prefix/web/index.html" || fail "GET $prefix/web/index.html"
    local tags="<link rel=\"stylesheet\" href=\"../$STYLE_PATH?v=$PLUGIN_VERSION\"><script async src=\"../$SCRIPT_PATH?v=$PLUGIN_VERSION\"></script>"
    count="$(grep -o "$SCRIPT_PATTERN" "$work/served" | wc -l | tr -d ' ')"
    [ "$count" = "1" ] || fail "$label: $prefix/web/index.html carries $count home client script tags, expected 1"
    count="$(grep -o "$STYLE_PATTERN" "$work/served" | wc -l | tr -d ' ')"
    [ "$count" = "1" ] || fail "$label: $prefix/web/index.html carries $count home client stylesheet tags, expected 1"
    grep -q -F "$tags</head>" "$work/served" || fail "$label: $prefix/web/index.html does not carry $tags right before </head>"
    for path in /web/ /web; do
        curl -sfL -o "$work/served-alt" "$prefix$path" || fail "GET $prefix$path"
        [ "$(grep -o "$SCRIPT_PATTERN" "$work/served-alt" | wc -l | tr -d ' ')" = "1" ] || fail "$label: $prefix$path does not carry one home client script tag"
    done
    pass "$label: $prefix/web/index.html, /web/ and /web carry one stylesheet and one script tag for version $PLUGIN_VERSION before </head>"
}

# How the changed page is sent: plain to a client that asks for compression, a
# Content-Length that matches, HEAD as GET, an ETag of its own that gets 304, and
# jellyfin-web's file on disk untouched.
check_web_http() {
    local prefix="$1" url="$1/web/index.html"
    curl -sf -D "$work/get-headers" -o "$work/get" "$url" || fail "GET $url"
    header() { grep -i "^$1:" "$2" | sed 's/^[^:]*: *//' | tr -d '\r'; }
    [ "$(header content-length "$work/get-headers")" = "$(wc -c < "$work/get" | tr -d ' ')" ] \
        || fail "GET $url: Content-Length $(header content-length "$work/get-headers") does not match the $(wc -c < "$work/get" | tr -d ' ')-byte body"
    curl -sf --compressed -H 'Accept-Encoding: gzip, br' -o "$work/compressed" "$url" || fail "GET $url accepting gzip and br"
    grep -q "$SCRIPT_PATTERN" "$work/compressed" || fail "GET $url accepting gzip and br has no home client script tag"
    curl -sf -I -o /dev/null -D "$work/head-headers" "$url" || fail "HEAD $url"
    [ "$(header content-length "$work/head-headers")" = "$(header content-length "$work/get-headers")" ] \
        || fail "HEAD $url answers Content-Length $(header content-length "$work/head-headers"), GET $(header content-length "$work/get-headers")"
    etag="$(header etag "$work/get-headers")"
    [ -n "$etag" ] && [ "$(header etag "$work/head-headers")" = "$etag" ] \
        || fail "HEAD $url answers ETag '$(header etag "$work/head-headers")', GET '$etag'"
    rm -f "$work/not-modified"
    code="$(curl -s -o "$work/not-modified" -w '%{http_code}' -H "If-None-Match: $etag" "$url")"
    [ "$code" = "304" ] && [ ! -s "$work/not-modified" ] || fail "GET $url with its ETag returned $code, expected an empty 304"
    MSYS_NO_PATHCONV=1 docker exec "$container" cat /jellyfin/jellyfin-web/index.html > "$work/stock" || fail "read the stock index.html"
    ! grep -q "ExperienceManager/home" "$work/stock" || fail "jellyfin-web's index.html on disk carries the home client tags"
    pass "$url is sent plain when compression is accepted, with a matching Content-Length, HEAD headers equal to GET's, an ETag that gets 304, and the file on disk stock"
}

# On its own: the plugin, its image, its admin page, and the home client added to jellyfin-web.
check_plugin_active "$base" "$PLUGIN_NAME"
code="$(get_status -H "$auth" "$base/Plugins")"
entry="$(sed 's/},{/}\n{/g' "$work/body" | grep "\"Name\":\"$PLUGIN_NAME\"" || true)"
[ "$(echo "$entry" | json_str Version)" = "$PLUGIN_VERSION" ] || fail "$PLUGIN_NAME is not version $PLUGIN_VERSION: $entry"

# Dashboard → Plugins shows the image the server answers for the plugin.
echo "$entry" | grep -q '"HasImage":true' || fail "/Plugins says $PLUGIN_NAME has no image"
id="$(echo "$entry" | json_str Id)"
code="$(curl -s -o "$work/image" -D "$work/image-headers" -w '%{http_code}' -H "$auth" "$base/Plugins/$id/$PLUGIN_VERSION/Image")"
[ "$code" = "200" ] || fail "GET /Plugins/$id/$PLUGIN_VERSION/Image returned $code"
grep -qi '^content-type: image/png' "$work/image-headers" || fail "the plugin image is not served as image/png"
[ "$(md5sum < "$work/image")" = "$(md5sum < "$repo/images/icon.png")" ] || fail "the plugin image served is not images/icon.png"
pass "the server serves images/icon.png as $PLUGIN_NAME's image"

check_no_create_error

# The Dashboard builds its sidebar from the pages listed with enableInMainMenu.
code="$(get_status -H "$auth" "$base/web/ConfigurationPages?enableInMainMenu=true")"
[ "$code" = "200" ] || fail "GET /web/ConfigurationPages?enableInMainMenu=true returned $code"
page="$(sed 's/},{/}\n{/g' "$work/body" | grep "\"Name\":\"$PAGE_NAME\"" || true)"
[ -n "$page" ] || fail "the Dashboard sidebar does not list $PAGE_NAME; pages: $(cat "$work/body")"
[ "$(echo "$page" | json_str DisplayName)" = "$PLUGIN_NAME" ] || fail "the sidebar entry is not named $PLUGIN_NAME: $page"
[ -n "$(echo "$page" | json_str MenuIcon)" ] || fail "the sidebar entry has no icon: $page"
pass "the Dashboard sidebar lists $PLUGIN_NAME with its icon"

code="$(get_status -H "$auth" "$base/web/ConfigurationPage?name=$PAGE_NAME")"
[ "$code" = "200" ] || fail "GET /web/ConfigurationPage?name=$PAGE_NAME returned $code"
grep -q 'pluginConfigurationPage' "$work/body" || fail "the admin page served is not the plugin's page"
pass "the server serves $PLUGIN_NAME's admin page"

# The public home API: discovery without a token, every v1 route refused without one, the page's
# sections in order with per-section timing and an ETag that gets 304, and no Home Screen Sections routes.
code="$(get_status "$base/ExperienceManager/Info")"
[ "$code" = "200" ] && grep -q '"ApiVersions":\["v1"\]' "$work/body" || fail "anonymous GET /ExperienceManager/Info returned $code: $(cat "$work/body")"
pass "anonymous GET /ExperienceManager/Info reports API v1"
for path in "ExperienceManager/v1/Pages/home" "ExperienceManager/v1/Sections/featured/Items"; do
    code="$(get_status "$base/$path")"
    [ "$code" = "401" ] || fail "GET /$path without a token returned $code, expected 401"
done
pass "the v1 routes answer 401 without a token"
code="$(curl -s -D "$work/page-headers" -o "$work/body" -w '%{http_code}' -H "$auth" "$base/ExperienceManager/v1/Pages/home?include=items")"
[ "$code" = "200" ] || fail "GET /ExperienceManager/v1/Pages/home returned $code: $(cat "$work/body")"
ids="$(grep -o '"Id":"[a-z]*"' "$work/body" | sed 's/"Id":"\([a-z]*\)"/\1/' | tr '\n' ' ')"
[ "$ids" = "featured continue nextup " ] || fail "the page's sections are '$ids', expected 'featured continue nextup ' on a server with no libraries"
grep -q '"Hint":"feature","Shape":"landscape"' "$work/body" || fail "the featured section has no feature hint: $(cat "$work/body")"
grep -q '"Items":{"Items":\[\]' "$work/body" || fail "include=items did not inline the sections' items: $(cat "$work/body")"
grep -qiE '^server-timing: *featured;desc="(hit|miss)";dur=.*continue;desc="(hit|miss)";dur=.*nextup;desc=' "$work/page-headers" || fail "the page has no per-section Server-Timing: $(grep -i '^server-timing' "$work/page-headers")"
etag="$(grep -i '^etag:' "$work/page-headers" | sed 's/^[^:]*: *//' | tr -d '\r')"
code="$(get_status -H "$auth" -H "If-None-Match: $etag" "$base/ExperienceManager/v1/Pages/home?include=items")"
[ "$code" = "304" ] || fail "the page with its ETag returned $code, expected 304"
pass "GET /ExperienceManager/v1/Pages/home lists featured, Continue Watching and Next Up with items, Server-Timing and an ETag that gets 304"
code="$(get_status -H "$auth" "$base/ExperienceManager/v1/Sections/continue/Items?startIndex=0&limit=5")"
[ "$code" = "200" ] && grep -q '"TotalRecordCount":0' "$work/body" || fail "GET a section's items returned $code: $(cat "$work/body")"
code="$(get_status -H "$auth" "$base/ExperienceManager/v1/Sections/no-such-section/Items")"
[ "$code" = "404" ] || fail "an unknown section returned $code, expected 404"
pass "section items are paged, and an unknown section is 404"
for path in "HomeScreen/Sections" "HomeScreen/Section/ContinueWatching"; do
    code="$(get_status -H "$auth" "$base/$path")"
    [ "$code" = "404" ] || fail "GET /$path returned $code; the plugin must not serve Home Screen Sections' routes"
done
pass "none of Home Screen Sections' /HomeScreen routes are served"
code="$(get_status -H "$auth" "$base/ScheduledTasks")"
grep -q '"Name":"Warm the home screen cache"' "$work/body" || fail "Dashboard → Scheduled Tasks does not list the cache warm-up task"
pass "Dashboard → Scheduled Tasks lists the home screen cache warm-up"

grep -q 'Home client added to jellyfin-web' "$work/server.log" \
    || fail "the plugin did not log that it adds the home client to jellyfin-web"
pass "the plugin logs that it adds the home client to jellyfin-web"
check_client_served "$base"
check_web_changed "$base" "on its own"
check_web_http "$base"

# With File Transformation installed alongside, for another plugin.
MSYS_NO_PATHCONV=1 docker exec "$container" cp -r /experience-manager-ft "/config/plugins/FileTransformation_$FT_VERSION" || fail "install File Transformation"
restart ""
base="$(base_url "")"
check_plugin_active "$base" "File Transformation"
check_plugin_active "$base" "$PLUGIN_NAME"
check_no_create_error
check_client_served "$base"
check_web_changed "$base" "with File Transformation alongside"

# Under a BaseUrl.
code="$(get_status -H "$auth" "$base/System/Configuration/network")"
[ "$code" = "200" ] || fail "GET network configuration returned $code"
sed 's/"BaseUrl":"[^"]*"/"BaseUrl":"\/jf"/' "$work/body" > "$work/network.json"
grep -q '"BaseUrl":"/jf"' "$work/network.json" || fail "could not set BaseUrl in network configuration"
curl -sf -X POST -H 'Content-Type: application/json' -H "$auth" \
    --data-binary "@$work/network.json" "$base/System/Configuration/network" >/dev/null || fail "POST network configuration"
restart "/jf"
base="$(base_url "/jf")"
check_client_served "$base"
check_web_changed "$base" "under BaseUrl /jf"
check_web_http "$base"

echo "smoke-test: all checks passed"
