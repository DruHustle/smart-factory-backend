# PostgreSQL Free instance memory budget

The 1 CPU / 1 GB Aiven Free instance is shared by the dashboard, device,
telemetry and BaSyx databases. Its 20 connections are an instance limit, not
a separate allowance for each database or application.

## Application candidate budget

| Pool | Maximum connections |
| --- | ---: |
| Node dashboard API, one process | 2 |
| DeviceService device storage | 1 |
| DeviceService dashboard/session storage | 1 |
| IdentityService | 1 |
| AnalyticsService | 1 |
| NotificationService | 1 |
| TelemetryService, including acquisition, delivery and status | 2 |
| Six BaSyx services, one per service | 6 |
| **Total** | **15** |

Five slots remain for provider operations, an operator and bounded migration
work. Idle .NET dashboard/device connections are eligible for pruning after
30 seconds. Keep the Node singleton pool and the existing API/worker split.
Do not start additional API replicas or a second MQTT worker against this
budget. Stop a prior container before replacing it. The one-shot BaSyx
configuration service is not part of steady state; run it separately before
the application starts.

These caps become effective only after deploying this application candidate
and applying its BaSyx Compose configuration. Source review alone is not
evidence that the live pools have changed.

## Database settings

The 27 October 2026 OOM remediation keeps the Free plan and connection limit:

| Aiven setting | Prior value | Remediation value |
| --- | ---: | ---: |
| Application-database `work_mem` (MB) | 2 | 1 |
| `pg.max_parallel_workers_per_gather` | 2 | 0 |
| `pg.autovacuum_max_workers` | 3 | 1 |

Autovacuum remains enabled. The provider's approximately 89 MiB maintenance
allowance can otherwise be allocated by each of three simultaneous autovacuum
workers. One worker reduces that possible concurrency. Queries on the single
CPU run without extra parallel query workers. Smaller per-operation query
memory may increase temporary I/O for large analytics queries.

Aiven warns that the autovacuum-worker change restarts the database. Inspect
the effective settings and server start time after applying it rather than
assuming a restart occurred. Apply the worker settings through the existing
service's Advanced configuration. Do not change
the service plan, disable backups, disable vacuum, delete telemetry or increase
`max_connections` to accommodate oversized client pools.

On this service, the provider retained a global `work_mem=2MB` even after
saving `work_mem=1` in Advanced configuration. Apply the verified 1 MB default
to each application database instead:

```sql
BEGIN;
SET LOCAL lock_timeout='3s';
ALTER DATABASE basyx SET work_mem='1MB';
ALTER DATABASE smart_factory_dashboard SET work_mem='1MB';
ALTER DATABASE smart_factory_devices SET work_mem='1MB';
ALTER DATABASE smart_factory_telemetry SET work_mem='1MB';
COMMIT;
```

All four databases previously had no `work_mem` override. Verify their entries
in `pg_db_role_setting` and `SHOW work_mem` in a new connection. Existing client
sessions adopt this default on reconnection; do not terminate them to force it.

## Queue behavior

The telemetry outbox backs off empty/error checks to at most 30 seconds. A
committed MQTT acquisition coalesces a wakeup to resume delivery immediately;
the wakeup is a hint and PostgreSQL remains the durable queue. Startup scans
the queue, so loss of an in-process hint cannot lose a delivery. Existing
deduplication, retry deadlines, quarantines and acknowledgement boundaries
remain in force. Notification checks back off from 5 to 30 seconds when empty
or unavailable and reset after work. New email may therefore wait up to 30
seconds before the next claim.

## Verify and recover

Use `pg_settings` to verify the effective settings and restart time. Check
`pg_stat_activity` for connection counts, transaction ages and locks. Inspect
available memory and OOM events, database temporary-file counters, per-table
dead tuples and autovacuum progress under representative load. Sustained
vacuum backlog requires re-evaluating the maintenance budget; do not disable
vacuum to hide memory usage.

Verify API liveness/readiness, a fresh compatible singleton-worker receipt,
MQTT redelivery, queue draining and AAS reads after reconnection. A short
healthy observation is not proof that OOM can never recur.

The Aiven settings can be reverted to the prior values above using the same
configuration screen; restoring the worker count may restart PostgreSQL. To
restore the previous database defaults, run `ALTER DATABASE <name> RESET work_mem`
for each of the four application databases. Preserve
the existing image and runtime environment for application rollback. The
qualified Oracle image update procedure remains in the dashboard repository's
`deploy/oracle/README.md`; never reactivate a legacy Render worker.
