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
| `read: health` | `GET /health` |

These are the endpoints the service has from the start. **Add a `read:` sampler to `perf/traceability.jmx` for each API endpoint the service gains** (for example the dashboard summary and the batch trace), so the 2-second target covers the real queries. Only samplers labelled `read:` or `write:` are measured.

No sign-in is needed for these endpoints. Every request carries an `X-Correlation-ID` of `jmeter-<uuid>`, so load-test traffic is easy to find or exclude in the logs.

**Setup, not measured:** one warm-up request to `/health` (Free-tier App Services cold-start). It is labelled `setup:` and filtered out of the report.

The test writes nothing.

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
