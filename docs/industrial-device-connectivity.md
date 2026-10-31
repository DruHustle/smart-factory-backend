# Industrial Device Connectivity

The backend provisions a versioned, read-only gateway configuration and carries typed tag observations, OPC UA FX evidence and TSN diagnostics through the existing telemetry worker. There is no vendor preset or additional MQTT consumer. The dashboard owns inventory, permissions, revisions and asset bindings; the commissioned gateway owns device sessions, local credentials and network collectors.

## Provisioning

The private DeviceService exposes two additional routes. Both require `X-AAS-Provisioning-Token` using the existing configured `AAS_PROVISIONING_TOKEN`. A dashboard JWT alone does not grant access. The dashboard must authorize the caller, check the registered gateway and save the reviewed desired revision before using these routes.

| Route | Body | Result |
|---|---|---|
| `POST /api/assets/connectivity/sync` | `industrial-connectivity/v1` configuration | MQTT QoS 1 retained publication; HTTP 202 means published, with `applied: false` |
| `POST /api/assets/connectivity/probe` | Gateway identity, UUID command identity, expiry within 60 seconds, one full connection | MQTT QoS 1 non-retained probe; HTTP 202 does not establish a successful device connection |

For configuration, the publisher sends `{"action":"replace_connectivity_configuration","configuration":...}` to `factory/{site}/{line}/{gateway}/commands/connectivity`. The existing asset profile remains on `factory/{site}/{line}/{gateway}/commands`. These retained documents must have separate topics so one cannot overwrite the other during gateway reconnect. An optional `EDGE_COMMAND_TOPIC_TEMPLATE` still supplies the base command topic; the new retained profile appends `/connectivity`.

For probes, the publisher sends `action: "probe_connectivity"`, `gatewayDeviceId`, `commandId`, `expiresAt` and `connection` on the base command topic without retaining it. The gateway returns a correlated report on its normal telemetry route. A timeout means no confirmed result; it does not establish that a remote operation failed. Probes only perform the read/discovery operations supported by the selected driver.

Topic identity uses the existing `mqtt-topic-v1` dot encoding. The complete route, including `/connectivity`, is limited to 255 UTF-8 bytes. Gateway broker permissions must explicitly permit its encoded connectivity command topic as well as its existing command, telemetry, heartbeat and acknowledgement topics. Do not widen a factory ACL to a wildcard or rename commissioned equipment to work around route permissions.

### Configuration contract

The configuration is at most 64000 UTF-8 bytes, leaving headroom inside the 64 KiB MQTT command for its wrapper. Configuration contains `schemaVersion: "industrial-connectivity/v1"`, `gatewayDeviceId`, a positive safe-integer `revision` and up to 16 `connections`. Revisions describe the complete desired document; disabling or removing connections is represented by the replacement document. An empty connection list is valid.

Each connection has a stable `connectionId`, name, protocol, endpoint URI, enabled flag, 100–60000 millisecond interval and up to 100 independently identified tags. Optional fields are `credentialRef`, mode (`live` or explicitly labelled `simulated`), simulation identity, FX configuration and TSN configuration. `gateway-configuration` is reserved for the gateway receipt and cannot name a connection.

| Protocol | Endpoint | Tag source |
|---|---|---|
| `opcua` | `opc.tcp://host:port/path` | Node ID, optionally a namespace URI |
| `modbus_tcp` | `tcp://host:port` | Register table, zero-based address, unit ID, register width and byte/word order |
| `modbus_rtu` | `serial:///dev/ttyUSB0?baudrate=9600&bytesize=8&parity=N&stopbits=1` | Same reviewed register mapping as TCP |
| `mqtt` | `mqtt://host:port` or `mqtts://host:port` | Literal topic and RFC 6901 JSON pointers for value and optional time/quality |
| `opcua_fx`, diagnostics | `opc.tcp://host:port/path` | Node ID/namespace URI; `fx.mode: "diagnostics"` |
| `opcua_fx`, UADP observation | `opc.udp://address:port` | Field index, explicit publisher/type, writer group, dataset writer and metadata versions |

Tags may include a unit, an explicit asset/property binding and scale/offset. Supported source types are boolean, signed/unsigned 16-, 32- and 64-bit integers, float32, float64 and string. Numeric 16-/32-bit source encodings may produce fractional engineering values after scaling. A 64-bit integer is carried as a bounded exact decimal string, preserving values that JavaScript numbers cannot represent exactly. Boolean, string and 64-bit integer mappings require identity scaling. Modbus register counts must match the source type, and the address span cannot cross the table boundary. A UADP dataset requires its complete nonempty field layout, with unique contiguous indexes starting at zero.

Credentials are never embedded in the configuration or endpoint URL. `credentialRef` selects a gateway-local credential/trust entry. `tsn.collectorRef` selects a locally commissioned collector. Neither reference can supply arbitrary commands, socket paths or secret material. Serial options are explicitly enumerated; network protocols reject unexpected queries and URL credentials. Unknown or duplicate JSON fields, invalid types, duplicate identities, oversized documents and mismatched driver sources are rejected before publication. Error responses do not echo configuration values.

