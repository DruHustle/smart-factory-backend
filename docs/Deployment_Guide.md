# Backend deployment

The maintained production topology is Oracle ARM64 API plus a singleton telemetry/notification worker, with a separate prebuilt Vercel UI. The frontend repository owns the combined image, current Oracle Compose configuration and cross-repository release procedure. Use its [Oracle runbook](https://github.com/DruHustle/smart-factory-frontend/blob/main/deploy/oracle/README.md) and [operating guide](https://github.com/DruHustle/smart-factory-frontend/blob/main/docs/operations.md). Dated release records identify historical candidates, not the currently running release.

## Prepare and qualify

Resolve clean, immutable source revisions for frontend, backend and edge. Build the combined ARM64 image through the frontend's `scripts/prepare-backend-context.py` and `deploy/render/Dockerfile`; the historical filename is retained for compatibility. Keep runtime credentials outside the build context. Use the SDK pinned in `global.json` (currently .NET 10.0.403) and run:

```bash
dotnet restore src/SmartFactory.sln -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high -warnaserror:NU1900,NU1903,NU1904 --force-evaluate
dotnet test src/SmartFactory.sln -c Release --no-restore
```

The PostgreSQL tests require `TEST_DASHBOARD_DATABASE_CONNECTION` to point to a disposable local database whose name ends in `_e2e`; they refuse other storage. Full qualification also includes the frontend's bundle smoke, trusted MQTT path, dependency/image audit, browser gates and a disposable restore drill. Unit tests or a ready endpoint alone do not qualify a release. Retain exact source revisions, image ID/registry digest, architecture, migration digests and test evidence together under the workspace's `local-tools/deployment-evidence`.

## Approved production update

Approval must cover the concrete qualified candidate. Runtime owner-only `.env.api` and `.env.worker` files remain under `/opt/smart-factory-backend`; ordinary releases do not replace them. Compose uses raw env-file mode and requires its documented version. Preserve service accounts, database names, JWT issuers and remote runtime paths.

1. Retain and qualify the previous compatible image and frontend artifact. Confirm backup/recovery readiness independently.
2. Transfer and inspect the exact tested ARM64 image. Stop the previous worker before replacing its persistent MQTT subscriber.
3. Start the API role using the established bounded migrations. Apply the telemetry manifest and DeviceService EF migrations through the repository-owned release entry points before the updated worker starts. Never edit an already applied migration.
4. Verify API liveness/readiness, exact source and migration identity, authorization/session policy and the four API processes.
5. Start exactly one worker. Verify Telemetry/Notification, MQTT subscription, durable outbox and a recent authenticated worker receipt.
6. Verify `/api/runtime/contract` with the frontend's `scripts/verify-runtime.mts`, then promote only the compatible frontend after its required exact-revision CI and hosting checks pass. Inspect `/release.json` and critical public journeys afterward.

The API role runs Node, Device, Identity and Analytics. The worker role runs Telemetry and Notification. `RENDER_SERVICE_ROLE` and `BACKEND_DEPLOYMENT_MODE=render-bundle` remain technical compatibility variables; the legacy Render production workflow is disabled. Do not reactivate it as part of ordinary deployment. A single stable MQTT identity cannot be used by overlapping workers.

## Recovery

Use the current cross-repository rollback manifest and qualify its application compatibility with the additive schema. If the UI requires candidate API capabilities, restore the verified prior UI while the candidate API still serves it; then stop the candidate worker and restore the compatible API/worker pair. Check sources, applied schema and the new worker receipt again. Do not reverse migrations or delete evidence during an application rollback. Data restoration needs a separately approved procedure and verified backups.

## Local and historical configurations

For local split Compose development, configure `.env.local` from `.env.local.example`, start the frontend's local dependencies/BaSyx, then run `docker compose --env-file .env.local up --build -d --wait`. Local host ports remain loopback-only: Device 5001, Identity 5002, Notification 5003, Analytics 5004 and Telemetry health 5005. Use only disposable databases/brokers for destructive integration fixtures.

The separate `deploy/oracle-basyx` and `deploy/terraform/oracle-basyx` configurations describe the existing AAS infrastructure; do not reprovision it for an image update. `scripts/split-render-env.mjs`, Render, Kubernetes and Cloudflare examples are historical recovery/reference material, not the maintained production promotion path. A hosting migration requires its own reviewed plan.
