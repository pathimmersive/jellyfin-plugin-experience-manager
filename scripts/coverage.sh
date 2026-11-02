#!/usr/bin/env bash
# Runs the plugin tests with coverage and prints a summary. It reports coverage;
# it does not fail on a percentage. It fails if a test fails or the report is missing.
#
# Usage: scripts/coverage.sh
#
# Needs: bash, the .NET 10 SDK.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

dotnet test --collect "Code Coverage;Format=cobertura" --results-directory "$out"
report="$(find "$out" -name '*.cobertura.xml' | head -1)"
[ -n "$report" ] || fail "dotnet test wrote no coverage report"

# The plugin's package only, not the test assembly's.
package="$(grep -o '<package [^>]*name="Jellyfin.Plugin.ExperienceManager"[^>]*>' "$report" || true)"
[ -n "$package" ] || fail "the coverage report has no Jellyfin.Plugin.ExperienceManager package"
rate() { echo "$package" | sed -E "s/.* $1=\"([0-9.]+)\".*/\1/" | awk '{ printf "%.1f%%", $1 * 100 }'; }

echo
echo "Coverage"
echo "  plugin   lines $(rate line-rate), branches $(rate branch-rate)"
