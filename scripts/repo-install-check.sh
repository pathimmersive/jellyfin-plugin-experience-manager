#!/usr/bin/env bash
# Proves a Jellyfin server installs the plugin the way an admin does: packages the
# plugin, serves the zip and manifest.json from a local web container, adds that URL
# as a plugin repository on a local Jellyfin, installs it through the API, restarts
# the server, and checks the plugin is active with its image and its admin page is
# listed in the Dashboard sidebar and served. No other plugin is installed.
#
# Usage: scripts/repo-install-check.sh [--upgrade] [--corrupt-zip]
#   --upgrade      also package a newer version, install it over the first from the
#                  same repository, and check only the newer one is active
#   --corrupt-zip  change a zip after its checksum is computed (the newer one with
#                  --upgrade); its install must fail
#
# Needs: bash, curl, docker, the .NET 10 SDK.
# Environment:
#   JELLYFIN_IMAGE  image to run (default: the one scripts/smoke-test.sh pins)
#   CHECK_TIMEOUT   seconds to wait for each step (default 180)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12.2.20261005-225228}"
timeout="${CHECK_TIMEOUT:-180}"
corrupt=0
upgrade=0
for arg in "$@"; do
  case "$arg" in
    --corrupt-zip) corrupt=1 ;;
    --upgrade) upgrade=1 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

meta="$(bash scripts/plugin-meta.sh)"
plugin_name="$(sed -n 's/.*"name": *"\([^"]*\)".*/\1/p' "$meta")"
plugin_guid="$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$meta")"
version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$meta")"
# The newer version for --upgrade: the same build with its last part raised.
next="${version%.*}.$(( ${version##*.} + 1 ))"
page_name="ExperienceManager"

suffix="$$"
network="experience-manager-check-$suffix"
repo_container="experience-manager-repo-$suffix"
server_container="experience-manager-server-$suffix"
repo_host="experience-manager-repo"
repo_url="http://$repo_host/manifest.json"
out_dir="artifacts/repo-check"
served="$(mktemp)"

cleanup() {
  rm -f "$served"
  docker rm -f "$server_container" "$repo_container" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

# The newer version is packaged apart and joins the manifest only after the first is
# checked, as a new release would; listed from the start, Jellyfin's own plugin
# update would install it at the first restart.
next_dir="$out_dir/next"
rm -rf "$out_dir"
versions=("$version")
[[ $upgrade -eq 1 ]] && versions+=("$next")
echo "== package $version"
bash scripts/package.sh "$version" "$out_dir" "http://$repo_host" "Local repository check"
if [[ $upgrade -eq 1 ]]; then
  echo "== package $next"
  bash scripts/package.sh "$next" "$next_dir" "http://$repo_host" "Local repository check"
fi
if [[ $corrupt -eq 1 ]]; then
  last_dir="$out_dir"
  [[ $upgrade -eq 1 ]] && last_dir="$next_dir"
  echo "== corrupt the $plugin_name ${versions[-1]} zip after its checksum was computed"
  printf 'corrupted' >>"$last_dir/experience-manager_${versions[-1]}.zip"
fi
bash scripts/build-manifest.sh "$out_dir/manifest.json" --entries "$out_dir"

echo "== serve the repository"
docker network create "$network" >/dev/null
docker create --name "$repo_container" --network "$network" --network-alias "$repo_host" nginx:alpine >/dev/null
docker cp "$out_dir/manifest.json" "$repo_container:/usr/share/nginx/html/manifest.json"
docker cp "$out_dir/experience-manager_$version.zip" "$repo_container:/usr/share/nginx/html/"
docker start "$repo_container" >/dev/null

echo "== start $image"
docker pull --quiet "$image" >/dev/null
docker create --name "$server_container" --network "$network" -p 127.0.0.1::8096 "$image" >/dev/null
docker start "$server_container" >/dev/null

# The server is published on a new host port each time it starts.
find_api() {
  local port
  port="$(docker port "$server_container" 8096/tcp | head -n 1 | sed 's/.*://')"
  api="http://127.0.0.1:$port"
}
find_api

# Waits until the server log matches a pattern; fails on timeout or container exit.
wait_for_log() {
  local pattern="$1" deadline=$((SECONDS + timeout))
  while (( SECONDS < deadline )); do
    if docker logs "$server_container" 2>&1 | grep -Eq "$pattern"; then
      return 0
    fi
    if [[ "$(docker inspect -f '{{.State.Running}}' "$server_container")" != "true" ]]; then
      docker logs --tail 50 "$server_container" 2>&1 || true
      fail "server exited while waiting for: $pattern"
    fi
    sleep 2
  done
  docker logs --tail 50 "$server_container" 2>&1 || true
  fail "timed out waiting for: $pattern"
}

auth='MediaBrowser Client="experience-manager-check", Device="script", DeviceId="experience-manager-check", Version="1.0.0"'
call() {
  local method="$1" path="$2" body="${3:-}"
  if ! curl -sS --fail-with-body -X "$method" "$api$path" \
    -H "Authorization: $auth${token:+, Token=\"$token\"}" \
    -H "Content-Type: application/json" \
    ${body:+--data "$body"}; then
    echo >&2
    docker logs --tail 40 "$server_container" 2>&1 | grep -A4 -E '\[(ERR|WRN)\]' >&2 || true
    fail "$method $path was refused"
  fi
}

# Installs a package from a repository the way Dashboard → Plugins does, and waits
# for its files; fails as soon as the server reports a failed install.
install() {
  local name="$1" guid="$2" v="$3" from="$4" dll="$5" deadline
  echo "== install $name $v from $from"
  call POST "/Packages/Installed/${name// /%20}?assemblyGuid=$guid&version=$v&repositoryUrl=$from"
  deadline=$((SECONDS + timeout))
  while true; do
    if docker logs "$server_container" 2>&1 | grep -iE "checksum|installation.*fail|failed.*install" >&2; then
      docker logs "$server_container" 2>&1 | grep -iE -A12 "installation.*fail|failed.*install" | head -40 >&2 || true
      fail "the server refused to install $name $v"
    fi
    if docker exec "$server_container" sh -c "ls /config/plugins/*_$v/$dll" >/dev/null 2>&1; then
      return 0
    fi
    (( SECONDS < deadline )) || fail "timed out waiting for the install of $name $v"
    sleep 2
  done
}

# Restarts the server and checks what loaded: the plugin active at version v with its
# image, and its admin page in the sidebar and served.
restart_and_check() {
  local v="$1" plugins entries entry page
  echo "== restart and wait for $plugin_name $v to load"
  docker restart "$server_container" >/dev/null
  wait_for_log "Loaded plugin: \"?${plugin_name}\"? \"?${v}\"?"
  find_api
  local deadline=$((SECONDS + timeout))
  until curl -sf "$api/System/Info/Public" >/dev/null 2>&1; do
    (( SECONDS < deadline )) || fail "the server did not answer after the restart"
    sleep 2
  done

  plugins="$(call GET /Plugins)"
  entries="$(sed 's/},{/}\n{/g' <<<"$plugins")"
  entry="$(grep "\"Name\":\"$plugin_name\"" <<<"$entries" | grep '"Status":"Active"' || true)"
  [[ "$(grep -c . <<<"$entry")" -eq 1 ]] || fail "expected one Active $plugin_name, /Plugins lists: $(grep "\"Name\":\"$plugin_name\"" <<<"$entries" || echo none)"
  grep -q "\"Version\":\"$v\"" <<<"$entry" || fail "the Active $plugin_name is not version $v: $entry"
  grep -q '"HasImage":true' <<<"$entry" || fail "the installed $plugin_name has no image in Dashboard → Plugins"
  local id
  id="$(grep -o '"Id":"[^"]*"' <<<"$entry" | head -n 1 | cut -d'"' -f4)"
  curl -sSf -o "$served" -H "Authorization: $auth, Token=\"$token\"" "$api/Plugins/$id/$v/Image" || fail "GET the installed $plugin_name's image failed"
  cmp -s "$served" images/icon.png || fail "the installed $plugin_name's image is not images/icon.png"

  call GET "/web/ConfigurationPages?enableInMainMenu=true" | grep -q "\"Name\":\"$page_name\"" \
    || fail "the Dashboard sidebar does not list $page_name"
  page="$(call GET "/web/ConfigurationPage?name=$page_name")"
  grep -q 'pluginConfigurationPage' <<<"$page" || fail "the admin page served is not the plugin's page"
  echo "PASS: $plugin_name $v is Active with its image, and its admin page is in the sidebar and served"
}

wait_for_log "Startup complete"
server_version="$(docker logs "$server_container" 2>&1 | sed -n 's/.*Jellyfin version: \([0-9.]*\).*/\1/p' | head -n 1)"

echo "== complete the startup wizard"
token=""
call POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
call GET /Startup/User >/dev/null
call POST /Startup/User '{"Name":"check","Password":"check-password"}'
call POST /Startup/Complete
token="$(call POST /Users/AuthenticateByName '{"Username":"check","Pw":"check-password"}' |
  sed -n 's/.*"AccessToken":"\([^"]*\)".*/\1/p')"
