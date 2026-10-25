using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFactory.Services.TelemetryService.Application.DTOs;
using SmartFactory.Services.TelemetryService.Domain.Entities;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;

namespace SmartFactory.Services.TelemetryService
{
    public class TelemetryFunction
    {
        private readonly ILogger _logger;
        private readonly TelemetryDbContext _dbContext;
        private readonly IHttpClientFactory? _httpClientFactory;

        public TelemetryFunction(
            ILoggerFactory loggerFactory,
            TelemetryDbContext dbContext,
            IHttpClientFactory? httpClientFactory = null)
        {
            _logger = loggerFactory.CreateLogger<TelemetryFunction>();
            _dbContext = dbContext;
            _httpClientFactory = httpClientFactory;
        }

        public Task<TelemetryRecord?> Run(string message)
        {
            return ProcessInternal(message);
        }

        private async Task<TelemetryRecord?> ProcessInternal(string message)
        {
            // Invalid messages are permanent rejects. Infrastructure failures must
            // propagate so the MQTT subscriber does not acknowledge lost records.
            TelemetryData? data;
            try { data = JsonSerializer.Deserialize<TelemetryData>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { return null; }
            if (data is null || !ValidTelemetry(data)) return null;
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(data.Timestamp!.Value).UtcDateTime;
            var normalized = new
            {
                data.DeviceId, data.GatewayId, data.AssetId, data.SensorType, data.SensorStatus, data.Timestamp,
                data.Temperature, data.Humidity, data.Vibration, data.Power, data.Pressure, data.Rpm,
                AssetSignals = data.AssetSignals is null ? null : new SortedDictionary<string, double>(data.AssetSignals, StringComparer.Ordinal),
            };
            var identityBytes = data.Integration is null || data.Integration.Value.ValueKind == JsonValueKind.Null
                ? JsonSerializer.SerializeToUtf8Bytes(normalized)
                : JsonSerializer.SerializeToUtf8Bytes(new { Legacy = normalized, data.Integration });
            var ingestionId = Convert.ToHexString(SHA256.HashData(identityBytes)).ToLowerInvariant();
            var record = await _dbContext.TelemetryRecords.FirstOrDefaultAsync(item => item.IngestionId == ingestionId);
            if (record is null)
            {
                record = new TelemetryRecord
                {
                    IntegrationJson = data.Integration?.GetRawText(),
                    DeviceId = data.DeviceId, GatewayId = data.GatewayId, AssetId = data.AssetId,
                    SensorType = data.SensorType, SensorStatus = data.SensorStatus,
                    AssetSignalsJson = data.AssetSignals is null ? null : JsonSerializer.Serialize(data.AssetSignals),
                    IngestionId = ingestionId, Temperature = data.Temperature, Humidity = data.Humidity,
                    Vibration = data.Vibration, Power = data.Power, Pressure = data.Pressure, Rpm = data.Rpm, Timestamp = timestamp,
                };
                _dbContext.TelemetryRecords.Add(record);
                try { await _dbContext.SaveChangesAsync(); }
                catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" })
                {
                    _dbContext.Entry(record).State = EntityState.Detached;
                    record = await _dbContext.TelemetryRecords.SingleAsync(item => item.IngestionId == ingestionId);
                }
            }
            // Once saved, acknowledgement is safe: incomplete deliveries remain
            // durable and the outbox worker retries them after outages/restarts.
            // Delivery runs independently: a slow sink must not delay MQTT
            // acknowledgement or receipt of the next durable sample.
            return record;
        }

        public static bool ValidTelemetry(TelemetryData data)
        {
            if (data.Integration is JsonElement integration && integration.ValueKind != JsonValueKind.Null)
            {
                if (integration.ValueKind != JsonValueKind.Object || integration.GetRawText().Length > 8192 ||
                    !integration.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("live" or "simulated" or "replay") ||
                    !integration.TryGetProperty("gatewayReceivedAt", out var received) || received.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(received.GetString(), out _) ||
                    (mode.GetString() == "simulated" && !integration.TryGetProperty("scenarioRunId", out _)) ||
                    (mode.GetString() == "replay" && !integration.TryGetProperty("replayRunId", out _))) return false;
            }
            const double MaxMetricMagnitude = 1_000_000_000;
            // Arduino millis() and several industrial counters are unsigned
            // 32-bit values. Keep named signals bounded without rejecting a
            // healthy arm after roughly 11.6 days of uptime.
            const double MaxNamedSignalMagnitude = uint.MaxValue;
            static bool SafeId(string value) => Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
            if (!SafeId(data.DeviceId) || (data.GatewayId is not null && !SafeId(data.GatewayId)) ||
                (data.AssetId is not null && (data.AssetId.Length is 0 or > 128 || data.AssetId.Any(char.IsControl))) ||
                data.Timestamp is null or <= 0 || data.Timestamp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300_000) return false;
            if (data.SensorType is not null && (data.SensorType.Length is 0 or > 64 || data.SensorType.Any(char.IsControl))) return false;
            if (data.SensorStatus is not null && data.SensorStatus is not ("ok" or "read_error")) return false;
            if (data.SensorType == "DHT11" && (data.SensorStatus is null ||
                (data.SensorStatus == "ok" && (!data.Temperature.HasValue || !data.Humidity.HasValue)) ||
                (data.SensorStatus == "read_error" && (data.Temperature.HasValue || data.Humidity.HasValue)))) return false;
            if (data.Humidity.HasValue && (!double.IsFinite(data.Humidity.Value) || data.Humidity.Value is < 0 or > 100)) return false;
            foreach (var value in new[] { data.Temperature, data.Vibration, data.Power, data.Pressure, data.Rpm })
                if (value.HasValue && (!double.IsFinite(value.Value) || Math.Abs(value.Value) > MaxMetricMagnitude)) return false;
            if (data.AssetSignals is not null && (data.AssetSignals.Count > 32 || data.AssetSignals.Any(signal =>
                !Regex.IsMatch(signal.Key, "^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant) ||
                !double.IsFinite(signal.Value) || Math.Abs(signal.Value) > MaxNamedSignalMagnitude))) return false;
            return true;
        }

        public async Task DrainPendingAsync(CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var bridgeEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL"));
            if (!bridgeEnabled) return;
            var records = await _dbContext.TelemetryRecords.Where(record => record.IngestionId != null &&
                (record.NextDeliveryAttemptAt == null || record.NextDeliveryAttemptAt <= now) &&
                record.DashboardForwardedAt == null)
                .OrderBy(record => record.NextDeliveryAttemptAt).ThenBy(record => record.Id).Take(100).ToListAsync(cancellationToken);
            foreach (var record in records) await DeliverAsync(record, cancellationToken);
        }

        private async Task DeliverAsync(TelemetryRecord record, CancellationToken cancellationToken = default)
        {
            await using var transaction = _dbContext.Database.IsRelational()
                ? await _dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
            if (transaction is not null)
            {
                await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({record.IngestionId}))", cancellationToken);
                await _dbContext.Entry(record).ReloadAsync(cancellationToken);
            }
            var failed = false;
            var sinkUrl = Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");
            var token = Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
            if (!string.IsNullOrWhiteSpace(sinkUrl) && record.DashboardForwardedAt is null)
            {
                try
                {
                    if (_httpClientFactory is null || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Bridge credentials are missing.");
                    using var request = new HttpRequestMessage(HttpMethod.Post, sinkUrl)
                    {
                        Content = JsonContent.Create(new
                        {
                            integration = record.IntegrationJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(record.IntegrationJson),
                            deviceId = record.DeviceId, gatewayId = record.GatewayId, assetId = record.AssetId,
                            sensorType = record.SensorType, sensorStatus = record.SensorStatus,
                            assetSignals = record.AssetSignalsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(record.AssetSignalsJson),
                            ingestionId = record.IngestionId, timestamp = new DateTimeOffset(record.Timestamp).ToUnixTimeMilliseconds(),
                            temperature = record.Temperature, humidity = record.Humidity, vibration = record.Vibration,
                            power = record.Power, pressure = record.Pressure, rpm = record.Rpm,
                        }),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var response = await _httpClientFactory.CreateClient("dashboard-ingestion").SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    record.DashboardForwardedAt = DateTime.UtcNow;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failed = true;
                    _logger.LogWarning("Dashboard delivery pending for telemetry record {RecordId}.", record.Id);
                }
            }
            record.DeliveryAttempts += 1;
            record.NextDeliveryAttemptAt = failed ? DateTime.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(record.DeliveryAttempts, 8)))) : null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

    }
}
