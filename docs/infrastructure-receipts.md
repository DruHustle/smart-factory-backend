# Infrastructure worker receipts

The telemetry worker adds optional `resources` and `notificationReady` fields to its authenticated status receipt. Resource measurements cover this .NET process: resident working-set bytes, CPU milliseconds consumed per elapsed second, and elapsed database-query milliseconds. The first CPU interval is unknown. These are neither host nor container totals, and no provider capacity percentage is inferred.

On bundled worker deployments, notification readiness comes from the fixed loopback NotificationService readiness endpoint with a two-second timeout. Readiness, queued requests, provider acceptance and confirmed recipient delivery remain separate observations. A missing readiness collector remains unknown. Collection is completion-driven on the existing 30-second receipt loop; it reuses the existing database context and HTTP client factory.

Deploy the compatible Node receipt receiver first. Its older strict schema rejects additional fields. Stop the old singleton worker before starting the replacement, then verify a fresh compatible receipt and broker subscription. Resource receipts do not establish physical equipment availability. No plant commands, restart controls or new external probe targets are introduced.

Validation: all 239 .NET tests passed with the pinned .NET 10.0.403 SDK and disposable local PostgreSQL, including persisted authorization and mixed-evidence coverage tests. Full packaged worker/API qualification is recorded separately with the frontend release.
