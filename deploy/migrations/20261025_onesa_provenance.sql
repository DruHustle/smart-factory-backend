-- Original gateway provenance is persisted once, never regenerated on retry.
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "IntegrationJson" text NULL;