[[ -n "$token" ]] || fail "could not sign in to the server"

echo "== add the repository"
call POST /Repositories "[{\"Name\":\"Experience Manager check\",\"Url\":\"$repo_url\",\"Enabled\":true}]"
packages="$(call GET /Packages)"
grep -q "\"name\":\"$plugin_name\"" <<<"$packages" || fail "the server does not list $plugin_name from $repo_url"

install "$plugin_name" "$plugin_guid" "$version" "$repo_url" Jellyfin.Plugin.ExperienceManager.dll
restart_and_check "$version"

if [[ $upgrade -eq 1 ]]; then
  echo "== publish $plugin_name $next in the repository"
  cp "$next_dir/experience-manager_$next.json" "$out_dir/"
  bash scripts/build-manifest.sh "$out_dir/manifest.json" --entries "$out_dir"
  docker cp "$next_dir/experience-manager_$next.zip" "$repo_container:/usr/share/nginx/html/"
  docker cp "$out_dir/manifest.json" "$repo_container:/usr/share/nginx/html/manifest.json"
  call GET /Packages | grep -q "\"version\":\"$next\"" || fail "the server does not list $plugin_name $next from $repo_url"
  install "$plugin_name" "$plugin_guid" "$next" "$repo_url" Jellyfin.Plugin.ExperienceManager.dll
  restart_and_check "$next"
fi

echo "PASS: $image (Jellyfin ${server_version:-unknown}) installed $plugin_name ${versions[*]} from its repository, with no other plugin"
