# Oracle BaSyx runtime

Run this Compose stack on the Oracle VM created by `../terraform/oracle-basyx`. It starts the six BaSyx Go 1.1.0 services, a minimal client-credentials token endpoint, and Caddy. BaSyx ports bind to loopback; HTTPS is the only public application ingress.

```bash
cp .env.example .env
# edit .env; never commit it
docker compose run --rm configuration
docker compose up -d
docker compose ps
```

Create the Aiven `basyx` database and dedicated `basyx_service` user first. Set a unique database password, OAuth client secret, and gateway bearer token in `.env`. Caddy routes `/aas`, `/aas-registry`, `/submodels`, `/submodel-registry`, `/concept-descriptions`, and `/aasx`. Render uses `/oauth/token` with the configured client credentials, then sends the returned bearer token to those component paths.

Restrict SSH at the OCI network rule, leave 8081–8086 bound to `127.0.0.1`, protect `.env` with owner-only permissions, and configure Aiven backups/monitoring. Verify each component's `/description`, then exercise AAS create, import, delete, registry, package, and edge-profile workflows through the dashboard.

## API and worker on this VM

The frontend repository now provides `deploy/oracle/compose.yaml` and the migration
procedure in `deploy/oracle/README.md`. That stack joins this stack's existing
Docker network, and Caddy routes the API hostname to `factory-api:10000`. Keep
the worker stopped until the previous Render worker is confirmed suspended.
The existing BaSyx service names, volumes, HTTPS paths and database remain in place.
