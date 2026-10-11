#!/usr/bin/env bash
# Pass / fail a JMeter run against its thresholds (SCRUM-43, reused for SCRUM-123).
#
#   bash perf/check-thresholds.sh <report-dir>/statistics.json <p95-max-ms> <max-error-pct>
#
# Reads the statistics.json that JMeter's HTML report generator writes. Only the measured samplers
# ('read:' / 'write:') are in it; the warm-up is filtered out when the report is generated.
# Writes a per-request table to the GitHub run summary and fails on a breach.
set -euo pipefail

STATS="${1:?usage: $0 <statistics.json> <p95-max-ms> <max-error-pct>}"
P95_MAX="${2:?p95 threshold (ms) missing}"
ERR_MAX="${3:?error-rate threshold (%) missing}"
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/null}"

if [ ! -f "$STATS" ]; then
  echo "::error::No $STATS: the run produced no measured requests. Check jmeter.log in the artifact."
  exit 1
fi

samples=$(jq '.Total.sampleCount // 0' "$STATS")
p95=$(jq '.Total.pct2ResTime // 0' "$STATS")
err=$(jq '.Total.errorPct // 0' "$STATS")

{
  echo "### Performance test (JMeter)"
  echo
  echo "| Request | Samples | p95 (ms) | Errors |"
  echo "|---|---:|---:|---:|"
  jq -r 'to_entries | map(select(.key != "Total")) | sort_by(.key)[]
         | "| \(.key) | \(.value.sampleCount) | \(.value.pct2ResTime | round) | \((.value.errorPct * 100 | round) / 100)% |"' "$STATS"
  jq -r '.Total | "| **All requests** | **\(.sampleCount)** | **\(.pct2ResTime | round)** | **\((.errorPct * 100 | round) / 100)%** |"' "$STATS"
  echo
  echo "Thresholds: p95 ≤ ${P95_MAX} ms, errors ≤ ${ERR_MAX}%. Full report: the \`jmeter-report\` artifact (open \`report/index.html\`)."
} | tee -a "$SUMMARY"

failed=0
if [ "$samples" -eq 0 ]; then
  echo "::error::No requests were measured."
  failed=1
fi
if awk -v a="$p95" -v b="$P95_MAX" 'BEGIN { exit !(a > b) }'; then
  echo "::error::p95 latency ${p95} ms is above the ${P95_MAX} ms threshold (NFR1)."
  failed=1
fi
if awk -v a="$err" -v b="$ERR_MAX" 'BEGIN { exit !(a > b) }'; then
  echo "::error::Error rate ${err}% is above the ${ERR_MAX}% threshold."
  failed=1
fi

if [ "$failed" -eq 0 ]; then
  echo "Thresholds met: p95 ${p95} ms, errors ${err}% over ${samples} requests."
fi
exit "$failed"
