-- Permanent validation failures remain visible, never falsely acknowledged.
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "DeliveryQuarantinedAt" timestamp with time zone NULL;
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "DeliveryErrorCode" varchar(128) NULL;
CREATE INDEX IF NOT EXISTS "IX_TelemetryRecords_Deliverable" ON "TelemetryRecords" ("NextDeliveryAttemptAt", "Id") WHERE "DashboardForwardedAt" IS NULL AND "DeliveryQuarantinedAt" IS NULL;
