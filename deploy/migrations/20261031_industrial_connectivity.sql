-- Additive storage for validated industrial tag and OPC UA FX / TSN observations.
-- Existing telemetry identity and outbox delivery remain unchanged when this is NULL.
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "ConnectivityJson" text NULL;
