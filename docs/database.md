# Traceability Service: Database

Where the Traceability database is hosted, how to connect, how access is controlled, how migrations work, and how backups are handled (SCRUM-122).

---

## 1. Overview

| Item | Staging | Production |
|---|---|---|
| Engine | MySQL 8.0 | MySQL 8.0 |
| Hosting | Azure Database for MySQL, Flexible Server | same server |
| Server | `wonrichmysql.mysql.database.azure.com` | same server |
| Region | Southeast Asia | Southeast Asia |
| Database | `traceability` | `traceability_prod` |
| Character set | `utf8mb4` | `utf8mb4` |
| Application user | `trc_app` | `trc_app_prod` |
| ORM | Entity Framework Core 9 with the Pomelo MySQL provider | same |

The server is shared by the Wonrich services. Each service has **its own database and its own user**, and production uses separate databases from staging, so a staging test can never touch production data.

---

## 2. Access control

Each user can only reach its own database:

```sql
CREATE USER 'trc_app'@'%' IDENTIFIED BY '<password>' REQUIRE SSL;
GRANT ALL PRIVILEGES ON traceability.* TO 'trc_app'@'%';

CREATE USER 'trc_app_prod'@'%' IDENTIFIED BY '<password>' REQUIRE SSL;
GRANT ALL PRIVILEGES ON traceability_prod.* TO 'trc_app_prod'@'%';
```

`wonrich-infra/azure/mysql/create-databases.sh` creates these (and the production databases of the other services), generating each password with `openssl`.

Verification:

```sql
SHOW GRANTS FOR 'trc_app'@'%';
-- GRANT USAGE ON *.* TO `trc_app`@`%`
-- GRANT ALL PRIVILEGES ON `traceability`.* TO `trc_app`@`%`
```

As `trc_app`, `USE quality_lab`, `USE mccdb` and `USE processingdb` all fail with `Access denied`. The same holds for `trc_app_prod`.

The server administrator login is **not** used by the application. The server accepts connections only over SSL (`SslMode=Required`).

### Network access

Access is controlled by the server's firewall rules (**Networking** in the portal). If you get a connection timeout, check that your IP is allowed there first.

---

## 3. Connection settings

```
Server=wonrichmysql.mysql.database.azure.com;Port=3306;Database=traceability;User=trc_app;Password=<password>;SslMode=Required;
```

The application reads it from `ConnectionStrings:TraceabilityDb`:

| Environment | Where the value lives | Key |
|---|---|---|
| Docker Compose (local) | `.env` (git-ignored) | `TRC_DB_CONNECTION` |
| `dotnet run` (local) | .NET user secrets | `ConnectionStrings:TraceabilityDb` |
| Azure App Service | Environment variables | `ConnectionStrings__TraceabilityDb` |
| GitHub Actions, production migrations | `production` environment | `DB_HOST`, `DB_NAME`, `DB_USER` (variables), `DB_PASSWORD` (secret) |

```bash
cp .env.example .env        # fill in TRC_DB_CONNECTION
cd src/TraceabilityService
dotnet user-secrets set "ConnectionStrings:TraceabilityDb" '<connection-string>'
```

> **Never commit connection strings or passwords.** Share them privately.

If the connection string is missing, the service fails at startup with a message naming the missing setting.

---

## 4. Schema migrations

Migrations are EF Core migrations in `src/TraceabilityService/Migrations`. Applied migrations are recorded in `__EFMigrationsHistory`.

| Environment | How migrations are applied |
|---|---|
| Development, Staging | By the service on startup (`Database.Migrate()`) |
| Production | By the pipeline, before the deploy: it runs the idempotent `migration-script` artifact (`dotnet ef migrations script --idempotent`) against `traceability_prod` |

The script is safe to run against a database at any migration state, so a re-run changes nothing.

### Creating a migration

```bash
dotnet tool restore
dotnet ef migrations add <MigrationName> --project src/TraceabilityService
```

### Rules

- **Keep migrations backward compatible:** add columns and tables; do not rename or drop in the same release. Rolling back the application does not roll back the database, so the previous version must still run on the new schema.
- Never edit or delete a migration that has already been applied; add a new one.
- Create migrations on a feature branch and get them reviewed before they reach `develop`.

### Verifying

```sql
SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory;
```

The service's `/health` endpoint reports the `mysql` check, which is Healthy only when the service can reach its database.

---

## 5. Backups and restore

Azure Database for MySQL takes **automatic backups** of the whole server, so both Traceability databases are covered together with every other service's.

| Item | Value |
|---|---|
| Backup type | Automatic, managed by Azure |
| Retention | See the server's **Backup and restore** page (default 7 days) |
| Restore method | Point-in-time restore to a **new** server |
| Scope | The entire server |

To restore: **Backup and restore → Restore**, choose the time, name the new server. The original is not overwritten; point the connection string at the new server, or copy the data back.

Manual backup of one database before a risky change:

```bash
mysqldump -h wonrichmysql.mysql.database.azure.com -u trc_app -p \
  --ssl-mode=REQUIRED --single-transaction traceability > traceability_$(date +%F).sql
```

Do not commit dump files.

---

## 6. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Connection timeout | Firewall does not allow your IP | Add it under the server's **Networking** |
| `Access denied for user 'trc_app'` | Wrong password or user | Check the connection string |
| `/health` returns `Unhealthy` | Database unreachable | Check the firewall, connection string and server state |
| Service fails at startup | Missing connection string or failed migration | App Service **Log stream**, or `docker compose logs traceability-service` |
| Server not responding | Server stopped | Start it on the server's **Overview** page |

---

## 7. Ownership

| Item | Owner |
|---|---|
| MySQL server `wonrichmysql` | DevOps |
| `traceability` and `traceability_prod`, and their users | DevOps |
| Entities and migrations | Traceability developer |
