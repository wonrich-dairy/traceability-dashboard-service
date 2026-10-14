# Performance testing (JMeter)

A JMeter load test runs against staging after every deployment, so NFR1 ("the system responds within 2 seconds") is measured on each release (SCRUM-123, same approach as Quality Lab's SCRUM-43).

| | |
|---|---|
| Test plan | `perf/traceability.jmx` |
| Workflow | `.github/workflows/performance.yml` |
| Runs | after every successful staging deploy (the `performance` job in `ci-cd.yml`), and by hand |
| Result | pass / fail on p95 latency and error rate, plus an HTML report artifact |

**Staging only.** Never point it at production: Free-tier App Services have a daily CPU quota.

---

## What it tests

Each simulated user repeats this loop, with a pause of 1.0 to 1.5 s before each request:

| Request | Endpoint |
|---|---|
| `read: service info` | `GET /` |
| `read: version` | `GET /version` |

These are the endpoints the service has from the start. `/health` is not load tested: each call opens a Kafka connection, so it is meant for probes, and on a free-tier app it times out under load. **Add a `read:` sampler to `perf/traceability.jmx` for each API endpoint the service gains** (for example the dashboard summary and the batch trace), so the 2-second target covers the real queries. Only samplers labelled `read:` or `write:` are measured.

No sign-in is needed for these endpoints. Every request carries an `X-Correlation-ID` of `jmeter-<uuid>`, so load-test traffic is easy to find or exclude in the logs.

**Setup, not measured:** one warm-up request to `/health` (Free-tier App Services cold-start). It is labelled `setup:` and filtered out of the report.

The test writes nothing.

---

## Seed data for the batch endpoints

The batch record endpoints (`/api/batches/…`, SCRUM-133) and the dashboard (`/api/dashboard/…`, SCRUM-135) need data to read. `perf/seed-batches.sql` generates 1,000 batches, `PERF-1000` to `PERF-1999`, at their own facility, `FACTORY-PERF`, each with an upstream snapshot in the contract shape, timeline events and some deviations. It also fills both daily aggregate tables from them, as the consumers would. Two batches share each dispatch note and batch reference, `DN-PERF-1000` / `WR-PERF-1000` to `…-1499`, so the list lookups return two rows. The IDs are predictable so a sampler can pick one at random, e.g. `/api/batches/PERF-${__Random(1000,1999)}`.

The batches all fall in August 2026, so the dashboard samplers ask for `?facility=FACTORY-PERF&from=2026-08-01&to=2026-08-31`. Over that range the societies panel reads all 980 resolved snapshots, the heaviest query the dashboard has.

The batch search samplers (SCRUM-139, DoD: p95 under 2 s against at least 1,000 batches) use the same 1,000 batches. `read: batch search` pages through them at random (`page=1…20`). `read: batch search by society` and `read: batch search by tank and user` filter through the snapshot JSON (societies `VS010`–`VS050`, tank `T2`, user `processing.tech`). `read: batch export` writes all of them as CSV, the heaviest of the four. `BatchSearchEndpointTests.Search_and_export_answer_within_two_seconds_over_a_thousand_batches` also checks these queries against 1,000 batches on every CI run, without load, and writes each one's time to the test output.

Load it into the **staging** database before the run, and remove it afterwards:

```bash
mysql -h wonrichmysql.mysql.database.azure.com -u trc_app -p \
  --ssl-mode=REQUIRED traceability < perf/seed-batches.sql
mysql … traceability < perf/reset-seed-batches.sql    # removes every PERF- batch, nothing else
```

Both scripts can be run twice safely. Never run them against `traceability_prod`.

---

## Load profile

| Setting | Default | Input |
|---|---|---|
| Concurrent users | 10 | `users` |
| Ramp-up | 30 s | `ramp_seconds` |
| Duration | 120 s | `duration_seconds` |
| Pause between a user's requests | 1000 ms + up to 500 ms random | `think_ms` |

## Thresholds

| Metric | Fails when | Input |
|---|---|---|
| p95 latency, all measured requests | above **2000 ms** (NFR1) | `p95_ms` |
| Error rate | above **1%** | `max_error_pct` |

All defaults live in `performance.yml`.

---

## Running it

**By hand:** Actions → **Performance test (JMeter)** → **Run workflow**. Every input above can be overridden.

Configuration on the `staging` environment: the `APP_HOST` variable (already set for deployment). `PERF_TARGET_HOST` overrides it if set.

**Locally**, against the container from `docker compose up`:

```bash
jmeter -n -t perf/traceability.jmx -l results.jtl -e -o report \
  -Jprotocol=http -Jhost=localhost -Jport=5240 \
  -Jusers=3 -Jduration=30 \
  "-Jjmeter.reportgenerator.sample_filter=^(read|write): .*"
bash perf/check-thresholds.sh report/statistics.json 2000 1
```

---

## Reading the report

The run summary shows a table of every request with its sample count, p95 and error rate. The full report is the **`jmeter-report`** artifact: unzip it and open `report/index.html` (**Dashboard → Statistics** for p95 and errors, **Errors** for what failed).

---

## Proving the gate works

Run the workflow by hand with **`p95_ms` = `1`**. No request over the internet answers in 1 ms, so the job fails with *"p95 latency … is above the 1 ms threshold"*.

| Item | Evidence |
|---|---|
| Green run on staging, report attached | _run link_ |
| Deliberately tightened threshold fails the job | _run link_ |
