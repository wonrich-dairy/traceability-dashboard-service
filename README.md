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

Feature endpoints are added by the developer and documented here.

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
