# .NET 10 qualification candidate — 27 October 2026

All projects target stable .NET 10. The SDK is pinned to 10.0.403 with previews disabled; runtime images use 10.0.14-noble. EF Core, ASP.NET authentication/testing and Microsoft extensions use compatible 10.0.14 packages; PostgreSQL providers use 10.0.3. System.Text.Json and regular expressions use the supported framework instead of obsolete explicit packages. MQTTnet and AAS SDK versions are preserved and tested.

References: [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [support deadline](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/), [.NET 10 compatibility](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10), [ASP.NET migration](https://learn.microsoft.com/en-us/aspnet/core/migration/90-to-100?view=aspnetcore-10.0).

Reviewed changes include container OS compatibility, BackgroundService startup scheduling, configuration null semantics, API authentication responses and service entry-point selection in tests. The combined image already uses Ubuntu Noble and copies the older-glibc-compatible Node binary into that runtime. An explicit DeviceRuntime project alias keeps the DeviceService HTTP test host unambiguous among service references.

Completed locally: full NuGet audit (all dependencies, high/critical advisories and unavailable audit feeds treated as errors), compilation and 230 tests on the SHA-512-verified official macOS ARM64 SDK. The additional PostgreSQL regression ran with explicit local disposable storage and verifies persisted session revocation, role/version checks, disable/re-enable and shared/public-deployment denial. An earlier container run passed the existing 216 tests. These results do not establish a production deployment or physical commissioning.

CI now provides disposable PostgreSQL and requires the real session test. For a complete local run set TEST_DASHBOARD_DATABASE_CONNECTION to a local database ending in _e2e. The test creates/drops only its own random schema. Unit-only execution may use `dotnet test src/SmartFactory.sln --filter Category!=Postgres`; this does not qualify the database boundary.

Delegated DeviceService JWTs retain the parent session ID and generation. The service independently checks current database identity, role, disablement, forced-password state and the hashed session-revocation list before authorization reaches a controller/publisher. Internal profile/analytics calls also carry the parent session identity. Missing verification storage fails closed. Schema 0024 must precede the coordinated API release; old sid/generation-less cookies need a new sign-in.

The final combined ARM64 bundle, MQTT/.NET/Node interoperability, immutable release manifest and hosted promotion are separate release acceptance results and must be recorded with the final source revisions.

The trusted-observation candidate preserves source units, raw scaling evidence and broker-retained delivery through the durable outbox. Its new additive migration is included in the actual `--migrate-only` file list. Coverage applies evidence scope before aggregation and only aggregates good-quality canonical units; unclassified legacy data is counted separately. The dashboard owns route commissioning and current-observation eligibility. See the coordinated [engineering contract](https://github.com/DruHustle/smart-factory-iot-frontend/blob/feat/product-experience-trust/docs/trusted-observations.md).


## Worker migration and retained-delivery qualification

The actual .NET 10 executable applied eight manifest migrations to fresh disposable PostgreSQL, repeated idempotently, verified the five operational contract columns, and rejected an altered applied migration hash. Full service tests remained 230 passing. The real Mosquitto/Python spool/.NET/Node path exposed MQTT retained flags being cleared on queued delivery; the worker now requires MQTT 5 Retain As Published and treats pre-verification/legacy outbox transport context as historical. The repaired real path passed restart, duplicate, mixed-mode/units, route quarantine and disposable restore identity checks. Evidence is recorded by the dashboard qualification runner against its own synthetic databases.

Broker MQTT 5 support is a separate hosted qualification requirement. No factory broker configuration, command, firmware flash, email delivery or production deployment was performed by these tests. Protocol semantics: [OASIS MQTT 5 subscription options](https://docs.oasis-open.org/mqtt/mqtt/v5.0/mqtt-v5.0.html).
