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
- A GitHub token that can read the org's packages, for `Wonrich.QualityLab.Contracts` (see `nuget.config`).
  `dotnet restore` and `docker compose build` both read it from `GITHUB_PACKAGES_TOKEN`:

  ```bash
  gh auth refresh -h github.com -s read:packages   # once
  export GITHUB_PACKAGES_TOKEN=$(gh auth token)
  ```

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
| Swagger UI | http://localhost:5240/swagger |
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
| `Kafka:Topics:StageEvents` | `Kafka__Topics__StageEvents` | | Processing's stage events, default `wonrich.processing.stage-events.v1`. Also the topic the Kafka health check looks for |
| `Kafka:Topics:StageEventsDeadLetter` | `Kafka__Topics__StageEventsDeadLetter` | | Where stage events that cannot be applied are parked, default `wonrich.dlq.traceability-stage-events.v1` |
| `Kafka:ConsumerGroups:StageEvents` | `Kafka__ConsumerGroups__StageEvents` | | Consumer group for the stage events, default `traceability-stage-events` |
| `Kafka:Topics:HoldEvents` | `Kafka__Topics__HoldEvents` | | Processing's hold events, default `wonrich.processing.hold-events.v1` |
| `Kafka:Topics:HoldEventsDeadLetter` | `Kafka__Topics__HoldEventsDeadLetter` | | Where hold events that cannot be applied are parked, default `wonrich.dlq.traceability-hold-events.v1` |
| `Kafka:ConsumerGroups:HoldEvents` | `Kafka__ConsumerGroups__HoldEvents` | | Consumer group for the hold events, default `traceability-hold-events` |
| `Kafka:Topics:BatchDeterminations` | `Kafka__Topics__BatchDeterminations` | | Quality Lab's determinations, default `wonrich.quality-lab.batch-determinations.v1` |
| `Kafka:Topics:BatchDeterminationsDeadLetter` | `Kafka__Topics__BatchDeterminationsDeadLetter` | | Where determinations that cannot be applied are parked, default `wonrich.dlq.traceability-batch-determinations.v1` |
| `Kafka:ConsumerGroups:BatchDeterminations` | `Kafka__ConsumerGroups__BatchDeterminations` | | Consumer group for the determinations, default `traceability-batch-determinations` |
| `Kafka:Topics:ConsignmentTested` | `Kafka__Topics__ConsignmentTested` | | MCC gate test verdicts, default `wonrich.mcc.consignment-tested.v1` |
| `Kafka:Topics:ConsignmentTestedDeadLetter` | `Kafka__Topics__ConsignmentTestedDeadLetter` | | Where unprocessable gate tests are parked, default `wonrich.dlq.traceability-consignment-tested.v1` |
| `Kafka:ConsumerGroups:ConsignmentTested` | `Kafka__ConsumerGroups__ConsignmentTested` | | Consumer group for gate test events, default `traceability-consignment-tested` |
| `Kafka:Consumers:Enabled` | `Kafka__Consumers__Enabled` | | `false` stops all consumers from starting (the integration tests set it). Default `true` |
| `Traceability:FactoryFacility` | `Traceability__FactoryFacility` | | The facility batches created from Processing's events get, since those events carry none. Must match the `facility` claim of the factory's users. Default `FACTORY-01` |
| `Services:MccIntake:BaseUrl` | `Services__MccIntake__BaseUrl` | `MCC_INTAKE_BASE_URL` | MCC and Intake Service base URL (SCRUM-12, SCRUM-132). Empty leaves upstream resolution off |
| `Services:MccIntake:TimeoutSeconds` | `Services__MccIntake__TimeoutSeconds` | `MCC_INTAKE_TIMEOUT_SECONDS` | Per-request timeout to it and to the Auth Service sign-in. Default `10` |
| `Services:MccIntake:ClientId` | `Services__MccIntake__ClientId` | `MCC_INTAKE_CLIENT_ID` | Service account user name at the Auth Service. Its role must grant the MCC and Intake Service's `TraceBatches` and `ScreenFactoryArrivals` policies |
| `Services:MccIntake:ClientSecret` | `Services__MccIntake__ClientSecret` | `MCC_INTAKE_CLIENT_SECRET` | Service account password (secret) |
| `Services:Auth:BaseUrl` | `Services__Auth__BaseUrl` | `AUTH_BASE_URL` | Auth Service base URL, for the service account's sign-in |
| `Upstream:MaxAttempts` | `Upstream__MaxAttempts` | `UPSTREAM_MAX_ATTEMPTS` | Lookups before a snapshot that keeps failing is given up on as `Unavailable`. Default `10` |
| `Upstream:PollIntervalSeconds` | `Upstream__PollIntervalSeconds` | `UPSTREAM_POLL_INTERVAL_SECONDS` | How often the retry worker looks for due `PendingRetry` snapshots. Default `30` |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0`, `__1`, ... | `CORS_ORIGIN` | Frontend origins allowed to call the API |

Each consumer's group is also the group its events are recorded under in `processed_messages`. The topics, dead-letter topics and consumer groups are registered in `wonrich-infra` (`kafka/topics.env`, `docs/kafka.md`; SCRUM-126). The brokers do not create topics on first use, so change them there first. Never reuse another service's group, e.g. Processing's `processing-stage-events`: consumers in one group share the messages, so each service would see only some of them.

### Kafka consumers

Every consumer runs on the same base (`Infrastructure/Kafka`): `KafkaEventConsumer` reads the topic as the consumer's group, and `EventProcessor` sends each event, by its `eventType` header, to the handler registered for that type. The handler runs in one database transaction together with the event's `processed_messages` row, so an event read twice is recognised and skipped. The offset is committed only after the transaction commits or the message is dead-lettered, so nothing is skipped. Every log line written while an event is handled carries its `x-correlation-id` as `CorrelationId`, with its `EventId` and `EventType`.

| Consumer | Topic | Group | Dead-letter topic | Handlers (`eventType` header) |
|---|---|---|---|---|
| Stage events (SCRUM-130) | `wonrich.processing.stage-events.v1` | `traceability-stage-events` | `wonrich.dlq.traceability-stage-events.v1` | `MilkAllocatedToMixingTankEvent`, `ProcessingStageRecordedEvent`, `ProcessingCompletedEvent`: `ProcessingEventHandler` |
| Hold events (SCRUM-130) | `wonrich.processing.hold-events.v1` | `traceability-hold-events` | `wonrich.dlq.traceability-hold-events.v1` | `ProcessingHoldRaisedEvent`, `ProcessingHoldResolvedEvent`: `ProcessingEventHandler` |
| Lab determinations (SCRUM-131) | `wonrich.quality-lab.batch-determinations.v1` | `traceability-batch-determinations` | `wonrich.dlq.traceability-batch-determinations.v1` | `BatchCleared`, `BatchFailed`: `LabDeterminationHandler` |

The Processing consumers create a batch on its first event. A hold raised before allocation carries the dispatch number instead of a batch code; it is kept in `pending_dispatch_events` until a batch from that dispatch arrives. To add a consumer, register it with `services.AddEventConsumer(options, handlers => handlers.Add<THandler>("EventType"))`; see `ProcessingConsumers` and `LabDeterminationConsumer`.

| Failure | What happens |
|---|---|
| Transient, e.g. the database, or a lab determination for a batch whose Processing events have not arrived yet | Retried 3 times, after 1 s, 2 s and 4 s, then dead-lettered |
| An event type with no handler on that consumer | Retried the same way, then dead-lettered, so it can be replayed once a handler exists |
| Can never succeed: no value or event ID, the body is not valid JSON or not the contract's shape, or the batch code has no day of the year | Dead-lettered at once |
| The dead-letter publish fails too | Not committed: the message is read again after 5 s, so it is never lost |

A dead letter is the original message unchanged (key, value and headers) plus `x-original-topic`, `x-original-partition`, `x-original-offset`, `x-failure-reason`, `x-failure-timestamp` and `x-attempt-count`. Once the cause is fixed, replay it by publishing it back onto the source topic as it is.

### Upstream snapshots (SCRUM-132)

The first time the Processing consumers see a batch, they create its `upstream_snapshots` row as `PendingRetry` in the event's transaction, and once that commits they queue it for `UpstreamResolutionWorker`, which tries it straight away. One attempt (`UpstreamResolver`) looks the batch up in the MCC and Intake Service: factory intake's batch by the dispatch number (`GET /api/factory/batches?dispatchNote=`), which gives the batch reference, then that batch's trace (`GET /api/factory/batches/{reference}/trace`). The answer is mapped to the payload in [docs/schema.md](docs/schema.md#upstream_snapshotspayload) by `UpstreamSnapshotMapper`, and `UpstreamResolutionRules` decides the status:

| Lookup | Status |
|---|---|
| The whole tree | `Resolved`: `Payload`, `ResolvedAt` and `batch_records.BatchReference` are filled, and the trace becomes `Complete` if Processing and the lab have reported |
| No batch from that dispatch note, or no batch with that reference (a 404 with code `entity_not_found`) | `Unavailable` at once |
| Gaps in the tree, e.g. a tank with no pours recorded, or a consignment whose society is unknown | `PendingRetry`: a partial tree is never stored, and the gap may be filled upstream |
| Unreachable, timed out, any other status (a 401 is retried once with a new token), or an unreadable body | `PendingRetry` |

A `PendingRetry` row is due again after 30 s, then 1, 2, 4 minutes and so on, capped at 30 minutes. At `Upstream:MaxAttempts` it becomes `Unavailable`, with `FailureReason` saying the retries ran out and why the last one failed. Every `Upstream:PollIntervalSeconds` the worker picks up due rows, 20 at a time, oldest first, so a lost queue entry or a restart only delays an attempt. Each attempt first claims its row (counting the attempt, pushing `NextAttemptAt` past the time the calls can take, and setting `LastCorrelationId`, the row's concurrency token), so two instances never call upstream for the same batch. A failed attempt never fails the Kafka consumer: the attempt is queued only after the event's transaction commits, and runs on the worker, not the consume loop. MCC is deliberately not in `/health`: it is an optional upstream, and the service works without it. The calls carry `X-Correlation-ID`: the triggering event's ID for the first attempt, a new one per retry, stored as `LastCorrelationId`.

The MCC and Intake Service accepts the shared Auth Service tokens, so service-to-service means signing in as a service account (`Services:MccIntake:ClientId` and `ClientSecret`) at `Services:Auth:BaseUrl`. `ServiceTokenProvider` caches the token until a minute before it expires. The response contracts in `Infrastructure/MccIntake/MccIntakeContracts.cs` are copied from the MCC and Intake Service repository; the commit is in the file. The worker stays idle, and logs why, until `Services:MccIntake:BaseUrl`, `ClientId`, `ClientSecret` and `Services:Auth:BaseUrl` are all set. Attempts are counted in `traceability_upstream_resolutions_total{status}`.

> **Never commit secrets.** Use `.env` (git-ignored) for Docker Compose, user secrets for `dotnet run`, App Service settings for Azure, and GitHub environment secrets for CI/CD.

---

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/health` | Service and dependency health (anonymous) |
| GET | `/version` | Commit SHA of the running build (anonymous) |
| GET | `/metrics` | Prometheus metrics (anonymous) |
| GET | `/swagger` | Swagger UI, Development and Staging only (anonymous). See [API documentation](#api-documentation-swagger) |
| GET | `/swagger/v1/swagger.json` | The OpenAPI document the UI reads, Development and Staging only (anonymous) |
| GET | `/api/me` | The caller's user ID, name, role and facility from the JWT (requires a token) |
| GET | `/api/batches/{batchId}` | One batch's full record (audited as `BatchRecordOpened`): summary, upstream status, four checkpoint sections (MCC, Factory Intake, Processing, Quality Lab) each with a state, records and deviations, and links from dispatch to tanks, consignments and societies. 404 problem response if no batch has that ID (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/batches/{batchId}/trace` | The batch's backward trace (SCRUM-134, audited as `BatchTraceOpened`): its four checkpoints in stage order, each `WithinSpec`, `Deviation`, `MissingData` or `NotYetReached`, the checkpoint the first deviation came from (`originCheckpoint`, `origin`), its Processing holds, and its tanks and societies in the upstream snapshot's order. 404 problem response if no batch has that ID (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/batches?dispatchRef=…` or `?batchReference=WR-…` | Summaries of the matching batches, oldest first. One dispatch can feed several batches, so this is always a list; no match is an empty list, not a 404. With both filters, a batch must match both; with neither, 400 (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/batches/search?…&page=…` | Batch search (SCRUM-139): the batches matching every filter given, 50 to a page, newest first, with `totalCount` and `totalPages`. Filters: `status`, `productLine`, `from`/`to` (factory days of the batch's first event), `society` (code or name), `tank`, `user`, `reasonCode`, `facility`. Each batch appears once, however many tanks, societies, users or reasons it has. Each row adds its tanks, societies, users, reason codes and five lab parameters; one its product line does not measure is `NotApplicable`. No match is a 200 with an empty page and a `message`. See [Batch search and export](#batch-search-and-export) (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/batches/export?…` | The same filters, every matching batch as CSV, not only one page. No empty cells, a file name carrying the filters and the export date, and an `audit_entries` row per export. `X-Batch-Count` gives the row count; no match is the heading row alone (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/audit?…&page=…` | Consolidated audit trail (SCRUM-138): who recorded or did what, and when, across every facility, 50 to a page, newest first. Each entry has time, user, action, checkpoint, batch, facility, source and reference. Filters: `user`, `action`, `from`/`to` (factory days), `batchId`. See [Audit trail](#audit-trail) (SystemAdministrator or QualityControlOfficer) |
| GET | `/api/audit/coverage` | Which actions the audit trail covers and which it does not, with the reason (SystemAdministrator or QualityControlOfficer) |
| GET | `/api/dashboard/summary` | Pending, cleared, failed and on-hold counts for today and for the range, and deviations per checkpoint over the range (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/deviations` | Deviations per checkpoint over the range, always four in stage order: the summary's figures on their own, as `checkpoints` (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/trend` | Per day and product line: batch, cleared and failed counts and `failRate` = failed ÷ batches, pending and on-hold batches included, so a day still being decided reads low (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/failure-reasons` | Deviation reason codes over the range, most frequent first (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/recent-failures?limit=…` | The latest failed batches, newest first, each with its reason codes and `tracePath` to its batch record. `limit` 1–50, default 10 (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/societies?limit=…` | The most marginal societies behind the batches in range: the closest their milk came to a rejection limit at the MCC gate, and on which measure. Unscored societies last. `limit` 1–50, default 10 (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/marginal-societies?limit=…` | The same as `/api/dashboard/societies`, under the path the frontend's dashboard asks for (QualityControlOfficer or SystemAdministrator) |
| GET | `/api/dashboard/society-performance` | Every society tested in the date range, with acceptance rate, accepted/rejected counts, average fat/SNF and marginal count from MCC gate-test events (QualityControlOfficer or SystemAdministrator) |

Feature endpoints are added by the developer and documented here.

### API documentation (Swagger)

The service serves its own Swagger UI (SCRUM-125), set up the same way as the other services (SCRUM-49): Swashbuckle, in `Api/Infrastructure/Swagger`.

| | Development | Staging | Production |
|---|---|---|---|
| UI | `/swagger` | `/swagger` | Off (401, like any unknown path) |
| OpenAPI document | `/swagger/v1/swagger.json` | `/swagger/v1/swagger.json` | Off |

The page and the document load without a token. To call an endpoint from the page, sign in at the Auth Service (`POST /api/auth/login`), choose **Authorize** and paste the access token without `Bearer `. The root descriptor (`/`) shows `swagger` as the path, or `null` in Production.

Every controller action is in the document, with no Swagger configuration needed. For each endpoint it shows:

| What | Where it comes from |
|---|---|
| Path, HTTP method, parameters | The route and the action's parameters. A `[FromQuery]` object such as `DashboardQuery` is shown as its separate query parameters |
| Request and response schemas | The action's return type and its `[ProducesResponseType(typeof(...), status, contentType)]` attributes. Declare one for each status the action can return |
| Summary, description, parameter and status texts | The action's XML comments: `<summary>`, `<remarks>`, `<param>`, `<response code="...">`. Property descriptions come from `///` comments on the DTOs in `Api/Contracts`; a plain `//` comment is not shown |
| Authentication | `AuthorizationOperationFilter`: every operation needs the Bearer token and lists 401, as the fallback policy requires, unless it is `[AllowAnonymous]`. When `[Authorize]` names roles, directly or through a policy such as `QualityControl`, the roles are added to the description and 403 is listed |

The XML file is written by `GenerateDocumentationFile` in the project file and published with the app. `/health`, `/metrics`, `/version` and `/` are operational endpoints, not part of the API, and are left out. `SwaggerEndpointTests` checks that every controller endpoint is in the document with its schemas, authentication and XML comments, and that Production serves none of it.

Each checkpoint section of a batch record has a `state`:

| State | Meaning |
|---|---|
| `Recorded` | The checkpoint has data |
| `NotYetReached` | No data, and the batch has not got that far yet, e.g. the Lab section while the status is still `Processing`. Only Processing and Quality Lab can be in this state |
| `NoDataReceived` | No data although the batch is past this point: a later checkpoint has data, or the status is past it. MCC and Factory Intake with no data are always this: a batch only exists once it has passed factory intake. The `upstream` block says why their data is missing (lookup not run yet, `PendingRetry`, or `Unavailable`) |

The MCC and Factory Intake sections and the links come from the upstream snapshot, whose JSON shape is a contract described in [docs/schema.md](docs/schema.md#upstream_snapshotspayload). `BatchRecordAssembler` builds the response without touching the database, so these rules are covered by unit tests.

The backward trace answers "where did this batch go wrong?":

| Field | Rule |
|---|---|
| `chain` | Always four entries, MCC, Factory Intake, Processing, Quality Lab, never omitted. `Deviation` if the checkpoint reported any; otherwise the batch record's state rule above: `WithinSpec` for `Recorded`, `MissingData` (with `missingReason`) for `NoDataReceived`, and `NotYetReached` |
| `originCheckpoint` | The **first checkpoint in stage order** that deviated, not the deviation with the earliest time: milk that left the MCC warm is the origin even when Processing and the lab deviated too. `null`, with `hasDeviation: false`, for a clean batch |
| `origin` | The first deviation at that checkpoint: parameter, observed value, limit, time, reason code and the upstream event's ID |
| Ordering | Deviations by (`occurredAt`, `sourceEventId`), so two at the same time always come back in the same order. Tanks, consignments and `societies` exactly as the snapshot lists them, never sorted |
| `status`, `labResult` | As stored by the consumers (`BatchStatusRules`, SCRUM-130) and `LabResults`; the trace computes no status of its own. A hold is open by `BatchStatusRules.OpenHolds` |

The same rows always give the same response. `BackwardTraceAssembler` builds it without the database or the clock, so these rules are unit-tested; `BatchTraceService` loads it in five queries (batch, timeline events, deviations, active determinations, snapshot) whatever the batch holds, and one for an unknown batch. `BatchTraceEndpointTests.A_trace_takes_five_queries_and_an_unknown_batch_one` asserts that and writes the SQL to the test output.

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

### Batch search and export

`GET /api/batches/search` and `GET /api/batches/export` (SCRUM-139) take the same filters, all optional; together they must all match. Status and product line are matched without regard to case, and an unknown one is a 400 naming the parameter. Society, tank and user are matched without regard to case too. Society and tank, and the MCC and intake users, come from the resolved upstream snapshot (`JSON_TABLE` over its payload, MySQL 8). Processing and Quality Lab users come from `timeline_events.RecordedBy`. Every filter is an `EXISTS` or `IN` subquery on `batch_records`, never a join, so one batch is one row and the total counts batches.

The export has one row per batch and these columns: batch ID, facility, product line, status, lab result, trace completeness, dispatch ref, batch reference, first and last event (UTC), upstream snapshot status, tanks, societies, users, deviation count, reason codes, then the five lab parameters. A blank cell in an audit export cannot be told apart from a test somebody forgot to run, so no cell is left empty:

| Marker | Means |
|---|---|
| `N/A` | The product line does not measure it: SNF and corrected CLR on SY, SK, DY and CD; texture on FM and FLM (Quality Lab's panel shape) |
| `Pending` | Quality Lab has made no determination yet |
| `No deviation` | The current determination records nothing against the parameter |
| `Out of spec: 3.20 against limit 3.50` | The current determination's reading and the limit it broke |
| `Not received` | No event has carried the value yet (dispatch ref, or no snapshot row) |
| `Not resolved` | The upstream snapshot has not resolved, so tanks, societies and batch reference are not known yet |
| `None` | Known, and there are none (reason codes, users) |

The file is UTF-8 with a byte order mark, so Excel reads Sinhala and Tamil society names. Lines end in CRLF. A cell starting with `=`, `+`, `-` or `@` is prefixed with `'`, so a spreadsheet does not run it as a formula. The file is named `batches_[filters]_exported-[date].csv`, for example `batches_status-Failed_line-FM_from-2026-09-01_to-2026-09-30_exported-2026-10-13.csv`, or `batches_all_exported-…` with no filter. The date is today in Asia/Colombo.

Each export writes an `audit_entries` row before the file is returned: `Action` `BatchSearchExported`, `EntityType` `BatchSearch`, `EntityId` the file name, `Actor` the token's user ID, the correlation ID, and `Details` with the filters, the row count and the caller's role. A refused request (401 or 403) is not audited, since nothing was exported. CORS exposes `Content-Disposition` and `X-Batch-Count`, so the frontend can read the file name and count.

### Audit trail

`GET /api/audit` (SCRUM-138) is read at query time from three places: the events Traceability consumed (`timeline_events`), the records in each resolved upstream snapshot (consignment registrations, the dispatch, the intake screening), and `audit_entries`, Traceability's own record of batches opened and exports. So it covers the actions that have events, and no others: an edit made in another service without an event is not in it. [docs/audit-trail.md](docs/audit-trail.md) lists what is and is not covered, as does `GET /api/audit/coverage`.

Opening a batch record or trace writes an `audit_entries` row before the response, as an export does; a 404 or a refused request writes none. `audit_entries` is append-only: there is no endpoint to change an entry, the DbContext refuses to save one changed or removed, and the table's triggers refuse any `UPDATE` or `DELETE`. Deploying the triggers needs `log_bin_trust_function_creators = ON` on the Azure server, see [docs/database.md](docs/database.md).

## Database

EF Core migrations are applied on startup in Development and Staging. Production applies the pipeline's idempotent migration script before each deploy.

The tables, their indexes, the coded values and the sample data are described in [docs/schema.md](docs/schema.md).

---

## Documentation

| Topic | File |
|---|---|
| CI/CD pipeline, environments, rollback | [docs/ci-cd-pipeline.md](docs/ci-cd-pipeline.md) |
| Database schema, coded values, sample data | [docs/schema.md](docs/schema.md) |
| Audit trail: what it covers and what it does not | [docs/audit-trail.md](docs/audit-trail.md) |
| Tests and coverage | [docs/testing.md](docs/testing.md) |
| Security scanning | [docs/security-scanning.md](docs/security-scanning.md) |
| Performance test | [docs/performance-testing.md](docs/performance-testing.md) |
