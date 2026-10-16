# Smart Factory IoT Backend

Production AAS runs on the Oracle VM defined by `deploy/terraform/oracle-basyx`, using the `deploy/oracle-basyx` Compose stack and the dedicated Aiven `basyx` database. The dashboard repository's deployment guide remains canonical for the cross-repository release.

This repository contains five .NET services. Production packages them with the Node API in one ARM64 image deployed as separate API and singleton telemetry/notification worker containers on the existing Oracle BaSyx VM. The React UI is a separate prebuilt Vercel artifact, not a Docker image. The dashboard repository owns the combined Dockerfile and the Oracle Compose deployment under `deploy/oracle`. Its legacy Render release workflow is disabled after migration; standard quality CI remains enabled. Kubernetes is unnecessary.

| Service | Responsibility | Production route |
|---|---|---|
| DeviceService | AAS provisioning/import, asset revisions and commissioned gateway commands | Private 127.0.0.1:3102 |
| TelemetryService | Durable MQTT ingestion, dashboard retry outbox and health | Worker health on VM 127.0.0.1:10001 |
| IdentityService | Existing dashboard account profile and current roles | Private 127.0.0.1:3104 |
| AnalyticsService | SQL sample coverage, gaps and null-safe observed metric summaries | Private 127.0.0.1:3105 |
| NotificationService | Durable incident and account-email delivery through Gmail SMTP or future Resend | Private 127.0.0.1:3106 |

Dashboard accounts remain the identity authority. No separate Entra SSO account store is required. SMTP/Resend credentials are used only for notification delivery; Entra client credentials are reserved for company AAS OAuth integrations. Dashboard roles are reloaded at each private identity/analytics request; anonymous service calls are rejected. Notifications use the shared PostgreSQL inbox/queue and never accept arbitrary email recipients from public requests.

Use the current [Oracle deployment guide](https://github.com/DruHustle/smart-factory-iot-frontend/blob/main/deploy/oracle/README.md) for production. The historical [local and Vercel/Render guide](https://github.com/DruHustle/smart-factory-iot-frontend/blob/main/RENDER_DEPLOYMENT.md) covers local development, shared tokens, database migrations, image context, provider TLS, single-instance MQTT rollout safety, CI/CD and rollback. Oracle production settings live in owner-only `.env.api` and `.env.worker` files under `/opt/smart-factory-backend`; the migration preserved the live Render values. The `split-render-env.mjs` helper and Render examples remain for historical rollback use. Start dashboard Compose/BaSyx first, then this repository's Compose services. Replace every `.env.local.example` placeholder and configure the dashboard DB connection and shared internal service token. Local host ports are Device 5001, Identity 5002, Notifications 5003, Analytics 5004, Telemetry health 5005. All bind to loopback. Company AAS services and managed production databases/broker/Redis remain external to the Oracle VM.

## Tests

```bash
dotnet restore src/SmartFactory.sln -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high -warnaserror:NU1900,NU1903,NU1904 --force-evaluate
dotnet test src/SmartFactory.sln -c Release --no-restore
```

Use a .NET 8 SDK/runtime container if the host has only newer runtimes. The dashboard's `pnpm e2e` covers role and browser workflows; `python3 scripts/bundle-smoke.py` there tests the exact six-service image and real disposable dependencies; `pnpm e2e:system` covers real AAS/MQTT/Pi code with simulated serial equipment. Before each production update, scan the exact combined image locally with telemetry disabled and reject high/critical findings. Runtime Node build tools are excluded. See [integration test evidence](docs/E2E_Integration_Test_Strategy.md).

For live AAS acceptance, use the local BaSyx profile and `AAS_LIVE_TESTS=true dotnet test src/SmartFactory.sln --filter FullyQualifiedName~AasxLiveIntegrationTests`. Set `AASX_TEST_CORPUS_DIR` to licensed vendor fixtures for optional parser checks; do not commit vendor assets without distribution permission. Local browser AAS responses are deterministic stubs; they do not certify the company's IDTA conformance.

## Runtime limits

.NET 8 support ends on 2026-11-11. Migrate SDK/runtime/package dependencies and revalidate before that date; see [Microsoft lifecycle dates](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core). Telemetry has one stable persistent MQTT identity, so Oracle worker deployments must stop the previous container before starting its replacement. Factory downtime is explicitly confirmed by technicians/admins; missing telemetry does not prove downtime. Provider acceptance does not prove mailbox delivery. No automatic firmware OTA receiver exists in the current edge release.

[API contracts](API-SPEC.md), [architecture](ARCHITECTURE.md), [troubleshooting](docs/Troubleshooting.md), and [AASX/edge configuration](docs/AASX-and-Edge-Configuration.md) describe supported workflows. Legacy Kubernetes/Cloudflare/ACR artifacts are historical references outside the selected production release.
