using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SmartFactory.Services.TelemetryService;
using SmartFactory.Services.TelemetryService.Application.DTOs;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;

namespace SmartFactory.Tests;

[CollectionDefinition("Telemetry delivery", DisableParallelization = true)]
public sealed class TelemetryDeliveryCollection { }

[Collection("Telemetry delivery")]
public sealed class TelemetryDeliveryTests
{
    private static DbContextOptions<TelemetryDbContext> Options() => new DbContextOptionsBuilder<TelemetryDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    private static string Message() => JsonSerializer.Serialize(new TelemetryData
    {
        DeviceId = "pi-test-01", GatewayId = "pi-test-01", AssetId = "urn:test:motor",
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Temperature = 42.5,
        AssetSignals = new() { ["bearingTemp"] = 74.5, ["joint1"] = 15 },
    });

    [Fact]
    public async Task RetryAfterRestartPreservesSignalsAndIngestionIdentity()
    {
        var sink = Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");
        var token = Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL", "https://dashboard.test/api/internal/telemetry");
            Environment.SetEnvironmentVariable("INGESTION_API_TOKEN", "test-only-service-token");
            var options = Options();
            var handler = new DeliveryHandler();
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(item => item.CreateClient("dashboard-ingestion")).Returns(new HttpClient(handler));
            using (var db = new TelemetryDbContext(options))
            {
                var function = new TelemetryFunction(new LoggerFactory(), db, factory.Object);
                Assert.NotNull(await function.Run(Message()));
                Assert.Empty(handler.Payloads);
                Assert.Equal(1, await function.DrainPendingAsync(default));
                var saved = Assert.Single(db.TelemetryRecords);
                Assert.Null(saved.DashboardForwardedAt);
                Assert.NotNull(saved.NextDeliveryAttemptAt);
                Assert.Equal(0, await function.DrainPendingAsync(default));
                saved.NextDeliveryAttemptAt = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
            handler.Status = HttpStatusCode.Accepted;
            using (var db = new TelemetryDbContext(options))
            {
                await new TelemetryFunction(new LoggerFactory(), db, factory.Object).DrainPendingAsync(default);
                Assert.NotNull(Assert.Single(db.TelemetryRecords).DashboardForwardedAt);
            }
            Assert.Equal(2, handler.Payloads.Count);
            Assert.Equal(handler.Payloads[0].GetProperty("ingestionId").GetString(), handler.Payloads[1].GetProperty("ingestionId").GetString());
            Assert.Equal(74.5, handler.Payloads[1].GetProperty("assetSignals").GetProperty("bearingTemp").GetDouble());
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL", sink);
            Environment.SetEnvironmentVariable("INGESTION_API_TOKEN", token);
        }
    }

    [Fact]
    public async Task DuplicateMqttDeliveryDoesNotInsertAnotherRecord()
    {
        using var db = new TelemetryDbContext(Options());
        var function = new TelemetryFunction(new LoggerFactory(), db);
        var message = Message();
        Assert.NotNull(await function.Run(message));
        Assert.NotNull(await function.Run(message));
        Assert.Single(db.TelemetryRecords);
    }