## Reports and applied revisions

The gateway publishes the existing telemetry payload on its existing `factory/{site}/{line}/{gateway}/telemetry` topic. The payload has `deviceId == gatewayId`, its established provenance `integration` object and one optional `connectivity` envelope. It does not invent a child device identity or an outer asset identity. The dashboard resolves each tag against the published revision and its saved asset binding.

The envelope uses `schemaVersion: "industrial-observation/v1"`, connection identity, configuration revision, UUID event identity, UTC observation time ending in `Z`, connection state and up to 20 tags. Optional fields are a bounded diagnostic error code, a probe command UUID, discovered tags, FX evidence and TSN evidence. The envelope budget is 15000 bytes; the complete telemetry payload still has the existing 16384-byte limit. Drivers batch observations to fit that transport budget.

Each tag preserves its source type, typed value, unit, source/server timestamps where available, quality, native status and quality basis. A null value cannot claim good quality. Connection health, tag quality and provenance remain separate facts. Unauthenticated UDP observation carries its own quality basis; it cannot establish an FX model or deterministic timing.

After applying a desired revision, including a valid empty configuration, the gateway sends an ordinary observation with `connectionId: "gateway-configuration"`, that revision, state `waiting` or `disabled` and an empty tag list. It contains no tag discovery, diagnostics, error code or probe identity. This is an applied revision receipt, not proof that every configured device is reachable. The existing command acknowledgement topic remains available. DeviceService never marks a profile applied based on broker acceptance alone.

## OPC UA FX and TSN boundaries

FX diagnostics carry recognized AutomationComponent, FunctionalEntity, ConnectionEndpoint and ConnectionManager observations. UADP observation has an explicitly commissioned publisher/dataset filter, metadata versions and `allowUnsecured: true`; this mode always reports `modelVerified: false`. Read-only observation does not create or establish an FX ConnectionManager connection. See the OPC Foundation [Part 81 communication model](https://reference.opcfoundation.org/specs/OPC-10000-81/5.5.6.1) for the distinction between model access and process-data connections.

TSN reports contain the gateway interface, observation time, clock state/domain/grandmaster/offset, hardware timestamping capability, observed queue disciplines, counters and collector availability. Missing collectors remain unavailable or unknown. Evidence cannot be timestamped after its enclosing observation beyond the one-second collection allowance. A configured queue discipline, observed clock state or software test does not establish hardware enforcement, end-to-end deadlines or an industrial TSN profile. Both FX and TSN envelopes require `determinism: "unverified"`. The backend rejects a claim that this implementation verified determinism.

## Persistence, delivery and compatibility

Apply the ordered telemetry migration manifest before starting the updated worker. The new `20261031_industrial_connectivity.sql` adds nullable `ConnectivityJson` to `TelemetryRecords`. Existing migrations and rows are preserved. The standard `TelemetryService --migrate` command applies the migration under the existing advisory lock and content-hash ledger; rerunning it is idempotent.

Connectivity reports are validated independently in the backend, canonically ordered, stored durably and forwarded to the existing dashboard ingestion endpoint by the same outbox. Different event UUIDs remain different records; property reordering deduplicates. Delivery revalidates persisted envelopes, and invalid historical connectivity enters `historical_connectivity_validation` quarantine without an HTTP acknowledgement. Receiver error classes, bounded retries, retain evidence and provenance rules remain those of the [telemetry policy](telemetry-provenance-policy.md).

Absent or null connectivity preserves the prior ingestion identity byte for byte. Legacy asset configuration, source identities, metric/unit mappings and existing telemetry are unchanged. The fixture intentionally combines supported value types for contract validation; its observation is not a deployable device map and must not be sent to inventory-bound ingestion without preparing matching connection/tag bindings.

## Qualification

`fixtures/connectivity/industrial-v1.json` is a local copy of the versioned fixture used by the peer implementations. Backend tests independently exercise all protocol configurations, nested strict validation, probe expiry, separate retained topics, token authorization, typed values, unsupported assurance rejection, preserved legacy identity, outbox restart/deduplication and corrupted-row quarantine.

The PostgreSQL test first applies the preceding manifest, inserts a legacy record, applies the additive migration twice, verifies that the legacy value and identity survive, and persists/reloads/deduplicates a typed report through a real `TelemetryDbContext`. It requires a disposable local database whose name ends in `_e2e`.

```bash
dotnet restore src/SmartFactory.sln -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high -warnaserror:NU1900,NU1903,NU1904 --force-evaluate
dotnet test src/SmartFactory.sln -c Release --no-restore
```

Use SDK 10.0.403 from `global.json` and set `TEST_DASHBOARD_DATABASE_CONNECTION` to that disposable database. `--filter 'Category!=Postgres'` runs tests that do not require PostgreSQL. Software qualification and loopback protocol fixtures do not certify the customer's actual controller, register map, broker ACL, certificate trust, serial adapter or TSN hardware.
