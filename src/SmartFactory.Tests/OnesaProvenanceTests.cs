using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFactory.Services.TelemetryService;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;
using SmartFactory.Services.TelemetryService.Application.DTOs;

namespace SmartFactory.Tests;
public class OnesaProvenanceTests
{
    [Fact]
    public async Task PersistsSimulationIdentityAndTimestampAcrossRestartAndDuplicates()
    {
        var options = new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
        var data = new TelemetryData { DeviceId = "onesa-gateway", AssetId = "urn:test:pump", Timestamp = timestamp, Temperature = 45,
            Integration = JsonSerializer.SerializeToElement(new { mode = "simulated", scenarioRunId = "repeatable-run", sourceQuality = "good", retained = true, gatewayReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToString("O") }) };
        var message = JsonSerializer.Serialize(data);
        string id;
        using (var db = new TelemetryDbContext(options))
        {
            var first = await new TelemetryFunction(new LoggerFactory(), db).Run(message);
            Assert.NotNull(first); id = first.IngestionId!;
            Assert.Contains("simulated", first.IntegrationJson); Assert.Contains("repeatable-run", first.IntegrationJson);
        }
        using (var db = new TelemetryDbContext(options))
        {
            var again = await new TelemetryFunction(new LoggerFactory(), db).Run(message);
            Assert.Equal(id, again!.IngestionId); Assert.Single(db.TelemetryRecords);
            Assert.Equal(timestamp, new DateTimeOffset(again.Timestamp).ToUnixTimeMilliseconds());
        }
    }
    [Theory]
    [InlineData("{\"mode\":\"simulated\",\"gatewayReceivedAt\":\"2026-10-20T09:00:00Z\"}")]
    [InlineData("{\"mode\":\"replay\",\"gatewayReceivedAt\":\"2026-10-20T09:00:00Z\"}")]
    [InlineData("{\"mode\":3,\"gatewayReceivedAt\":3}")]
    public void RejectsMissingRunIdentityOrInvalidTypes(string provenance)
    {
        Assert.False(TelemetryFunction.ValidTelemetry(new TelemetryData { DeviceId = "test", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Integration = JsonSerializer.Deserialize<JsonElement>(provenance) }));
    }
}
