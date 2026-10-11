# Azure setup

The Azure resources Traceability Service runs on, and how they were created. The pipeline in
[ci-cd-pipeline.md](ci-cd-pipeline.md) expects exactly these.

## Resources

| | Staging | Production |
|---|---|---|
| App Service | `wonrich-traceability` | `wonrich-traceability-prod` |
| Plan | Free F1, Linux, Southeast Asia | its own Free F1 plan `plan-traceability-prod` |
| Resource group | `rg-traceability` | `rg-traceability` |
| URL | `https://wonrich-traceability-hhfsh9d4cff6gsd3.southeastasia-01.azurewebsites.net` | `https://wonrich-traceability-prod-hcagaah2exc9frbj.southeastasia-01.azurewebsites.net` |
| Image the app follows | `wonrichtrcacr.azurecr.io/traceability:latest` | `wonrichtrcacr.azurecr.io/traceability:production` |
| Registry webhook | `stagingtraceability`, on pushes of `traceability:latest` | `productiontraceability`, on pushes of `traceability:production` |
| Database | `traceability`, user `trc_app` | `traceability_prod`, user `trc_app_prod` |
| Kafka broker | `wonrich-kafka.southeastasia.cloudapp.azure.com:9094` | same host, port `9095` |

Both apps use the free tier. Nothing in this service needs a paid plan, Always On, or Application Insights.

The registry (`wonrichtrcacr`, resource group `rg-wonrich`) and the MySQL server (`wonrichmysql`) are in a
different subscription from the apps, so the apps pull the image with a registry token instead of a
managed identity.

## Creating an app in the portal

1. **Create a resource, Web App.** Publish **Container**, Operating System **Linux**, Region **Southeast Asia**,
   a new App Service plan on **Free F1**.
2. **Container tab.** Sidecar support off. Image source **Other container registries**, access **Private**,
   registry `https://wonrichtrcacr.azurecr.io`, user `gh-traceability`, the token password, image
   `traceability:latest` (staging) or `traceability:production` (production). No startup command.
3. **Networking:** public access on. **Monitor:** Application Insights off, Defender off.
4. **Settings, Environment variables.** All of these are App Service settings, never in source:

   | Setting | Value |
   |---|---|
   | `ASPNETCORE_ENVIRONMENT` | `Staging` or `Production` |
   | `WEBSITES_PORT` | `8080` |
   | `ConnectionStrings__TraceabilityDb` | `Server=wonrichmysql.mysql.database.azure.com;Port=3306;Database=<db>;User=<user>;Password=<password>;SslMode=Required;` |
   | `Auth__SigningKey` | **Secret.** Must be the same value as the Auth Service's `Auth__SigningKey`, or every token is rejected. At least 32 bytes; the app does not start without it |
   | `Kafka__BootstrapServers` | host and port from the table above |
   | `Kafka__SecurityProtocol` | `SaslPlaintext` |
   | `Kafka__SaslMechanism` | `Plain` |
   | `Kafka__SaslUsername` | `wonrich` |
   | `Kafka__SaslPassword` | that broker's password |
   | `Cors__AllowedOrigins__0` | the frontend URL, when there is one |

5. **Configuration, General settings:** tick **SCM Basic Auth Publishing Credentials** (the webhook needs it).
6. **Deployment, Deployment Center:** Continuous deployment on. Copy the **Webhook URL**.
7. **Registry webhook** (needs the Azure CLI, in the registry's subscription):

   ```bash
   az acr webhook create --registry wonrichtrcacr --name stagingtraceability \
     --uri '<webhook url>' --actions push --scope traceability:latest
   ```

   For production use `--name productiontraceability` and `--scope traceability:production`.

## Registry token

The pipeline logs in to the registry with a token limited to the `traceability` repository (read and write
on that repository only), created with:

```bash
az acr scope-map create --name traceability-push --registry wonrichtrcacr \
  --repository traceability content/read content/write
az acr token create --name gh-traceability --registry wonrichtrcacr --scope-map traceability-push
```

## GitHub

Environments `staging` and `production` hold the secrets and variables listed in
[ci-cd-pipeline.md](ci-cd-pipeline.md#secrets-and-variables). `production` only deploys from `main` and has
required reviewers.

## Backups

Both databases are on the `wonrichmysql` server, which takes automatic backups of the whole server. Retention
and point-in-time restore are on the server's **Backup and restore** page. Do not commit dump files.
