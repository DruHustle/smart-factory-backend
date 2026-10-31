namespace SmartFactory.Services.TelemetryService.Application.DTOs
{
    /// <summary>Normalized edge payload. Timestamp is Unix epoch milliseconds in UTC.</summary>
    public class TelemetryData
    {
        public System.Text.Json.JsonElement? Integration { get; set; }
        public System.Text.Json.JsonElement? Connectivity { get; set; }
        public Dictionary<string, TelemetryMapping>? Mapping { get; set; }
        public Dictionary<string, string>? Units { get; set; }
        public bool TransportRetained { get; set; }
        public bool? TransportRetainVerified { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string? GatewayId { get; set; }
        public string? AssetId { get; set; }
        public string? SensorType { get; set; }
        public string? SensorStatus { get; set; }
        public Dictionary<string, double>? AssetSignals { get; set; }
        public double? Temperature { get; set; }
        public double? Humidity { get; set; }
        public double? Vibration { get; set; }
        public double? Power { get; set; }
        public double? Pressure { get; set; }
        public double? Rpm { get; set; }
        public long? Timestamp { get; set; }
    }
}
