#!/usr/bin/env bash
# NuGet dependency audit (SCRUM-112).
#
# Lists known-vulnerable packages, direct and transitive, in the given projects.
# High and Critical fail; Moderate and Low are reported but do not block.
#
#   dotnet restore <project>
#   bash scripts/nuget-audit.sh <project> [<project>...]
#
# Needs jq. Advisories listed in .nuget-audit-ignore are skipped; each needs a reason there.
set -euo pipefail

[ $# -gt 0 ] || { echo "usage: $0 <project>..." >&2; exit 2; }

findings="$(mktemp)"
trap 'rm -f "$findings"' EXIT

for project in "$@"; do
  dotnet list "$project" package --vulnerable --include-transitive --format json |
    jq -r '
      .projects[]? | (.path | split("/") | last) as $project |
      .frameworks[]? |
      ([(.topLevelPackages // [])[] | . + {ref: "direct"}] +
       [(.transitivePackages // [])[] | . + {ref: "transitive"}])[] |
      select(.vulnerabilities) | . as $pkg |
      .vulnerabilities[] |
      [.severity, $pkg.id, $pkg.resolvedVersion, $pkg.ref, $project, .advisoryurl] | @tsv' \
    >> "$findings"
done

# Triaged advisories: one URL per line in .nuget-audit-ignore, anything after '#' is the reason.
ignore_file="$(dirname "$0")/../.nuget-audit-ignore"
if [ -f "$ignore_file" ]; then
  ignored="$(sed 's/#.*//; s/[[:space:]]//g; /^$/d' "$ignore_file")"
  if [ -n "$ignored" ]; then
    grep -v -F "$ignored" "$findings" > "$findings.kept" || true
    mv "$findings.kept" "$findings"
  fi
fi

sort -u -o "$findings" "$findings"
total=$(wc -l < "$findings" | tr -d ' ')
blocking=$(awk -F'\t' '$1 == "High" || $1 == "Critical"' "$findings" | wc -l | tr -d ' ')

report() {
  echo "### NuGet dependency audit"
  echo
  if [ "$total" -eq 0 ]; then
    echo "No known-vulnerable packages."
    return
  fi
  echo "| Severity | Package | Version | Reference | Project | Advisory |"
  echo "|---|---|---|---|---|---|"
  awk -F'\t' '{ printf "| %s | %s | %s | %s | %s | %s |\n", $1, $2, $3, $4, $5, $6 }' "$findings"
  echo
  echo "High / Critical: **$blocking** (blocking). Total: $total."
}

report
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then report >> "$GITHUB_STEP_SUMMARY"; fi

if [ "$blocking" -gt 0 ]; then
  echo "::error::$blocking High or Critical vulnerable package(s). Upgrade them, or triage in .nuget-audit-ignore with a reason."
  exit 1
fi
