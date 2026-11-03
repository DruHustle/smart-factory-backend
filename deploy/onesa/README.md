# Smart Factory Onesa migration candidate

These files prepare `https://factory.onesa.eu` and the requested Onesa UG
backend consolidation. They are **source candidates**, not a live migration or
installation receipt. Keep the current backend and database primary until a
separately qualified destination and coordinated cutover are ready.

## Application domain, identity and email

Merge the relevant fields from `api.env.example` and `worker.env.example`
into the retained private configuration. They are intentionally incomplete and
must not replace it. Keep all actual secrets, runtime files, certificates and
dumps outside this checkout and image build contexts. Preserve signing keys,
service tokens, existing identities, scoped integration settings and the exact
commissioned MQTT client ID. URL-encode URI credentials and retain correct
quoting/escaping in the existing private secret mechanism.

Set the API's `ALLOWED_ORIGIN` and both roles' `PASSWORD_RESET_ORIGIN` to the
canonical `https://factory.onesa.eu` at cutover. Keep only qualified origins
during transition. The worker's `DASHBOARD_API_ORIGIN` must be the real direct
HTTPS API origin with working certificate verification and host routing; do not
send internal credentials through a guessed UI/proxy URL. Preserve the existing
frontend's first-party API proxy and verify sessions, allowed/denied origins and
reset-link redemption on the new domain.

