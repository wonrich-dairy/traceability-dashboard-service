# Security scanning

Automated dependency, static-analysis and container-image scanning for Traceability Service (SCRUM-112, reused for SCRUM-123). A known-vulnerable package or an obvious code weakness should be caught on the pull request, not found in the final evaluation.

---

## What runs, and when

| Check (PR status name) | Scans | Tool | Workflow | Runs on |
|---|---|---|---|---|
| **Dependency audit** | NuGet packages, direct and transitive, in all three projects (API, unit tests, integration tests) | `dotnet list package --vulnerable --include-transitive` | `ci-cd.yml` | Every PR and push |
| **CodeQL (C#)** | Application source code (SAST) | GitHub CodeQL, `security-extended` queries | `codeql.yml` | Every PR and push, plus weekly |
| **Build and test** → image scan steps | The built Docker image: OS packages in the base image and the published app | Trivy | `ci-cd.yml` | Every PR and push |

JavaScript scanning and `npm audit` belong to the frontend repository; this repository has no JavaScript.

---

## Severity policy

| Severity | Effect |
|---|---|
| **Critical, High** | The check fails. The PR cannot merge and staging does not deploy |
| **Medium / Moderate, Low** | Reported in the run summary and the Security tab; does not block |

Specific rules per tool:

- **NuGet:** NuGet's scale is Low / Moderate / High / Critical. High and Critical fail.
- **CodeQL:** each finding has a numeric security severity; 7.0–8.9 is High and 9.0+ is Critical, matching GitHub's labels. 7.0 and above fails.
- **Trivy:** High and Critical fail **only when a fixed version exists**. A finding with no fix yet has nothing to upgrade to, and blocking on it would stop every build until the upstream publishes one. Unfixed findings still appear in the Security tab so they are visible.

Staging deployment needs both **Dependency audit** and **Build and test** to pass. CodeQL is a separate workflow, so it blocks through branch protection (below) rather than by gating the deploy job directly.

---

## Where to read results

| Where | What |
|---|---|
| The failed step's log and the run summary | A table of findings, with severity, package or file, and advisory link |
| **Security → Code scanning** | CodeQL and Trivy findings, with history, filterable by severity and tool |

---

## Configuration

All configuration lives in this repository, so a change to what is scanned or accepted goes through a pull request like any other code.

| File | Purpose |
|---|---|
| `.github/workflows/ci-cd.yml` | Dependency audit job, image scan steps |
| `.github/workflows/codeql.yml` | CodeQL workflow |
| `.github/codeql/codeql-config.yml` | Query suite, excluded paths, triaged CodeQL rules |
| `scripts/nuget-audit.sh` | Runs the NuGet audit and applies the severity policy |
| `scripts/codeql-gate.py` | Applies the severity policy to CodeQL results |
| `.nuget-audit-ignore` | Triaged NuGet advisories |
| `.trivyignore` | Triaged image CVEs |

**Excluded from CodeQL:** `tests/`. Test projects never ship, and they deliberately contain throwaway credentials for the Testcontainers database, which CodeQL would report as hard-coded secrets on every run. Test projects are still included in the dependency audit.

---

## When a scan fails

1. **Fix it** if you can: upgrade the package, update the base image, or change the code. This is the expected path.
2. **Triage it** if it genuinely cannot be fixed yet. Add the advisory or CVE to the matching file with a reason, your name and a date to look again, and open a PR. The reviewer decides whether the reason holds.

| Tool | Triage file | Entry |
|---|---|---|
| NuGet | `.nuget-audit-ignore` | The advisory URL from the audit output |
| Trivy | `.trivyignore` | The CVE ID |
| CodeQL | `.github/codeql/codeql-config.yml` | A `query-filters` exclusion for the rule ID |

Dismissing an alert in the Security tab records the decision there but **does not unblock the build**; only the files above do.

Never lower a threshold to get a build through.

### Run the dependency audit locally

```bash
dotnet restore src/TraceabilityService/TraceabilityService.csproj
bash scripts/nuget-audit.sh src/TraceabilityService/TraceabilityService.csproj
```

Needs `jq` (`brew install jq` on macOS).

---

## Prerequisites

- **Code scanning must be available on the repository.** It is free on public repositories. On a private repository it needs GitHub Advanced Security; without it, the CodeQL analysis and the Trivy upload steps fail with "Code scanning is not enabled".
- **Branch protection on `develop`** requires these checks: **Build and test**, **Dependency audit**, **CodeQL (C#)**.

---

## Proof that it blocks

Definition of Done: a deliberately vulnerable package is confirmed to block a PR.

| Test | Expected result | Evidence |
|---|---|---|
| `Newtonsoft.Json 12.0.3` (GHSA-5crp-9r3c-p9vr, High) added on a throwaway branch | **Dependency audit** fails, PR cannot merge | _screenshot / PR link_ |
| Scans on `develop` | Green, or findings triaged above | _run link_ |
