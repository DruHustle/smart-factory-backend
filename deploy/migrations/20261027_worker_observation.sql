-- Acquisition time is not queue acceptance time. Old records remain explicitly unknown.
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "AcceptedAt" timestamptz NULL;
CREATE INDEX IF NOT EXISTS "IX_TelemetryRecords_PendingAccepted" ON "TelemetryRecords"("AcceptedAt") WHERE "DashboardForwardedAt" IS NULL AND "DeliveryQuarantinedAt" IS NULL;

-- Old outbox records have no verified broker retain semantics and stay historical.
ALTER TABLE "TelemetryRecords" ADD COLUMN IF NOT EXISTS "TransportRetainVerified" boolean NOT NULL DEFAULT false;
