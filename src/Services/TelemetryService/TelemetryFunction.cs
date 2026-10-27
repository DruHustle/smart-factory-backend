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
            if (System.Text.Encoding.UTF8.GetByteCount(message)>16*1024) return null;
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
            TelemetryProvenance.TryNormalize(data.Integration,out var provenance,out _);
            static byte[] Identity(object normalized, JsonElement? metadata, Dictionary<string,string>? units, Dictionary<string,TelemetryMapping>? mapping) {
                var legacy=metadata is null || metadata.Value.ValueKind==JsonValueKind.Null ? JsonSerializer.SerializeToElement(normalized) : JsonSerializer.SerializeToElement(new {Legacy=normalized,Integration=metadata});
                if(mapping is {Count:>0})return JsonSerializer.SerializeToUtf8Bytes(new {Legacy=legacy,Units=units is null?null:new SortedDictionary<string,string>(units,StringComparer.Ordinal),Mapping=new SortedDictionary<string,TelemetryMapping>(mapping,StringComparer.Ordinal)});
                return units is {Count:>0} ? JsonSerializer.SerializeToUtf8Bytes(new {Legacy=legacy,Units=new SortedDictionary<string,string>(units,StringComparer.Ordinal)}) : JsonSerializer.SerializeToUtf8Bytes(legacy);
            }
            var originalBytes=Identity(normalized,data.Integration,data.Units,data.Mapping);
            var oldIngestionId=Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
            data.Integration=provenance;
            var identityBytes=Identity(normalized,data.Integration,data.Units,data.Mapping);
            var ingestionId = Convert.ToHexString(SHA256.HashData(identityBytes)).ToLowerInvariant();
            // Preserve an existing pre-policy ingestion identity on a redelivery.
            var record = await _dbContext.TelemetryRecords.FirstOrDefaultAsync(item => item.IngestionId == ingestionId || item.IngestionId == oldIngestionId);
            if (record is null)
            {
                record = new TelemetryRecord
                {
                    IntegrationJson = data.Integration?.GetRawText(),
                    MappingJson = data.Mapping is null ? null : JsonSerializer.Serialize(new SortedDictionary<string,TelemetryMapping>(data.Mapping,StringComparer.Ordinal)),
                    UnitsJson = data.Units is null ? null : JsonSerializer.Serialize(new SortedDictionary<string,string>(data.Units,StringComparer.Ordinal)),
                    TransportRetained = data.TransportRetained,
                    TransportRetainVerified=data.TransportRetainVerified,
                    AcceptedAt = DateTime.UtcNow,
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
            if (!TelemetryProvenance.TryNormalize(data.Integration,out _,out _)) return false;
            var units=new Dictionary<string,string[]>(StringComparer.Ordinal) {
                ["temperature"]=["°C","C","Cel","°F","F","K"], ["humidity"]=["%","fraction"], ["vibration"]=["mm/s","m/s"],
                ["power"]=["W","kW","MW"], ["pressure"]=["Pa","kPa","MPa","bar"], ["rpm"]=["rpm","r/min","1/min","Hz"]
            };
            if(data.Units is {Count:>6} || data.Units is not null && data.Units.Any(u=>!units.TryGetValue(u.Key,out var allowed)||!allowed.Contains(u.Value)))return false;
            if(data.Mapping is {Count:>6})return false;
            if(data.Mapping is not null)foreach(var (metric,mapping) in data.Mapping){
                if(mapping is null||!units.ContainsKey(metric)||data.Units is null||!data.Units.ContainsKey(metric)||mapping.Version!="sf-mapping-v1"||mapping.RawUnit is not ("register" or "native")||!double.IsFinite(mapping.RawValue)||Math.Abs(mapping.RawValue)>9007199254740991||!double.IsFinite(mapping.Scale)||Math.Abs(mapping.Scale)>1e9||!double.IsFinite(mapping.Offset)||Math.Abs(mapping.Offset)>1e9)return false;
                var value=metric switch {"temperature"=>data.Temperature,"humidity"=>data.Humidity,"vibration"=>data.Vibration,"power"=>data.Power,"pressure"=>data.Pressure,"rpm"=>data.Rpm,_=>null};
                if(value is null||Math.Abs(mapping.RawValue*mapping.Scale+mapping.Offset-value.Value)>Math.Max(1e-6,Math.Abs(value.Value)*1e-9))return false;
            }
            const double MaxMetricMagnitude = 1_000_000_000;
            // Arduino millis() and several industrial counters are unsigned
            // 32-bit values. Keep named signals bounded without rejecting a
            // healthy arm after roughly 11.6 days of uptime.
            const double MaxNamedSignalMagnitude = uint.MaxValue;
            static bool SafeId(string value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$", RegexOptions.CultureInvariant);
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

        // 404/409 may resolve after deployment/concurrency recovery. A timeout
        // gives no evidence whether the receiver committed; retry the same ID.
        public static string ClassifyDelivery(int status) => status switch {
            >=200 and <300 => "accepted",
            400 or 413 or 415 or 422 => "payload",
            401 or 403 => "credentials",
            404 or 408 or 409 or 423 or 425 or 428 or 429 => "transient",
            >=500 => "transient",
            _ => "configuration"
        };

        public async Task DrainPendingAsync(CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var bridgeEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL"));
            if (!bridgeEnabled) return;
            var records = await _dbContext.TelemetryRecords.Where(record => record.IngestionId != null &&
                (record.NextDeliveryAttemptAt == null || record.NextDeliveryAttemptAt <= now) &&
                record.DashboardForwardedAt == null && record.DeliveryQuarantinedAt == null)
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
            if (record.DeliveryQuarantinedAt is not null || record.DashboardForwardedAt is not null) return;
            string? quarantine = null;
            var failed = false;
            double? retryAfterSeconds=null;
            if(record.IntegrationJson is not null){
                try{
                    using var metadata=JsonDocument.Parse(record.IntegrationJson);
                    if(!TelemetryProvenance.TryNormalize(metadata.RootElement,out var canonical,out _))quarantine="historical_provenance_validation";
                    else record.IntegrationJson=canonical?.GetRawText();
                }catch(JsonException){quarantine="historical_provenance_validation";}
            }
            var sinkUrl = Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");
            var token = Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
            if (quarantine is null && !string.IsNullOrWhiteSpace(sinkUrl) && record.DashboardForwardedAt is null)
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
                            mapping = record.MappingJson is null ? null : JsonSerializer.Deserialize<Dictionary<string,TelemetryMapping>>(record.MappingJson),
                            units = record.UnitsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string,string>>(record.UnitsJson),
                            transportRetained = record.TransportRetained,
                            transportRetainVerified=record.TransportRetainVerified,
                            sensorType = record.SensorType, sensorStatus = record.SensorStatus,
                            assetSignals = record.AssetSignalsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(record.AssetSignalsJson),
                            ingestionId = record.IngestionId, timestamp = new DateTimeOffset(record.Timestamp).ToUnixTimeMilliseconds(),
                            temperature = record.Temperature, humidity = record.Humidity, vibration = record.Vibration,
                            power = record.Power, pressure = record.Pressure, rpm = record.Rpm,
                        }),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var response = await _httpClientFactory.CreateClient("dashboard-ingestion").SendAsync(request, cancellationToken);
                    var retry=response.Headers.RetryAfter;
                    retryAfterSeconds=retry?.Delta?.TotalSeconds ?? (retry?.Date-DateTimeOffset.UtcNow)?.TotalSeconds;
                    var status=(int)response.StatusCode;
                    var category=ClassifyDelivery(status);
                    if(category=="accepted") {record.DashboardForwardedAt=DateTime.UtcNow;record.DeliveryErrorCode=null;}
                    else if(category=="payload" || category=="credentials" || category=="configuration")quarantine=$"receiver_{category}_{status}";
                    else {failed=true;record.DeliveryErrorCode=$"receiver_transient_{status}";}
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failed = true;
                    record.DeliveryErrorCode="transport_unconfirmed";
                    _logger.LogWarning("Dashboard delivery pending for telemetry record {RecordId}.", record.Id);
                }
            }
            record.DeliveryAttempts += 1;
            if(failed && record.DeliveryAttempts>=12)quarantine="transient_retry_exhausted";
            if(quarantine is not null){
                record.DeliveryQuarantinedAt=DateTime.UtcNow;
                record.DeliveryErrorCode=quarantine;
                failed=false;
                _logger.LogWarning("Telemetry delivery record {RecordId} quarantined ({Code}); not acknowledged by receiver.",record.Id,quarantine);
            }
            record.NextDeliveryAttemptAt = failed ? DateTime.UtcNow.AddSeconds(Math.Clamp(retryAfterSeconds ?? Math.Pow(2, Math.Min(record.DeliveryAttempts, 8)), 1, 300)) : null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

    }
}
