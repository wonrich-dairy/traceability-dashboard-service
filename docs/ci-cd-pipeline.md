# CI/CD pipeline

Traceability Service is built, tested, scanned and deployed by GitHub Actions (SCRUM-123), independently of the other services. The shape is Quality Lab's (SCRUM-109, SCRUM-42, SCRUM-112) with a production stage added.

| Workflow | File | Runs on |
|---|---|---|
| CI/CD | `.github/workflows/ci-cd.yml` | Every push to `develop` / `main`, every pull request, manual |
| Rollback | `.github/workflows/rollback.yml` | Manual only |
| Performance test | `.github/workflows/performance.yml` | After every staging deploy, and manual |
| CodeQL | `.github/workflows/codeql.yml` | Every push and pull request, weekly |

---

## Environments

| | Staging | Production |
|---|---|---|
| GitHub environment | `staging` | `production` (required reviewers, `main` only) |
| Deploys from | `develop`, automatically | `main`, after approval |
| App Service | `wonrich-traceability` | `wonrich-traceability-prod` |
| Image tag the app follows | `latest` | `production` |
| Rollback tag | `previous` | `production-previous` |
| `ASPNETCORE_ENVIRONMENT` | `Staging` | `Production` |
| Database | `traceability` | `traceability_prod` |
| Migrations | applied by the app on startup | applied by the pipeline from the migration script, before the deploy |

Image: `wonrichtrcacr.azurecr.io/traceability`. App names and URLs for every service are in `wonrich-infra/docs/environments.md`.

---

## Pipeline stages

```
push / PR
   │
   ├── Dependency audit ─────────┐
   └── Build and test ───────────┤  restore, build, unit tests, integration tests, coverage gate,
                                 │  migration script, docker build, image scan
                                 │
         push to develop ────────┼──► Deploy to staging ──► Performance
                                 │
         push to main ───────────┴──► Deploy to production (waits for approval)
```

### 1. Dependency audit (every push and PR)

NuGet packages, direct and transitive, in all three projects. High and Critical fail. See [security-scanning.md](security-scanning.md).

### 2. Build and test (every push and PR)

| Step | Purpose |
|---|---|
| Unit tests | `tests/TraceabilityService.UnitTests`. A failing test fails the job, and nothing deploys |
| Integration tests | `tests/TraceabilityService.IntegrationTests`: the real API against a throwaway MySQL (Testcontainers). Also proves the migrations apply to an empty database |
| Test results, coverage | `.trx` results as a check, merged coverage report, PR comment, **60% line coverage** gate |
| Migration script | `dotnet ef migrations script --idempotent`, uploaded as the `migration-script` artifact |
| Docker build, image scan | Trivy; High / Critical with a fix available fail |

### 3. Deploy to staging (push to `develop`)

| Step | Purpose |
|---|---|
| Keep current image as `previous` | Rollback target |
| Build image | Tagged with the commit SHA and `latest`; the SHA is baked in as `GIT_SHA` |
| Push | Pushing `latest` fires the staging ACR webhook; App Service pulls the image and restarts |
| Verify | `scripts/verify-health.sh` waits until `/version` reports the new commit, then requires `/health` not `Unhealthy` and the `mysql` check `Healthy` |

### 4. Performance (after each staging deploy)

JMeter against staging, p95 ≤ 2000 ms and errors ≤ 1%. A separate job, so a regression fails the run but never undoes the deployment. See [performance-testing.md](performance-testing.md).

### 5. Deploy to production (push to `main`, after approval)

| Step | Purpose |
|---|---|
| Approval | `environment: production` pauses the job until a required reviewer approves it |
| Find the tested image | Uses the image staging built for the same code: the `main` commit itself, or, for a merge commit, the `develop` commit it merged (which must contain identical code). Production never runs an image staging has not run |
| Apply migrations | Runs the `migration-script` artifact against `traceability_prod`. It is idempotent, so an up-to-date database is left as it is |
| Keep current image as `production-previous` | Rollback target |
| Promote | Retags the tested image as `production`; the production ACR webhook makes App Service pull it |
| Verify | `verify-health.sh` against the production host and the promoted commit |

Release flow: merge `develop` into `main` through a pull request after `develop` has deployed to staging, then approve the production deployment.

---

## Rollback

Rollback restores an earlier image without rebuilding, on either environment.

1. GitHub → **Actions** → **Rollback** → **Run workflow**.
2. `environment`: `staging` or `production`.
3. `image_tag`:
   - `previous`: the deployment before the current one (`previous` on staging, `production-previous` on production)
   - a commit SHA: any earlier image (see **Container registry → traceability → Tags**)
4. The workflow points the live tag at that image; the webhook makes App Service pull it, and the same `/health` and `/version` verification runs.

To roll forward again, run Rollback with the newer commit SHA.

**The database is not rolled back.** Migrations must stay backward compatible: add columns and tables, do not rename or drop in the same release, so the previous version still runs on the new schema.

---

## Secrets and variables

No credentials are in workflow files. Everything is a GitHub **environment** secret or variable, so only a job that declares that environment can read it, and production values only after approval.

| Name | Type | `staging` | `production` |
|---|---|---|---|
| `ACR_LOGIN_SERVER` | Secret | `wonrichtrcacr.azurecr.io` | same |
| `ACR_USERNAME` | Secret | ACR token scoped to the `traceability` repository | same |
| `ACR_PASSWORD` | Secret | that token's password | same |
| `APP_HOST` | Variable | staging App Service host, without `https://` | production host |
| `DB_HOST` | Variable | | `wonrichmysql.mysql.database.azure.com` |
| `DB_NAME` | Variable | | `traceability_prod` |
| `DB_USER` | Variable | | `trc_app_prod` |
| `DB_PASSWORD` | Secret | | that user's password |

Application settings (connection string, Kafka, CORS) are App Service settings, not pipeline secrets.

### Why an ACR token instead of an Azure login

The pipeline never logs in to Azure. It authenticates only to the container registry with a token scoped to the `traceability` repository, and App Service continuous deployment pulls the image through an ACR webhook. This is the same setup as Quality Lab.

---

## Branch protection

Rulesets on `develop` and `main` require a pull request and these checks: **Dependency audit**, **Build and test**, **CodeQL (C#)**.

---

## Verification evidence (Definition of Done)

| Item | Evidence |
|---|---|
| Pipeline runs green | `<link to run>` |
| Failing test blocks deployment | `<link to run>`: Build and test failed, Deploy to staging skipped |
| Production deploy waits for approval | `<link to run>` |
| Rollback executed | `<link to run>` with timing |