The combined Node/.NET image and the `api`/`worker` Compose services are owned
by the [frontend repository's release contract](https://github.com/DruHustle/smart-factory-frontend/blob/main/deploy/oracle/README.md).
Keep `BACKEND_DEPLOYMENT_MODE=render-bundle` and the existing role names even
on Oracle or a future Kubernetes deployment: these identify the implemented
bundled runtime contract. An unsupported `kubernetes` mode is not introduced.

Resend uses `RESEND_API_KEY`, `RESEND_FROM` and
`RESEND_ALLOWED_RECIPIENT_DOMAINS` in the singleton worker. The sender example
expects verified `factory.onesa.eu`. The recipient-domain policy must preserve
approved recipient domains; it is independent of sender DNS verification. No
provider credential belongs in the API example. Keep
`EMAIL_DELIVERY_CONFIGURED=false` until real worker delivery, expected denial
cases and one-time reset redemption are qualified. Verified sender DNS alone
does not prove a scoped key, provider acceptance or mailbox delivery.

The combined entrypoint materializes `DATABASE_CA_CERT` at
`/tmp/smart-factory-aiven-ca.pem` and rewrites the recognized
`/run/secrets/aiven-ca.pem` placeholder used in the .NET examples. Node uses
the same trusted CA with `DATABASE_SSL_MODE=verify-full`; .NET uses VerifyFull.
Preserve separate dashboard/device/telemetry database identities and any BaSyx
database in the inventory.

The current combined image runs startup migrations with the same configured
application connections used during normal operation. Those app roles need the
required migration privileges within their own databases. A separate runtime
and migration credential split requires a reviewed image/entrypoint change; it
is not implemented by these examples.

## Optional PostgreSQL container

`compose.database.yaml` is a separate, unapplied Compose project with the
`candidate-database` profile. It joins only an internal database network and
publishes no host ports. Its external named volume must already exist on reviewed
durable storage; Compose does not create a differently named replacement or own
its deletion. Never prune data volumes. A volume on the same VM is not an
independent backup.

The optional `compose.application-database.yaml` adds that network to the
existing frontend-owned `api` and `worker` services. It explicitly retains
the worker's otherwise implicit `default` network, and merges with the API's
existing `default` and `basyx` connections. It does not change image, environment,
ports or dependencies. Use it only with a reviewed retained application manifest;
do not merge the database-only project into the app project. It neither changes
`DATABASE_URL` nor forces the container on external-database users.

Before a worker cutover, reconcile the durable MQTT/outbox backlog, stop the prior
singleton, and preserve its MQTT client ID/session. Never run a second production
subscriber while qualifying the replacement.

Before starting even an isolated candidate:

1. Inspect the source PostgreSQL major version, extensions, encoding, collation,
   database names and roles. Pin an official `postgres` image **by digest**,
   with its actual platform and named postgres UID/GID. No image version,
   architecture, UID, capacity or volume is selected in these examples.
2. Measure CPU, memory, shared memory, connection counts and storage growth.
   Complete every required field in `database.env.example`, retaining host and
   application headroom. Confirm the existing application image is compatible
   with the actual destination architecture.
3. Use a new, empty rehearsal volume preowned by the selected image's postgres
   UID/GID. The non-root container cannot repair arbitrary ownership. The data
   directory, socket directory, shared memory and /tmp are the writable paths.
   Keep the bootstrap password file and TLS key private and readable by that UID.
   Compose file-backed secrets preserve host ownership; `uid`/`mode` settings
   do not repair unreadable host files.
4. The manifest explicitly sets `PGDATA=/var/lib/postgresql/data/pgdata` beneath
   its retained mount. PostgreSQL 18+ images changed their default layout; verify
   this override with the selected image's entrypoint and fresh-start/restart
   behavior. Do not remove the override while retaining this mount or swap a
   major version over populated data. A layout/major change needs its own restore
   qualification.
5. Mount existing `server.crt` and `server.key` files before startup. The
   certificate must cover `factory-postgres`, or the actual selected connection
   hostname. The key belongs only to the DB identity; clients receive the
   issuing CA certificate. Network sessions require TLS 1.2+ and SCRAM.
6. Bootstrap administration uses the named postgres OS identity over its local
   socket. The HBA rejects network login for the role named `postgres`; it does
   **not** identify all possible restored superusers by privilege. Audit restored
   roles and exclude/disable additional administrative logins before connecting
   applications. Never give an app a superuser connection.
7. Validate with
   `docker compose --env-file <private-env> -f deploy/onesa/compose.database.yaml --profile candidate-database config --quiet`
   from this repository. Never print resolved private configuration. The env
   file supplies Compose interpolation; it does not inject the application env
   examples into the running app. Use the existing private runtime mechanism.

Only the bootstrap postgres database is initialized. A populated PGDATA skips
bootstrap; changing `POSTGRES_*` variables does not rotate existing passwords
or migrate data. Preserve a failed initialization for inspection instead of
deleting source data. The `pg_isready` check is process readiness, and can also
answer during temporary initialization; it does not prove authenticated app
access, completed restore, TLS validation or data integrity.

## Data, release and recovery gates

Keep the existing external primary selected until a consistent backup has been
restored into a disposable candidate and verified. The app connection remains
configurable for an external database or a qualified container; this preparation
does not add double-write, replication or automatic failover.

Verify schema/migration ledgers, owners, identities and record counts; valid and
invalid CA/hostname outcomes; plaintext rejection; least-privilege app access;
restart and container-recreation persistence; and measured resource use. Restore
a backup of the candidate again. Retain encrypted database-native backups
outside the VM with separately retained decryption keys and a reviewed retention
policy. Logical dumps recover to their backup point; tighter recovery objectives
need a qualified base-backup/WAL-archive scheme. No external backup provider is
selected here. Backblaze B2, if selected later, stores backup objects and is not a
live relational database.

Pause source writers for the final copy and coordinate one active primary.
Retain source data and compatible rollback images. After new writes, reconcile
those writes before reverting a database; changing DNS alone is not a data
rollback. Keep the existing release/readiness/compatibility gates and unrelated
services intact.

## What source checks establish

`python3 scripts/test_onesa_migration_config.py --require-compose` runs boundary
checks and Docker Compose **configuration-only** checks with temporary synthetic
inputs. It does not pull/start database images, initialize a database, perform
TLS handshakes or restore data. CI validates this newly authored source; it does
not reproduce or certify the earlier unpublished local preparation.

The actual image digest, init permissions, restore, private network isolation,
mail delivery and destination runtime remain unqualified. Kubernetes installation
requires the measured host, owner access and approved operating contract. Archived
manifests do not become production-ready merely because a cluster is requested.

Primary references:
[official PostgreSQL image](https://github.com/docker-library/docs/blob/master/postgres/README.md),
[image initialization](https://github.com/docker-library/postgres/blob/master/docker-entrypoint.sh),
[PostgreSQL backup methods](https://www.postgresql.org/docs/current/backup.html),
and [Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/).
