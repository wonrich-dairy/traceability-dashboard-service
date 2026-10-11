#!/usr/bin/env bash
# Post-deploy verification (same script as Quality Lab, made reusable for every service).
#
#   scripts/verify-health.sh <app-host> [expected-git-sha]
#
# Passes only when:
#   1. /version reports the expected commit (skipped when no SHA is given, for services without /version), and
#   2. /health is not Unhealthy and its database check is Healthy.
#
# HEALTH_DB_CHECK names the database check (default "mysql"; Processing calls it "database").
# Both /health shapes are accepted: checks as an array of { name, status } (Quality Lab, Traceability)
# or as an object keyed by check name (Processing).
# Kafka may be Degraded during a broker outage; that does not fail the deployment.
set -euo pipefail

HOST="${1:?usage: $0 <app-host> [expected-git-sha]}"
EXPECTED_SHA="${2:-}"
DB_CHECK="${HEALTH_DB_CHECK:-mysql}"
ATTEMPTS="${ATTEMPTS:-20}"
DELAY="${DELAY:-15}"

# Accept a host stored with a scheme or a trailing slash.
HOST="${HOST#https://}"; HOST="${HOST%/}"

for i in $(seq 1 "$ATTEMPTS"); do
  if [ -n "$EXPECTED_SHA" ]; then
    running=$(curl -fsS --max-time 20 "https://$HOST/version" 2>/dev/null | jq -r '.sha' 2>/dev/null || true)
    if [ "$running" != "$EXPECTED_SHA" ]; then
      echo "Attempt $i/$ATTEMPTS: running version '${running:-unreachable}', waiting for $EXPECTED_SHA"
      sleep "$DELAY"; continue
    fi
  fi

  body=$(curl -sS --max-time 20 "https://$HOST/health" 2>/dev/null || true)
  if echo "$body" | jq -e --arg db "$DB_CHECK" '
        def db_checks:
          if (.checks | type) == "array"
          then [.checks[] | select(.name == $db)]
          else [.checks | to_entries[] | select(.key == $db) | .value]
          end;
        (.status != "Unhealthy") and (db_checks | length == 1 and .[0].status == "Healthy")
      ' >/dev/null 2>&1; then
    echo "$body" | jq .
    echo "OK: version ${EXPECTED_SHA:-(not checked)} is running and the '$DB_CHECK' check is Healthy"
    exit 0
  fi

  echo "Attempt $i/$ATTEMPTS: /health not healthy yet: ${body:-unreachable}"
  sleep "$DELAY"
done

echo "FAILED: /health did not report a healthy '$DB_CHECK' check within $((ATTEMPTS * DELAY))s" >&2
exit 1
