#!/usr/bin/env bash
# Smoke test: build the plugin, load it into a real Jellyfin 12.x container, and
# check what the server reports and serves: the plugin active with its image, its
# admin page listed in the Dashboard sidebar, and the page itself served. Exits
# non-zero on the first failed check and always removes the container.
#
# Needs: bash, curl, docker, md5sum, the .NET 10 SDK.
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

# --- server ----------------------------------------------------------------

MSYS_NO_PATHCONV=1 docker run -d --name "$container" \
    -p 127.0.0.1::8096 \
    -v "$(hostpath "$plugin_dir"):/config/plugins/$PLUGIN_DIR" \
    "$IMAGE" >/dev/null || fail "docker run $IMAGE"

base="http://127.0.0.1:$(docker port "$container" 8096/tcp | head -1 | sed 's/.*://')"

# While starting, the server answers with an HTML holding page, so a status code
# alone is not enough; wait for the JSON system info.
for _ in $(seq 1 120); do
    if curl -sf "$base/System/Info/Public" 2>/dev/null | grep -q '"StartupWizardCompleted"'; then
        break
    fi
    sleep 1
done
curl -sf "$base/System/Info/Public" 2>/dev/null | grep -q '"StartupWizardCompleted"' \
    || { docker logs "$container" 2>&1 | tail -40 >&2; fail "server at $base did not become ready"; }
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

code="$(get_status -H "$auth" "$base/Plugins")"
[ "$code" = "200" ] || { docker logs "$container" 2>&1 | grep -A8 -E "ERR|Error" | tail -30 >&2; fail "GET /Plugins returned $code"; }
entry="$(sed 's/},{/}\n{/g' "$work/body" | grep "\"Name\":\"$PLUGIN_NAME\"" || true)"
[ -n "$entry" ] || fail "$PLUGIN_NAME not listed by /Plugins"
[ "$(echo "$entry" | json_str Status)" = "Active" ] || fail "$PLUGIN_NAME status is '$(echo "$entry" | json_str Status)', expected Active"
[ "$(echo "$entry" | json_str Version)" = "$PLUGIN_VERSION" ] || fail "$PLUGIN_NAME is not version $PLUGIN_VERSION: $entry"
pass "/Plugins lists $PLUGIN_NAME $PLUGIN_VERSION as Active"

# Dashboard → Plugins shows the image the server answers for the plugin.
echo "$entry" | grep -q '"HasImage":true' || fail "/Plugins says $PLUGIN_NAME has no image"
id="$(echo "$entry" | json_str Id)"
code="$(curl -s -o "$work/image" -D "$work/image-headers" -w '%{http_code}' -H "$auth" "$base/Plugins/$id/$PLUGIN_VERSION/Image")"
[ "$code" = "200" ] || fail "GET /Plugins/$id/$PLUGIN_VERSION/Image returned $code"
grep -qi '^content-type: image/png' "$work/image-headers" || fail "the plugin image is not served as image/png"
[ "$(md5sum < "$work/image")" = "$(md5sum < "$repo/images/icon.png")" ] || fail "the plugin image served is not images/icon.png"
pass "the server serves images/icon.png as $PLUGIN_NAME's image"

# Read the whole log first: grep -q stops early, and under pipefail the cut-off
# docker logs would fail the check.
docker logs "$container" > "$work/server.log" 2>&1
! grep -q 'Error creating Jellyfin.Plugin.ExperienceManager' "$work/server.log" || fail "the server log has an error creating the plugin"
pass "the server log has no error creating the plugin"

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

echo "smoke-test: all checks passed"
