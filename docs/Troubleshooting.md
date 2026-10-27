# Backend Troubleshooting

| Symptom | Checks |
|---|---|
| Compose reports missing variables | Copy `.env.local.example` to `.env.local` and replace every `CHANGE_ME`; inspect `docker compose --env-file .env.local config`. |
| PostgreSQL password fails after `.env.local` edit | The named volume retains the initial password. Rotate the user in PostgreSQL and update the secret, or use a fresh disposable local volume. |
| Telemetry reports a missing column | Run `docker compose --env-file .env.local run --rm telemetry-schema-prepare` locally. In production, run the release command `dotnet TelemetryService.dll --migrate` with `TELEMETRY_MIGRATIONS_DIR` pointing at all versioned SQL files before starting the worker. Use the complete ordered `deploy/migrations/manifest.json`; the CLI verifies content hashes and refuses an altered applied migration. Do not run a hand-picked historical prefix. |
| DeviceService readiness is 503 | Check the Aiven connection string and CA, PostgreSQL allowlist/availability, and Redis Cloud URL/TLS. The readiness probe checks both PostgreSQL and Redis; production rejects plaintext Redis. |
| DeviceService returns 401 | Check the current dashboard JWT, issuer/audience, HS256 secret, and system clock. |
| DeviceService returns 403 | Reads allow viewer and higher; writes require engineer/admin. Check the individually authenticated account, current database role, disabled/session-generation state and shared-demo policy. |
| Backend container is unhealthy | Rebuild the current image, confirm its health route listens on container port 8080, then inspect the container health-check output. |
| Port is already in use | Check ports 5001–5005, 5432, 5672, 15672, and 1883. Keep local dependencies on loopback. |
| AAS provisioning returns 502/503 | Check each AAS/SM Repository, Registry, Concept Description, and AASX File Server URL independently. Verify API profiles at `/description`, service credentials, TLS, and repository auto-registration settings. |
| Duplicate AAS descriptors on import | Set `AAS_REPOSITORY_REGISTRY_INTEGRATION=true` only when the repository auto-registers with the registry; otherwise leave it false and configure the dedicated Registry URL. |
| AASX package fails parsing | The generated C# SDK reference is metamodel 3.1. The parser normalizes AAS 3.0/3.2 XML namespaces and projects v3.2 `createdAt`/`updatedAt` fields for validation; unsupported metamodel/vendor fields may still fail. Run the parser tests with the extracted IDTA specification example or a licensed corpus in `AASX_TEST_CORPUS_DIR`. |
| TelemetryService cannot connect to CloudAMQP MQTT | Check the MQTT plugin, CloudAMQP TLS endpoint/port 8883, username/vhost, `MqttUseTls=true`, trusted CA, and topic ACL. Its MQTT identity needs subscribe access to `factory/+/+/+/telemetry` and `factory/+/+/+/heartbeat`. ESP32 clients connect to the Pi-local broker, never CloudAMQP. |
| Physical Pi appears Offline in the local dashboard | The local split-Compose TelemetryService defaults to its private `rabbitmq:1883`, while a production Pi may publish to CloudAMQP. Set `TELEMETRY_MQTT_HOST`, `TELEMETRY_MQTT_PORT=8883`, `TELEMETRY_MQTT_USERNAME`, `TELEMETRY_MQTT_PASSWORD`, and `TELEMETRY_MQTT_USE_TLS=true` in the backend `.env.local`, then recreate TelemetryService. Use a dedicated subscriber account with read-only access to the telemetry and heartbeat topic filters; never put subscriber credentials on the Pi. Match the broker topic's site/line/device IDs to the registered gateway. |
| MQTT connects but no reading is stored | Check the encoded topic (`.` becomes `%2E`), canonical JSON `deviceId`, reviewed source/route, UTC acquisition time, units, quarantine counters and consumer logs. Unverified retained-delivery context remains historical. |
| Dashboard has no forwarded readings | Configure both bridge URL and distinct `INGESTION_API_TOKEN`; check HTTPS, network route, exact token, and API logs. |

For login/AAS issues see the dashboard repository's [login troubleshooting](https://github.com/DruHustle/smart-factory-iot-frontend/blob/main/LOGIN_TROUBLESHOOTING.md).

For combined Oracle bundle readiness 503, inspect the six Supervisor process statuses and their private health routes, then check database migrations and verified PostgreSQL/Redis/MQTT connectivity. Keep secrets out of logs. See the canonical [Oracle operating guide](https://github.com/DruHustle/smart-factory-iot-frontend/blob/main/deploy/oracle/README.md).
