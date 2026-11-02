#!/usr/bin/env bash
# Checks that the repository carries its community files, that the README
# says the plugin is unofficial, that the issue chooser offers forms and the
# question and private security routes, and that CHANGELOG.md has a section
# for unreleased changes or a version.
#
# Usage: scripts/check-community-files.sh
#
# Exits non-zero listing everything missing.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"
missing=0

fail() { echo "FAIL: $*" >&2; missing=$((missing + 1)); }
pass() { echo "PASS: $*"; }

for file in README.md LICENSE CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md THIRD_PARTY_NOTICES.md \
    CHANGELOG.md .github/pull_request_template.md .github/ISSUE_TEMPLATE/bug_report.yml \
    .github/ISSUE_TEMPLATE/feature_request.yml; do
    if [ -s "$file" ]; then pass "$file"; else fail "$file is missing or empty"; fi
done
if ls .github/ISSUE_TEMPLATE/*.md >/dev/null 2>&1; then
    fail ".github/ISSUE_TEMPLATE still has markdown templates; issues use forms"
else
    pass ".github/ISSUE_TEMPLATE uses issue forms only"
fi

chooser=.github/ISSUE_TEMPLATE/config.yml
if grep -q 'blank_issues_enabled: false' "$chooser" 2>/dev/null; then
    pass "$chooser turns off blank issues"
else
    fail "$chooser does not turn off blank issues"
fi
if grep -q '/discussions' "$chooser" 2>/dev/null; then
    pass "$chooser links Discussions for questions"
else
    fail "$chooser does not link Discussions for questions"
fi
if grep -q '/security/advisories/new' "$chooser" 2>/dev/null; then
    pass "$chooser links the private security report form"
else
    fail "$chooser does not link the private security report form"
fi

if grep -q -E '^## \[(Unreleased|[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?)\]' CHANGELOG.md 2>/dev/null; then
    pass "CHANGELOG.md has an Unreleased or version section"
else
    fail "CHANGELOG.md has no Unreleased or version section"
fi

if [ -f README.md ]; then
    if grep -q -i 'unofficial' README.md; then
        pass "README.md says the plugin is unofficial"
    else
        fail "README.md does not say the plugin is unofficial"
    fi
fi

if [ "$missing" -gt 0 ]; then
    echo "check-community-files: $missing missing" >&2
    exit 1
fi
echo "check-community-files: all present"
