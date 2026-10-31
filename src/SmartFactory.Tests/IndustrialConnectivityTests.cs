using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using SmartFactory.Services.DeviceService.Application.Services;
using SmartFactory.Services.TelemetryService;
using SmartFactory.Services.TelemetryService.Application.DTOs;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;

namespace SmartFactory.Tests;

[Collection("Telemetry delivery")]
public sealed class IndustrialConnectivityTests
{
    private static JsonObject Fixture(string key) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "industrial-connectivity-v1.json")))![key]!.AsObject();
    private static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
    private static JsonObject Connection(JsonObject configuration, int index = 0) => configuration["connections"]![index]!.AsObject();

    [Fact]
    public void ConfiguresAllDriversAndPreservesIndependentTagTypesAndFxTsnBoundaries()
    {
        var configuration = Fixture("configuration");
        using var command = JsonDocument.Parse(EdgeConfigurationPublisher.BuildConnectivityConfigurationCommand(Element(configuration)));
        Assert.Equal("replace_connectivity_configuration", command.RootElement.GetProperty("action").GetString());
        var result = command.RootElement.GetProperty("configuration");
        Assert.Equal(6, result.GetProperty("connections").GetArrayLength());
        Assert.Equal(7, result.GetProperty("revision").GetInt32());
        Assert.Equal("uint64", result.GetProperty("connections")[1].GetProperty("tags")[0].GetProperty("dataType").GetString());
        Assert.Equal("diagnostics", result.GetProperty("connections")[4].GetProperty("fx").GetProperty("mode").GetString());
        Assert.Equal("eth0", result.GetProperty("connections")[4].GetProperty("tsn").GetProperty("interface").GetString());
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("legacy-version")]
    [InlineData("unsafe-revision")]
    [InlineData("invalid-gateway")]
    [InlineData("reserved-connection")]
    [InlineData("duplicate-connection")]
    [InlineData("too-many-connections")]
    [InlineData("duplicate-tag")]
    [InlineData("too-many-tags")]
    [InlineData("protocol")]
    [InlineData("endpoint-credentials")]
    [InlineData("endpoint-scheme")]
    [InlineData("secret-field")]
    [InlineData("source-mismatch")]
    [InlineData("missing-simulation-identity")]
    [InlineData("invalid-simulation-identity")]
    [InlineData("modbus-width")]
    [InlineData("modbus-span")]
    [InlineData("serial-option")]
    [InlineData("serial-traversal")]
    [InlineData("serial-normalized-traversal")]
    [InlineData("mqtt-wildcard")]
    [InlineData("mqtt-pointer")]
    [InlineData("integer64-scale")]
    [InlineData("asset-property")]
    [InlineData("fx-missing")]
    [InlineData("fx-control")]
    [InlineData("fx-publisher-overflow")]
    [InlineData("fx-unsecured-consent")]
    [InlineData("fx-field-gap")]
    [InlineData("fx-empty-layout")]
    [InlineData("tsn-command")]
    [InlineData("tsn-socket")]
    public void RejectsInvalidOrUncommissionedConfiguration(string scenario)
    {
        var configuration = Fixture("configuration");
        var first = Connection(configuration);
        switch (scenario)
        {
            case "unknown-field": configuration["execute"] = "command"; break;
            case "legacy-version": configuration["schemaVersion"] = 1; break;
            case "unsafe-revision": configuration["revision"] = 9_007_199_254_740_992L; break;
            case "invalid-gateway": configuration["gatewayDeviceId"] = "other/#"; break;
            case "reserved-connection": first["connectionId"] = "gateway-configuration"; break;
            case "duplicate-connection": configuration["connections"]!.AsArray().Add(first.DeepClone()); break;
            case "too-many-connections": for (var i = 0; i < 17; i++) configuration["connections"]!.AsArray().Add(first.DeepClone()); break;
            case "duplicate-tag": first["tags"]!.AsArray().Add(first["tags"]![0]!.DeepClone()); break;
            case "too-many-tags": for (var i = 0; i < 101; i++) first["tags"]!.AsArray().Add(first["tags"]![0]!.DeepClone()); break;
            case "protocol": first["protocol"] = "shell"; break;
            case "endpoint-credentials": first["endpoint"] = "opc.tcp://user:secret@localhost:4840"; break;
            case "endpoint-scheme": first["endpoint"] = "https://localhost:4840"; break;
            case "secret-field": first["password"] = "not-a-wire-field"; break;
            case "source-mismatch": first["tags"]![0]!["source"] = new JsonObject { ["address"] = 5 }; break;
            case "missing-simulation-identity": first["mode"] = "simulated"; break;
            case "invalid-simulation-identity": first["mode"] = "simulated"; first["scenarioRunId"] = "contains spaces"; break;
            case "modbus-width": Connection(configuration, 1)["tags"]![0]!["source"]!["count"] = 1; break;
            case "modbus-span": Connection(configuration, 1)["tags"]![0]!["source"]!["address"] = 65535; break;
            case "serial-option": Connection(configuration, 2)["endpoint"] = "serial:///dev/ttyUSB0?execute=anything"; break;
            case "serial-traversal": Connection(configuration, 2)["endpoint"] = "serial:///dev/../etc/passwd"; break;
            case "serial-normalized-traversal": Connection(configuration, 2)["endpoint"] = "serial:///dev/ports/../ttyUSB0"; break;
            case "mqtt-wildcard": Connection(configuration, 3)["tags"]![0]!["source"]!["topic"] = "#"; break;
            case "mqtt-pointer": Connection(configuration, 3)["tags"]![0]!["source"]!["path"] = "/value~2"; break;
            case "integer64-scale": Connection(configuration, 1)["tags"]![0]!["scale"] = 0.1; break;
            case "asset-property": first["tags"]![0]!.AsObject().Remove("assetId"); break;
            case "fx-missing": Connection(configuration, 4).Remove("fx"); break;
            case "fx-control": Connection(configuration, 4)["fx"]!["establishConnections"] = true; break;
            case "fx-publisher-overflow": Connection(configuration, 5)["fx"]!["publisherId"] = "65536"; break;
            case "fx-unsecured-consent": Connection(configuration, 5)["fx"]!["allowUnsecured"] = false; break;
            case "fx-field-gap": Connection(configuration, 5)["tags"]![0]!["source"]!["fieldIndex"] = 1; break;
            case "fx-empty-layout": Connection(configuration, 5)["tags"] = new JsonArray(); break;
            case "tsn-command": Connection(configuration, 4)["tsn"]!["interface"] = "eth0;reboot"; break;
            case "tsn-socket": Connection(configuration, 4)["tsn"]!["socketPath"] = "/arbitrary/socket"; break;
        }
        Assert.Throws<ArgumentException>(() => EdgeConfigurationPublisher.BuildConnectivityConfigurationCommand(Element(configuration)));
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejectedBeforePublication()
    {
        var json = Fixture("configuration").ToJsonString().Replace("\"revision\":7", "\"revision\":7,\"revision\":8", StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => EdgeConfigurationPublisher.BuildConnectivityConfigurationCommand(parsed.RootElement));
    }

    [Theory]
    [InlineData(64_000, true)]
    [InlineData(64_001, false)]
    public void ConfigurationBudgetLeavesRoomForTheCommandWrapper(int byteCount, bool accepted)
    {
        var configuration = Fixture("configuration");
        var connection = Connection(configuration).DeepClone().AsObject();
        configuration["connections"] = new JsonArray(connection);
        var tags = new JsonArray();
        for (var i = 0; i < 100; i++)
            tags.Add(new JsonObject { ["tagId"] = "tag" + i, ["name"] = "x", ["dataType"] = "float64", ["source"] = new JsonObject { ["nodeId"] = "ns=2;s=" + new string('x', 505) } });
        connection["tags"] = tags;
        var remaining = byteCount - Encoding.UTF8.GetByteCount(configuration.ToJsonString());
        foreach (var tag in tags)
        {
            var padding = Math.Min(119, remaining);
            tag!["name"] = new string('x', 1 + padding);
            remaining -= padding;
        }
        Assert.Equal(0, remaining);
        Assert.Equal(byteCount, Encoding.UTF8.GetByteCount(configuration.ToJsonString()));
        if (accepted) Assert.True(EdgeConfigurationPublisher.BuildConnectivityConfigurationCommand(Element(configuration)).Length <= 65_536);
        else Assert.Throws<ArgumentException>(() => EdgeConfigurationPublisher.BuildConnectivityConfigurationCommand(Element(configuration)));
    }

    [Fact]
    public void UnicodeConfigurationWithinTheUtf8BudgetDoesNotExpandPastTheMqttLimit()
    {
        var configuration = Fixture("configuration");
        var connection = Connection(configuration).DeepClone().AsObject();
        configuration["connections"] = new JsonArray(connection);
        var tags = new JsonArray();
        for (var i = 0; i < 100; i++)
            tags.Add(new JsonObject { ["tagId"] = "tag" + i, ["name"] = new string('測', 16), ["dataType"] = "float64", ["source"] = new JsonObject { ["nodeId"] = "ns=2;s=" + new string('x', 505) } });
        connection["tags"] = tags;
        var wire = JsonSerializer.SerializeToUtf8Bytes(configuration, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.InRange(wire.Length, 60_000, 64_000);
        using var parsed = JsonDocument.Parse(wire);
        var command = EdgeConfigurationPublisher.BuildConnectivityConfigurationCommand(parsed.RootElement);
        Assert.True(command.Length <= 65_536);
        Assert.Contains("測", Encoding.UTF8.GetString(command));
    }

    [Fact]
    public async Task UnicodeTagValuesSurviveTheActualTransportEnrichmentByteBudget()
    {
        var observation = Fixture("observation");
        observation.Remove("fx"); observation.Remove("tsn");
        var tags = new JsonArray();
        for (var i = 0; i < 6; i++)
            tags.Add(new JsonObject { ["tagId"] = "tag" + i, ["dataType"] = "string", ["value"] = new string('測', 700), ["quality"] = "good" });
        observation["tags"] = tags;
        var data = Data(observation);
        data.Connectivity = JsonSerializer.SerializeToElement(observation, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var payload = ConnectivityTelemetry.SerializeForPersistence(data);
        Assert.InRange(Encoding.UTF8.GetByteCount(payload), 12_000, 16_384);
        using var database = new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var record = await new TelemetryFunction(new LoggerFactory(), database).Run(payload);
        Assert.NotNull(record);
        Assert.Contains("測", record.ConnectivityJson);
    }

    [Fact]
    public void ProbeIsBoundedAndCarriesAnExpiringCorrelationIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var probe = new JsonObject
        {
            ["gatewayDeviceId"] = "gateway-01", ["commandId"] = Guid.NewGuid().ToString(),
            ["expiresAt"] = now.ToUnixTimeMilliseconds() + 30_000, ["connection"] = Connection(Fixture("configuration")).DeepClone(),
        };
        using var parsed = JsonDocument.Parse(EdgeConfigurationPublisher.BuildConnectivityProbeCommand(Element(probe), now));
        Assert.Equal("probe_connectivity", parsed.RootElement.GetProperty("action").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("configuration", out _));
        probe["expiresAt"] = now.ToUnixTimeMilliseconds() - 1;
        Assert.Throws<ArgumentException>(() => EdgeConfigurationPublisher.BuildConnectivityProbeCommand(Element(probe), now));
        probe["expiresAt"] = now.ToUnixTimeMilliseconds() + 60_001;
        Assert.Throws<ArgumentException>(() => EdgeConfigurationPublisher.BuildConnectivityProbeCommand(Element(probe), now));
    }

    [Fact]
    public void IndependentDesiredStateCannotReplaceTheLegacyRetainedAssetProfile()
    {
        Assert.Equal("factory/site%2E1/line%2E1/gateway%2E1/commands/connectivity",
            EdgeConfigurationPublisher.BuildConnectivityCommandTopic("site.1", "line.1", "gateway.1", retained: true));
        Assert.Equal("factory/site%2E1/line%2E1/gateway%2E1/commands",
            EdgeConfigurationPublisher.BuildConnectivityCommandTopic("site.1", "line.1", "gateway.1", retained: false));
        Assert.Throws<InvalidOperationException>(() => EdgeConfigurationPublisher.BuildConnectivityCommandTopic("site", "line", "gateway", true, new string('x', 250)));
    }

    [Fact]
    public async Task AbsentOrNullConnectivityPreservesTheExistingLegacyIngestionIdentity()
    {
        using var database = new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var function = new TelemetryFunction(new LoggerFactory(), database);
        // Golden identity calculated from the pre-connectivity canonical record.
        const string identity = "188528f4e868f63087eb445c5657aea3689d706306d1aa64a43149e11843bbce";
        var absent = await function.Run("""{"deviceId":"legacy-sensor","timestamp":1793354400000,"temperature":21.5}""");
        var nullValue = await function.Run("""{"deviceId":"legacy-sensor","timestamp":1793354400000,"temperature":21.5,"connectivity":null}""");
        Assert.Equal(identity, absent!.IngestionId);
        Assert.Equal(identity, nullValue!.IngestionId);
        Assert.Single(database.TelemetryRecords);
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("wrong-version")]
    [InlineData("duplicate-tag")]
    [InlineData("too-many-tags")]
    [InlineData("good-null")]
    [InlineData("wrong-type")]
    [InlineData("unsafe-integer")]
    [InlineData("integer-overflow")]
    [InlineData("integer-negative-zero")]
    [InlineData("invalid-event")]
    [InlineData("invalid-time")]
    [InlineData("non-canonical-time")]
    [InlineData("tsn-determinism")]
    [InlineData("tsn-future")]
    [InlineData("fx-control")]
    [InlineData("udp-model")]
    [InlineData("reserved-ack")]
    public void RejectsInvalidObservationAndUnsupportedAssurances(string scenario)
    {
        var observation = Fixture("observation");
        switch (scenario)
        {
            case "unknown-field": observation["endpointPassword"] = "private"; break;
            case "wrong-version": observation["schemaVersion"] = "unknown"; break;
            case "duplicate-tag": observation["tags"]!.AsArray().Add(observation["tags"]![0]!.DeepClone()); break;
            case "too-many-tags": for (var i = 0; i < 21; i++) observation["tags"]!.AsArray().Add(observation["tags"]![0]!.DeepClone()); break;
            case "good-null": observation["tags"]![0]!["value"] = null; break;
            case "wrong-type": observation["tags"]![1]!["value"] = 1; break;
            case "unsafe-integer": observation["tags"]![2]!["value"] = 1234; break;
            case "integer-overflow": observation["tags"]![2]!["value"] = "18446744073709551616"; break;
            case "integer-negative-zero": observation["tags"]![2]!["value"] = "-0"; break;
            case "invalid-event": observation["eventId"] = "not-an-event"; break;
            case "invalid-time": observation["observedAt"] = "2026-02-30T10:00:00Z"; break;
            case "non-canonical-time": observation["observedAt"] = "2026-10-30T10:00:00+00:00"; break;
            case "tsn-determinism": observation["tsn"]!["determinism"] = "verified"; break;
            case "tsn-future": observation["tsn"]!["observedAt"] = "2026-10-30T10:01:00Z"; break;
            case "fx-control": observation["fx"]!["controlEnabled"] = true; break;
            case "udp-model": observation["fx"]!["mode"] = "udp_uadp"; break;
            case "reserved-ack": observation["connectionId"] = "gateway-configuration"; break;
        }
        Assert.False(ConnectivityTelemetry.TryNormalize(Element(observation), out _, out var error));
        Assert.Equal("invalid_connectivity_observation", error);
    }

    [Fact]
    public void EmptyGatewayConfigurationAcknowledgementIsExplicitAndValid()
    {
        var ack = new JsonObject
        {
            ["schemaVersion"] = "industrial-observation/v1", ["connectionId"] = "gateway-configuration",
            ["configurationRevision"] = 7, ["eventId"] = Guid.NewGuid().ToString(),
            ["observedAt"] = "2026-10-30T10:00:00Z", ["state"] = "waiting", ["tags"] = new JsonArray(),
        };
        Assert.True(ConnectivityTelemetry.TryNormalize(Element(ack), out _, out _));
    }

    [Fact]
    public async Task TypedTagsAndNetworkEvidenceSurviveRestartDeduplicationAndOutboxDelivery()
    {
        var previousUrl = Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");
        var previousToken = Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
        var options = new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var data = Data(Fixture("observation"));
        string ingestionId;
        try
        {
            Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL", "https://fixture.test/api/internal/telemetry");
            Environment.SetEnvironmentVariable("INGESTION_API_TOKEN", "synthetic-test-only");
            using (var database = new TelemetryDbContext(options))
            {
                var first = await new TelemetryFunction(new LoggerFactory(), database).Run(JsonSerializer.Serialize(data));
                Assert.NotNull(first);
                ingestionId = first.IngestionId!;
                Assert.Contains("18446744073709551615", first.ConnectivityJson);
            }
            using (var database = new TelemetryDbContext(options))
            {
                var handler = new RecordingHandler();
                var clients = new Mock<IHttpClientFactory>();
                clients.Setup(factory => factory.CreateClient("dashboard-ingestion")).Returns(new HttpClient(handler));
                var function = new TelemetryFunction(new LoggerFactory(), database, clients.Object);
                var second = await function.Run(JsonSerializer.Serialize(data));
                Assert.Equal(ingestionId, second!.IngestionId);
                Assert.Single(database.TelemetryRecords);
                await function.DrainPendingAsync(default);
                Assert.NotNull(second.DashboardForwardedAt);
                using var delivered = JsonDocument.Parse(handler.Payload!);
                var envelope = delivered.RootElement.GetProperty("connectivity");
                Assert.Equal("industrial-observation/v1", envelope.GetProperty("schemaVersion").GetString());
                Assert.Equal("unverified", envelope.GetProperty("tsn").GetProperty("determinism").GetString());
                Assert.Equal("18446744073709551615", envelope.GetProperty("tags")[2].GetProperty("value").GetString());
                Assert.Equal("simulated", delivered.RootElement.GetProperty("integration").GetProperty("mode").GetString());
                Assert.False(delivered.RootElement.GetProperty("transportRetainVerified").GetBoolean());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL", previousUrl);
            Environment.SetEnvironmentVariable("INGESTION_API_TOKEN", previousToken);
        }
    }

    [Fact]
    public async Task ReorderedEnvelopePropertiesDeduplicateWhileDifferentEventsRemainDistinct()
    {
        using var database = new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var function = new TelemetryFunction(new LoggerFactory(), database);
        var envelope = Fixture("observation");
        var first = await function.Run(JsonSerializer.Serialize(Data(envelope)));
        var reversed = new JsonObject(envelope.Reverse().Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        var second = await function.Run(JsonSerializer.Serialize(Data(reversed)));
        Assert.Equal(first!.IngestionId, second!.IngestionId);
        envelope["eventId"] = Guid.NewGuid().ToString();
        var third = await function.Run(JsonSerializer.Serialize(Data(envelope)));
        Assert.NotEqual(first.IngestionId, third!.IngestionId);
        Assert.Equal(2, database.TelemetryRecords.Count());
    }

    [Fact]
    public async Task InvalidHistoricalConnectivityIsQuarantinedBeforeAnyHttpDelivery()
    {
        var previousUrl = Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");
        var previousToken = Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL", "https://fixture.test/api/internal/telemetry");
            Environment.SetEnvironmentVariable("INGESTION_API_TOKEN", "synthetic-test-only");
            using var database = new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var handler = new RecordingHandler();
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(factory => factory.CreateClient("dashboard-ingestion")).Returns(new HttpClient(handler));
            var function = new TelemetryFunction(new LoggerFactory(), database, clients.Object);
            var record = await function.Run(JsonSerializer.Serialize(Data(Fixture("observation"))));
            record!.ConnectivityJson = "{\"schemaVersion\":\"not-supported\"}";
            await database.SaveChangesAsync();
            await function.DrainPendingAsync(default);
            Assert.Equal("historical_connectivity_validation", record.DeliveryErrorCode);
            Assert.NotNull(record.DeliveryQuarantinedAt);
            Assert.Null(record.DashboardForwardedAt);
            Assert.Null(handler.Payload);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL", previousUrl);
            Environment.SetEnvironmentVariable("INGESTION_API_TOKEN", previousToken);
        }
    }

    [Fact]
    public void ConnectivityRequiresGatewayIdentityAndProvenanceWithoutInventingAssetIdentity()
    {
        var data = Data(Fixture("observation"));
        Assert.True(TelemetryFunction.ValidTelemetry(data));
        data.GatewayId = "different-gateway";
        Assert.False(TelemetryFunction.ValidTelemetry(data));
        data.GatewayId = data.DeviceId;
        data.AssetId = "urn:guessed:asset";
        Assert.False(TelemetryFunction.ValidTelemetry(data));
        data.AssetId = null;
        data.Integration = null;
        Assert.False(TelemetryFunction.ValidTelemetry(data));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task AdditiveMigrationPreservesLegacyRecordsAndPersistsConnectivityInRealPostgres()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DASHBOARD_DATABASE_CONNECTION")
            ?? throw new InvalidOperationException("TEST_DASHBOARD_DATABASE_CONNECTION must name disposable local _e2e storage.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (!settings.Database!.EndsWith("_e2e", StringComparison.Ordinal) || !new[] { "localhost", "127.0.0.1", "host.docker.internal", "database" }.Contains(settings.Host))
            throw new InvalidOperationException("Disposable local _e2e database required.");
        var schema = "connectivity_" + Guid.NewGuid().ToString("N") + "_e2e";
        await using var admin = NpgsqlDataSource.Create(connection);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            settings.SearchPath = schema;
            await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
            var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "telemetry-migrations");
            var migrations = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, "manifest.json")))!;
            Assert.Equal("20261031_industrial_connectivity.sql", migrations[^1]);
            foreach (var migration in migrations[..^1])
            {
                await using var apply = source.CreateCommand(await File.ReadAllTextAsync(Path.Combine(directory, migration)));
                await apply.ExecuteNonQueryAsync();
            }
            await using (var insert = source.CreateCommand("""
                INSERT INTO "TelemetryRecords"("DeviceId","Temperature","Timestamp","IngestionId","DashboardForwardedAt")
                VALUES('legacy-sensor',42.5,'2026-10-30T10:00:00Z',repeat('a',64),now())
                """)) await insert.ExecuteNonQueryAsync();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var apply = source.CreateCommand(await File.ReadAllTextAsync(Path.Combine(directory, migrations[^1])));
                await apply.ExecuteNonQueryAsync();
            }
            var options = new DbContextOptionsBuilder<TelemetryDbContext>().UseNpgsql(settings.ConnectionString).Options;
            string identity;
            using (var database = new TelemetryDbContext(options))
            {
                var legacy = await database.TelemetryRecords.SingleAsync(row => row.DeviceId == "legacy-sensor");
                Assert.Equal(42.5, legacy.Temperature);
                Assert.Equal(new string('a', 64), legacy.IngestionId);
                Assert.Null(legacy.ConnectivityJson);
                var row = await new TelemetryFunction(new LoggerFactory(), database).Run(JsonSerializer.Serialize(Data(Fixture("observation"))));
                Assert.NotNull(row);
                identity = row.IngestionId!;
            }
            using (var database = new TelemetryDbContext(options))
            {
                var row = await new TelemetryFunction(new LoggerFactory(), database).Run(JsonSerializer.Serialize(Data(Fixture("observation"))));
                Assert.Equal(identity, row!.IngestionId);
                Assert.Contains("18446744073709551615", row.ConnectivityJson);
                Assert.Equal(2, await database.TelemetryRecords.CountAsync());
            }
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static TelemetryData Data(JsonObject envelope) => new()
    {
        DeviceId = "gateway-01", GatewayId = "gateway-01", Timestamp = 1_793_354_400_000,
        Connectivity = Element(envelope),
        Integration = JsonSerializer.SerializeToElement(new { mode = "simulated", scenarioRunId = "connectivity-fixture", sourceQuality = "unknown", sourceConnection = "online", timestampOrigin = "gateway", gatewayReceivedAt = "2026-10-30T10:00:00.000Z" }),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
}
