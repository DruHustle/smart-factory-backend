# Smart Factory Backend API and Messaging Contract

This specification documents the current .NET service boundaries and the shared edge telemetry message. The Node/tRPC dashboard API, account roles, AAS procedures, and service-to-service bridge are documented in the root repository's `docs/authorization-and-aas.md`.

## DeviceService

Base path: `/api/devices`.

All routes require a dashboard JWT issued by the Node application: HS256, issuer `smart-factory-iot`, audience `smart-factory-iot-api`, with the shared `JWT_SECRET`.

| Method | Route | Minimum role | Purpose |
|---|---|---|---|
| GET | `/api/devices` | viewer | List device metadata. |
| GET | `/api/devices/{id}` | viewer | Read a device. |
| POST | `/api/devices` | engineer | Register a device. |
| POST | `/api/devices/{id}/trigger-update` | engineer | Returns 501 until a verified release service and device update agent are integrated; it does not queue an update. |
| POST | `/api/devices/{id}/update-status` | engineer | Returns 501 until authenticated device-reported status is integrated; user-supplied status is never accepted. |

IdentityService reads current dashboard accounts and roles through a private Node-delegated request; it does not configure separate Entra user login.

## Asset provisioning API

DeviceService also exposes private dashboard-to-service endpoints under `/api/assets`. Provisioning/import/sync endpoints require the separate `X-AAS-Provisioning-Token` service credential, compared in constant time. Control endpoints instead require a short-lived dashboard JWT with the `engineer` or `admin` role. Never expose DeviceService publicly.

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/assets` | Create and register a form-generated AAS shell, submodels, and registry descriptor. |
| PUT | `/api/assets` | Update a form-managed AAS with `expectedAasVersion` and the next `aasVersion`; returns 409 for stale revisions. |
| POST | `/api/assets/import` | Validate and import an AASX package, bounded to 50 MB and 25 shells. |
| DELETE | `/api/assets/import` | Compensate a failed cross-service AASX database commit using the receipt returned by the importer. |
| POST | `/api/assets/sync` | Validate and publish a gateway's complete desired-state profile. |
| POST | `/api/assets/control` | Publish one short-lived, non-retained ADA031 command for an engineer/admin. |
| POST | `/api/assets/gpio-control` | Publish one bounded, short-lived WROVER indicator pulse for an operator, engineer, or admin. |

Set `AAS_REPOSITORY_URL` for shells, `AAS_SUBMODEL_REPOSITORY_URL` for submodels, `AAS_CONCEPT_DESCRIPTION_REPOSITORY_URL` for Concept Descriptions, `AAS_REGISTRY_URL` and `AAS_SUBMODEL_REGISTRY_URL` for descriptors, and `AASX_FILE_SERVER_URL` for packages. DeviceService obtains OAuth tokens from `AAS_OIDC_TOKEN_URL` using its client id and secret. Redis Pub/Sub is required for production cross-replica AAS change notifications. Set `AAS_REPOSITORY_REGISTRY_INTEGRATION=true` when the repository owns automatic descriptor registration. `PUT` checks the repository revision before writing and returns HTTP 409 when the expected revision is stale; the dashboard keeps the immutable revision snapshots in PostgreSQL.

## TelemetryService

The background subscriber consumes MQTT topic `factory/+/+/+/telemetry`. The supported Compose/Render entry point is a generic .NET worker and exposes only health routes; `/api/telemetry` and `/api/telemetry/{deviceId}` are not served. Telemetry is forwarded to the dashboard API when both `INTERNAL_TELEMETRY_SINK_URL` and `INGESTION_API_TOKEN` are configured.

### Normalized telemetry JSON

```json
{
  "deviceId": "pi-edge-01",
  "gatewayId": "pi-edge-01",
  "assetId": "urn:smart-factory:asset:example",
  "sensorType": "DHT11",
  "sensorStatus": "ok",
  "assetSignals": { "bearingTemp": 74.5 },
  "timestamp": 1791045000000,
  "temperature": 68.4,
  "humidity": null,
  "vibration": 2.1,
  "power": 72000,
  "pressure": 7.4,
  "rpm": 2940
}
```

`timestamp` is Unix epoch milliseconds in UTC. `assetId`, `sensorType`, `sensorStatus`, and measurements are optional. `sensorType` is a case-preserved string of 1–64 characters (`DHT11` for the connected WROVER); `sensorStatus`, when present, is exactly `ok` or `read_error`. A `DHT11` record requires the status: `ok` requires both temperature and humidity, while `read_error` requires both to be absent or null rather than invented. A metric can otherwise be `null` when the connected asset does not provide it. Numeric values are in the source device's engineering unit and require a known unit/scaling mapping.

## MQTT topics

| Topic | Direction | Description |
|---|---|---|
| `factory/{site}/{line}/{deviceId}/telemetry` | Edge publishes, backend subscribes | Normalized telemetry. |
| `factory/{site}/{line}/{deviceId}/heartbeat` | Edge publishes | Device ID, UTC epoch-ms timestamp, and status. |
| `factory/{site}/{line}/{deviceId}/commands` | Edge subscribes | Device command messages. |

Provision broker credentials and topic ACLs per device. Do not put credentials in asset mappings or telemetry payloads.

### ADA031 control JSON

The current schema is version 2. All messages include `commandId`, `targetAssetId`, and an `expiresAt` UTC epoch-millisecond value no more than 30 seconds ahead. `jog` additionally requires `joint` and `direction`; `set_profile` requires `profile`; `neutral` and `stop_program` have no action-specific fields. DeviceService accepts legacy schema-1 jog requests during rolling deployment and emits the legacy `ada031_control` wire shape for those requests.

```json
{
  "schemaVersion": 2,
  "action": "set_profile",
  "commandId": "command-0001",
  "targetAssetId": "urn:smart-factory:asset:ada031-v4-arm-01",
  "expiresAt": 1791045005000,
  "profile": "pick_and_place_repeat"
}
```

Supported actions are `jog`, `set_profile`, `neutral`, and `stop_program`. Profiles are `pick_and_place_repeat` and `demonstration_moves`. Commands publish at QoS 1 with the retained flag off. Broker publication means the broker accepted the message; it does not prove physical motion or a safe stop.

## Delivery semantics

QoS 1 can redeliver messages. TelemetryService acknowledges MQTT only after durable PostgreSQL storage and deduplicates normalized samples by SHA-256. Dashboard bridge failures remain in the bounded-batch PostgreSQL retry outbox with backoff. The dashboard retains the ingestion digest and inserts each retry once. Analytics reads that dashboard database directly; there is no second AMQP telemetry copy. Apply all telemetry SQL migrations before rollout; old historical records are not automatically re-forwarded.

Payloads are limited to 16 KiB, topic identity must match deviceId, timestamps must be positive Unix milliseconds no more than five minutes ahead, humidity must be 0–100%, other top-level metrics must be finite within ±1e9, and at most 32 named signals with finite values within the unsigned-32-bit magnitude are accepted (including the arm's `uptime_ms`). Unknown JSON properties are ignored and excluded from the ingestion digest, so they cannot alter stored telemetry. Unlinked asset attribution is rejected by the dashboard. A broker or serial acknowledgement does not certify a physical machine action.

## Dashboard-backed private services

IdentityService: `GET /api/auth/profile` returns only id, openId, name, email and current normalized role; `GET /api/auth/check-role/{role}` accepts viewer/operator/engineer/admin. There is no token acquisition endpoint and no returned provider access token.

AnalyticsService: `POST /api/analytics/coverage` accepts assetIds (1–200 bounded IDs), startTime/endTime epoch milliseconds and a maximum 93-day range. It returns per-asset sample counts, first/last timestamps, longest observed inter-sample gap, and per-metric count/average/min/max. Missing metrics remain null. This is sample coverage, not OEE or inferred factory uptime.

NotificationService: `GET /api/notifications/status` reports Resend configuration and the at-least-once delivery contract. Its background worker claims the shared PostgreSQL incident inbox and sends through Resend's server-side email API with a notification-specific idempotency key. It accepts no anonymous/arbitrary email-send request. All incident recipient selection occurs transactionally in the dashboard database.

Every `/api` route above requires both the private `X-Service-Token` and the authenticated `X-User-Id` delegated by Node. Services reload the current account from PostgreSQL. These tokens are never exposed to the browser. Each service exposes `/health/live` and schema-aware `/health/ready`; readiness fails if its database/schema is unavailable. Production binds them only to loopback ports 3104–3106.
