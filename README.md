# Traceability & QC Dashboard Service

Part of the **Wonrich Dairy Milk Quality Monitoring and Traceability System** (SE3022, Group 16).

Traceability Service keeps a read model of every batch, built from the other services' Kafka events, and answers the QC dashboard and batch trace queries without calling those services.

This repository starts with the DevOps foundation: the Dockerfile, compose file, CI/CD pipeline, health, version and metrics endpoints, logging and tests. Feature code is added on top of it.

---

## Tech stack

| Area | Technology |
|---|---|
| API | ASP.NET Core Web API (.NET 10) |
| Database | Azure Database for MySQL Flexible Server (`wonrichmysql`), EF Core 9 (Pomelo) |
| Messaging | Apache Kafka (shared broker from [`wonrich-infra`](https://github.com/wonrich-dairy/wonrich-infra)) |
| Container | Docker, multi-stage build |
| Hosting | Azure App Service (Linux, container), image in `wonrichtrcacr` |

---

## Project structure

```
traceability-dashboard-service/
├── src/TraceabilityService/        # API (Program.cs, health, metrics, database context)
├── tests/
│   ├── TraceabilityService.UnitTests/
│   └── TraceabilityService.IntegrationTests/   # real API + throwaway MySQL (Testcontainers)
├── perf/traceability.jmx           # JMeter load test
├── scripts/                        # pipeline helpers (health check, audits)
├── .github/workflows/              # CI/CD, rollback, performance, CodeQL
├── docs/                           # pipeline, database, testing, security, performance
├── Dockerfile
├── docker-compose.yml
└── .env.example
```

---

## Run locally

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [`wonrich-infra`](https://github.com/wonrich-dairy/wonrich-infra) cloned next to this repository
- Your public IP added to the `wonrichmysql` firewall (ask the DevOps member)

### 1. Start the shared Kafka broker

```bash
cd ../wonrich-infra
docker compose up -d
```

This also creates the `wonrich-net` Docker network the service joins.

### 2. Configure

```bash
cd ../traceability-dashboard-service
cp .env.example .env        # then fill in TRC_DB_CONNECTION and AUTH_SIGNING_KEY (ask the DevOps member)
```

### 3. Start

```bash
docker compose up --build -d
curl -sS http://localhost:5240/health; echo
```

| | Address |
|---|---|
| API | http://localhost:5240 |
| Health | http://localhost:5240/health |
| Version | http://localhost:5240/version |
| Kafka UI (wonrich-infra) | http://localhost:8085 |

```bash
docker compose ps                         # trc-api is "healthy"
docker compose logs traceability-service
docker compose down
```

To run the whole platform (every service, frontend, Kafka and observability) with one command, use the root stack in `wonrich-infra`.

### Running with `dotnet run`

```bash
cd src/TraceabilityService
dotnet user-secrets set "ConnectionStrings:TraceabilityDb" '<connection-string>'
dotnet user-secrets set "Auth:SigningKey" '<shared signing key>'
dotnet run
```

Kafka is then reached at `localhost:29092`, the default in `appsettings.json`.

---

## Configuration

| Setting | Environment variable | `.env` key (compose) | Description |
|---|---|---|---|
| `ConnectionStrings:TraceabilityDb` | `ConnectionStrings__TraceabilityDb` | `TRC_DB_CONNECTION` | MySQL connection string (required) |
| `Auth:SigningKey` | `Auth__SigningKey` | `AUTH_SIGNING_KEY` | Shared JWT signing key, same value as the Auth Service (required, secret) |
| `Auth:Issuer` | `Auth__Issuer` | | Expected token issuer, default `wonrich-auth` |
| `Auth:Audience` | `Auth__Audience` | | Expected token audience, default `wonrich-services` |
| `Kafka:BootstrapServers` | `Kafka__BootstrapServers` | `KAFKA_BOOTSTRAP_SERVERS` | Broker address |
| `Kafka:SecurityProtocol` | `Kafka__SecurityProtocol` | `KAFKA_SECURITY_PROTOCOL` | `Plaintext` locally, `SaslPlaintext` on Azure |
| `Kafka:SaslMechanism` | `Kafka__SaslMechanism` | `KAFKA_SASL_MECHANISM` | `Plain` on Azure |
| `Kafka:SaslUsername` | `Kafka__SaslUsername` | `KAFKA_SASL_USERNAME` | Broker user |
| `Kafka:SaslPassword` | `Kafka__SaslPassword` | `KAFKA_SASL_PASSWORD` | Broker password (secret) |
| `MccIntake:BaseUrl` | `MccIntake__BaseUrl` | `MCC_INTAKE_BASE_URL` | MCC and Intake Service base URL (SCRUM-12) |
| `MccIntake:ClientId` | `MccIntake__ClientId` | `MCC_INTAKE_CLIENT_ID` | Service-to-service client ID |
| `MccIntake:ClientSecret` | `MccIntake__ClientSecret` | `MCC_INTAKE_CLIENT_SECRET` | Service-to-service secret |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0`, `__1`, ... | `CORS_ORIGIN` | Frontend origins allowed to call the API |

> **Never commit secrets.** Use `.env` (git-ignored) for Docker Compose, user secrets for `dotnet run`, App Service settings for Azure, and GitHub environment secrets for CI/CD.

---

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/health` | Service and dependency health (anonymous) |
| GET | `/version` | Commit SHA of the running build (anonymous) |
| GET | `/metrics` | Prometheus metrics (anonymous) |
| GET | `/api/me` | The caller's user ID, name, role and facility from the JWT (requires a token) |
| GET | `/api/batches/{batchId}` | One batch's full record: summary, upstream status, four checkpoint sections (MCC, Factory Intake, Processing, Quality Lab) each with a state, records and deviations, and links from dispatch to tanks, consignments and societies. 404 problem response if no batch has that ID (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/batches?dispatchRef=…` or `?batchReference=WR-…` | Summaries of the matching batches, oldest first. One dispatch can feed several batches, so this is always a list; no match is an empty list, not a 404. With both filters, a batch must match both; with neither, 400 (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/summary` | Pending, cleared, failed and on-hold counts for today and for the range, and deviations per checkpoint over the range (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/trend` | Per day and product line: batch, cleared and failed counts and `failRate` = failed ÷ (cleared + failed), null when nothing was decided (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/failure-reasons` | Deviation reason codes over the range, most frequent first (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/recent-failures?limit=…` | The latest failed batches, newest first, each with its reason codes and `tracePath` to its batch record. `limit` 1–50, default 10 (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/societies?limit=…` | The most marginal societies behind the batches in range: the closest their milk came to a rejection limit at the MCC gate, and on which measure. Unscored societies last. `limit` 1–50, default 10 (QualityControlOfficer or SystemAdministrator) |

Feature endpoints are added by the developer and documented here.

Each checkpoint section of a batch record has a `state`:

| State | Meaning |
|---|---|
| `Recorded` | The checkpoint has data |
| `NotYetReached` | No data, and the batch has not got that far yet, e.g. the Lab section while the status is still `Processing`. Only Processing and Quality Lab can be in this state |
| `NoDataReceived` | No data although the batch is past this point: a later checkpoint has data, or the status is past it. MCC and Factory Intake with no data are always this: a batch only exists once it has passed factory intake. The `upstream` block says why their data is missing (lookup not run yet, `PendingRetry`, or `Unavailable`) |

The MCC and Factory Intake sections and the links come from the upstream snapshot, whose JSON shape is a contract described in [docs/schema.md](docs/schema.md#upstream_snapshotspayload). `BatchRecordAssembler` builds the response without touching the database, so these rules are covered by unit tests.

The `/api/batches` and `/api/dashboard` endpoints use the `QualityControl` policy (`TraceabilityPolicies.QualityControl`): no token is 401, and a valid token with any role other than `QualityControlOfficer` or `SystemAdministrator` is 403.

Every dashboard endpoint takes the same optional filters, and echoes them back in its response:

| Parameter | Default | Rule |
|---|---|---|
| `facility` | every facility | |
| `productLine` | every product line | One of `FM`, `FLM`, `SY`, `SK`, `DY`, `CD`, in any case; anything else is 400 |
| `from`, `to` | the 30 days ending today | Production days, `yyyy-MM-dd`, both inclusive. `from` after `to` is 400, and so is a range longer than one year |

"Today" is the date in Asia/Colombo. An empty range is a valid answer with zero counts, not an error. Every response carries `dataAsOf`, the latest `processed_messages.ProcessedAt`, or null if nothing has been processed yet. Recent failures are those whose failure (the batch's last event) was recorded on one of the range's days in Colombo.

Per-society quality data is not kept (TR-12 is descoped), so the societies panel reads the cached upstream snapshots of batches whose first event falls in the range, and says so: every response carries `source: "upstream snapshots"` and `coverage: "societies that contributed to a batch in range"` for the screen to show. Batches whose snapshot has not resolved contribute nothing.

`/health` returns:

```json
{
  "status": "Healthy",
  "checks": [
    { "name": "mysql", "status": "Healthy", "description": null },
    { "name": "kafka", "status": "Healthy", "description": "Kafka reachable at kafka:9092; topic 'wonrich.processing.stage-events.v1' has 3 partitions." }
  ]
}
```

| Check | On failure | HTTP |
|---|---|---|
| `mysql` | `Unhealthy` | 503 |
| `kafka` | `Degraded` | 200 |

---

## Database

EF Core migrations are applied on startup in Development and Staging. Production applies the pipeline's idempotent migration script before each deploy.

The tables, their indexes, the coded values and the sample data are described in [docs/schema.md](docs/schema.md).

---

## Documentation

| Topic | File |
|---|---|
| CI/CD pipeline, environments, rollback | [docs/ci-cd-pipeline.md](docs/ci-cd-pipeline.md) |
| Database schema, coded values, sample data | [docs/schema.md](docs/schema.md) |
| Tests and coverage | [docs/testing.md](docs/testing.md) |
| Security scanning | [docs/security-scanning.md](docs/security-scanning.md) |
| Performance test | [docs/performance-testing.md](docs/performance-testing.md) |
