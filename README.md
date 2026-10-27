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

Use the current [Oracle deployment guide](https://github.com/DruHustle/smart-factory-iot-frontend/blob/main/deploy/oracle/README.md) for production. The [current operating guide](https://github.com/DruHustle/smart-factory-iot-frontend/blob/main/docs/operations.md) covers scope, recovery, runtime compatibility and release evidence. Oracle production settings live in owner-only `.env.api` and `.env.worker` files under `/opt/smart-factory-backend`; the migration preserved the live Render values. The `split-render-env.mjs` helper and Render examples remain for historical rollback use. Start dashboard Compose/BaSyx first, then this repository's Compose services. Replace every `.env.local.example` placeholder and configure the dashboard DB connection and shared internal service token. Local host ports are Device 5001, Identity 5002, Notifications 5003, Analytics 5004, Telemetry health 5005. All bind to loopback. Company AAS services and managed production databases/broker/Redis remain external to the Oracle VM.

## Tests

```bash
dotnet restore src/SmartFactory.sln -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high -warnaserror:NU1900,NU1903,NU1904 --force-evaluate
dotnet test src/SmartFactory.sln -c Release --no-restore
```

Use the SDK pinned in `global.json` or its matching .NET 10 SDK container; do not qualify this candidate with .NET 8. The dashboard's `pnpm e2e` covers role and browser workflows; `python3 scripts/bundle-smoke.py` there tests the exact six-service image and real disposable dependencies; `pnpm e2e:system` covers real AAS/MQTT/Pi code with simulated serial equipment. Before each production update, scan the exact combined image locally with telemetry disabled and reject high/critical findings. Runtime Node build tools are excluded. See [integration test evidence](docs/E2E_Integration_Test_Strategy.md).

For live AAS acceptance, use the local BaSyx profile and `AAS_LIVE_TESTS=true dotnet test src/SmartFactory.sln --filter FullyQualifiedName~AasxLiveIntegrationTests`. Set `AASX_TEST_CORPUS_DIR` to licensed vendor fixtures for optional parser checks; do not commit vendor assets without distribution permission. Local browser AAS responses are deterministic stubs; they do not certify the company's IDTA conformance.

## Runtime limits

The candidate targets .NET 10 LTS with the pinned SDK/runtime and compatible packages; see [qualification](docs/net10-qualification.md) and [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). .NET 8 reaches end of support on 10 November 2026. Telemetry has one stable persistent MQTT identity, so Oracle worker deployments must stop the previous container before starting its replacement. Factory downtime is explicitly confirmed by technicians/admins; missing telemetry does not prove downtime. Provider acceptance does not prove mailbox delivery. No automatic firmware OTA receiver exists in the current edge release.

[API contracts](API-SPEC.md), [architecture](ARCHITECTURE.md), [troubleshooting](docs/Troubleshooting.md), and [AASX/edge configuration](docs/AASX-and-Edge-Configuration.md) describe supported workflows. Legacy Kubernetes/Cloudflare/ACR artifacts are historical references outside the selected production release.


### Optional Smart Factory equipment provenance

TelemetryService accepts an optional `integration` object on the existing MQTT payload and persists it as `IntegrationJson`, unchanged through bridge retries. It carries explicit live/simulated/replay mode, UTC gateway receipt time, source quality/connection, session/sequence and scenario/replay identity. Simulated/replay data remains classified even over real MQTT. Legacy payloads retain their previous ingestion identity and explicit unclassified history; current eligibility and units require reviewed source policy. Apply `deploy/migrations/20261025_onesa_provenance.sql` before deploying the updated worker; `--migrate` includes it. No Onesa service or credential is required by this worker. The dashboard owns local 3D and the optional outbound adapter; no new MQTT subscriber or poller is introduced. Native model storage and rendering remain within the dashboard repository.

## Native OT security evidence

See [the service observation boundary](docs/cybersecurity.md) for sanitized actual access decisions, durable delivery, limits and current Oracle release procedure.


The worker uses MQTT 5 persistent sessions and Retain As Published subscriptions for both telemetry and heartbeat topics. MQTT 3.1.1 publishers remain compatible through the broker. Broker support must be independently qualified before promotion. Old queued deliveries without verified transport flags remain historical. A single completion-driven status receipt every 30 seconds reports source/runtime, verified migration digest, subscription and aggregate queue depth/oldest known acceptance; no factory identities or credentials are included.

Telemetry `--migrate` applies the ordered JSON manifest under an advisory transaction lock and records content hashes. An altered applied migration fails closed. `AcceptedAt` records queue acceptance separately from acquisition; old rows keep unknown acceptance time. The dashboard repository's `scripts/qualification/trusted-path.py` exercises real edge spool/MQTT/.NET/Node delivery, mixed evidence, retained delivery, duplicate/restart recovery and a disposable identity-preserving database restore. These software checks do not establish a hosted broker, hardware or production backup outcome.

## MQTT identity and retained evidence

Canonical IDs retain dots and colons in JSON and inventory. The `mqtt-topic-v1` contract encodes each literal dot in a topic level as `%2E` (`sensor.temp:01` becomes `sensor%2Etemp:01`); only that escape is decoded. The complete encoded route is limited to 255 UTF-8 bytes for RabbitMQ routing compatibility. Publishers, gateway profiles and device-specific ACLs must use the same reviewed encoded route. Do not rename a commissioned identity or change factory ACLs automatically. See the [shared telemetry contract](https://github.com/DruHustle/smart-factory-iot-frontend/blob/main/docs/telemetry-contract.md) and [RabbitMQ topic semantics](https://www.rabbitmq.com/docs/mqtt#topic-level-separator-and-wildcards).

The Pi-local ingress and cloud subscriber both use MQTT 5 Retain As Published. An upstream gateway child without verified transport context remains historical even when the cloud subscription is verified; downstream delivery cannot upgrade an explicit false upstream flag. The disposable two-broker acceptance passed with both RabbitMQ 4.3.6 and Mosquitto 2.0.22 as cloud broker, including punctuated identities, retained delivery, outage/restart, deduplication and database restore. This does not attest a hosted broker configuration.
