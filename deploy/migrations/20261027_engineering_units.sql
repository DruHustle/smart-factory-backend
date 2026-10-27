-- Preserve engineering units and actual broker delivery metadata through the durable outbox.
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "UnitsJson" text NULL;
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "TransportRetained" boolean NOT NULL DEFAULT false;
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "MappingJson" text NULL;
