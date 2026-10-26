namespace SmartFactory.Services.TelemetryService.Domain.Entities
{
    public class TelemetryRecord
    {
        public int Id { get; set; }
        public string? IntegrationJson { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string? GatewayId { get; set; }
        public string? AssetId { get; set; }
        public string? SensorType { get; set; }
        public string? SensorStatus { get; set; }
        public string? AssetSignalsJson { get; set; }
        public string? IngestionId { get; set; }
        public DateTime? DashboardForwardedAt { get; set; }
        public DateTime? NextDeliveryAttemptAt { get; set; }
        public int DeliveryAttempts { get; set; }
        public DateTime? DeliveryQuarantinedAt { get; set; }
        public string? DeliveryErrorCode { get; set; }
        public double? Temperature { get; set; }
        public double? Humidity { get; set; }
        public double? Vibration { get; set; }
        public double? Power { get; set; }
        public double? Pressure { get; set; }
        public double? Rpm { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