    [Fact]
    public async Task UnknownJsonFieldsDoNotChangePersistedIdentityOrData()
    {
        using var db = new TelemetryDbContext(Options());
        var function = new TelemetryFunction(new LoggerFactory(), db);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var baseline = JsonSerializer.Serialize(new
        {
            deviceId = "sensor-01", gatewayId = "pi-edge-01", temperature = 27.2, humidity = 34, timestamp,
        });
        var withUnknownFields = JsonSerializer.Serialize(new
        {
            deviceId = "sensor-01", gatewayId = "pi-edge-01", temperature = 27.2, humidity = 34, timestamp,
            firmwareRevision = "untrusted", untrusted = new { temperature = 999 },
        });

        var first = await function.Run(baseline);
        var duplicate = await function.Run(withUnknownFields);

        Assert.NotNull(first);
        Assert.Same(first, duplicate);
        var persisted = Assert.Single(db.TelemetryRecords);
        Assert.Equal(27.2, persisted.Temperature);
        Assert.Equal(34, persisted.Humidity);
        Assert.Equal(first!.IngestionId, persisted.IngestionId);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidHumidityIsRejected(double humidity)
    {
        Assert.False(TelemetryFunction.ValidTelemetry(new TelemetryData
        {
            DeviceId = "sensor-01",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Humidity = humidity,
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(4_294_967_295d)]
    public void ValidHumidityAndFullUint32NamedSignalsAreAccepted(double value)
    {
        var data = new TelemetryData
        {
            DeviceId = "ada031-arm-01",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Humidity = value <= 100 ? value : null,
            AssetSignals = new() { ["uptime_ms"] = value },
        };

        Assert.True(TelemetryFunction.ValidTelemetry(data));
    }

    [Fact]
    public void NamedSignalBeyondUint32RangeIsRejected()
    {
        Assert.False(TelemetryFunction.ValidTelemetry(new TelemetryData
        {
            DeviceId = "ada031-arm-01",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AssetSignals = new() { ["uptime_ms"] = 4_294_967_296d },
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("DHT11\nspoofed")]
    [InlineData("sensor-type-name-that-is-deliberately-longer-than-sixty-four-characters-123456")]
    public void InvalidSensorTypeIsRejected(string value)
    {
        Assert.False(TelemetryFunction.ValidTelemetry(new TelemetryData
        {
            DeviceId = "sensor-01", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), SensorType = value,
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("connected")]
    [InlineData("OK")]
    public void InvalidSensorStatusIsRejected(string value)
    {
        Assert.False(TelemetryFunction.ValidTelemetry(new TelemetryData
        {
            DeviceId = "sensor-01", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), SensorStatus = value,
        }));
    }

    [Fact]
    public async Task Dht11ReadErrorPersistsHealthWithoutInventedMeasurements()
    {
        using var db = new TelemetryDbContext(Options());
        var payload = JsonSerializer.Serialize(new
        {
            deviceId = "esp32-wrover-01", gatewayId = "pi-edge-01", sensorType = "DHT11",
            sensorStatus = "read_error", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        Assert.NotNull(await new TelemetryFunction(new LoggerFactory(), db).Run(payload));
        var persisted = Assert.Single(db.TelemetryRecords);
        Assert.Equal("DHT11", persisted.SensorType);
        Assert.Equal("read_error", persisted.SensorStatus);
        Assert.Null(persisted.Temperature);
        Assert.Null(persisted.Humidity);
    }

    [Theory]
    [InlineData(null, 20d, 50d)]
    [InlineData("ok", null, 50d)]
    [InlineData("ok", 20d, null)]
    [InlineData("read_error", 20d, null)]
    [InlineData("read_error", null, 50d)]
    public void InconsistentDht11HealthAndMeasurementsAreRejected(string? status, double? temperature, double? humidity)
    {
        Assert.False(TelemetryFunction.ValidTelemetry(new TelemetryData
        {
            DeviceId = "esp32-wrover-01", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SensorType = "DHT11", SensorStatus = status, Temperature = temperature, Humidity = humidity,
        }));
    }

    [Fact]
    public async Task Dht11ReadingKeepsSensorAndGatewayAttributionThroughDashboardBridge()
    {
        var sink = Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");
        var token = Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL", "https://dashboard.test/api/internal/telemetry");
            Environment.SetEnvironmentVariable("INGESTION_API_TOKEN", "test-only-service-token");
            using var db = new TelemetryDbContext(Options());
            var handler = new DeliveryHandler { Status = HttpStatusCode.Accepted };
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(item => item.CreateClient("dashboard-ingestion")).Returns(new HttpClient(handler));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var firmwarePayload = JsonSerializer.Serialize(new
            {
                deviceId = "esp32-wrover-01",
                gatewayId = "pi-edge-01",
                sensorType = "DHT11",
                sensorStatus = "ok",
                temperature = 27.2,
                humidity = 34.0,
                timestamp,
                assetSignals = new { buttonPressed = 0, buttonPressCount = 12 },
            });

            var function = new TelemetryFunction(new LoggerFactory(), db, factory.Object);
            Assert.NotNull(await function.Run(firmwarePayload));
            var persisted = Assert.Single(db.TelemetryRecords);
            Assert.Equal("esp32-wrover-01", persisted.DeviceId);
            Assert.Equal("pi-edge-01", persisted.GatewayId);
            Assert.Equal("DHT11", persisted.SensorType);
            Assert.Equal("ok", persisted.SensorStatus);
            Assert.Equal(27.2, persisted.Temperature);
            Assert.Equal(34.0, persisted.Humidity);

            await function.DrainPendingAsync(default);
            var forwarded = Assert.Single(handler.Payloads);
            Assert.Equal("esp32-wrover-01", forwarded.GetProperty("deviceId").GetString());
            Assert.Equal("pi-edge-01", forwarded.GetProperty("gatewayId").GetString());
            Assert.Equal("DHT11", forwarded.GetProperty("sensorType").GetString());
            Assert.Equal("ok", forwarded.GetProperty("sensorStatus").GetString());
            Assert.Equal(27.2, forwarded.GetProperty("temperature").GetDouble());
            Assert.Equal(34.0, forwarded.GetProperty("humidity").GetDouble());
            Assert.Equal(12, forwarded.GetProperty("assetSignals").GetProperty("buttonPressCount").GetDouble());
            Assert.Equal(1, persisted.DeliveryAttempts);

            await function.DrainPendingAsync(default);
            Assert.Single(handler.Payloads);
            Assert.Equal(1, persisted.DeliveryAttempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL", sink);
            Environment.SetEnvironmentVariable("INGESTION_API_TOKEN", token);
        }
    }

    [Theory]
    [InlineData("{\"deviceId\":\"topic/#\",\"timestamp\":1}")]
    [InlineData("{\"deviceId\":\"sensor-01\",\"timestamp\":0}")]
    [InlineData("{\"deviceId\":\"sensor-01\",\"timestamp\":9223372036854775807}")]
    [InlineData("malformed")]
    public async Task InvalidTelemetryIsRejectedWithoutPersistence(string message)
    {
        using var db = new TelemetryDbContext(Options());
        Assert.Null(await new TelemetryFunction(new LoggerFactory(), db).Run(message));
        Assert.Empty(db.TelemetryRecords);
    }

    private sealed class DeliveryHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.ServiceUnavailable;
        public List<JsonElement> Payloads { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Payloads.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
            return new HttpResponseMessage(Status);
        }
    }
}
