using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SmartFactory.Services.TelemetryService;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;
using SmartFactory.Services.TelemetryService.Domain.Entities;
namespace SmartFactory.Tests;
[Collection("Telemetry delivery")]
public class TelemetryValidationDeliveryTests {
 private class Handler:HttpMessageHandler {
  public HttpStatusCode Status=HttpStatusCode.UnprocessableEntity; public int Calls; public bool Timeout;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c){Calls++;if(Timeout)throw new TaskCanceledException("synthetic timeout after possible acceptance");return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent("{\"error\":\"fixture validation failure\"}")});}
 }
 [Theory][InlineData(false,422)][InlineData(false,400)][InlineData(false,401)][InlineData(false,403)][InlineData(false,302)][InlineData(true,422)]
 public async Task PermanentOrHistoricalValidationFailuresNeverBecomeSuccessfulDeliveries(bool historicalInvalid,int status){
  var oldUrl=Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");var oldToken=Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
  try{
   Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL","https://fixture.test/api/internal/telemetry");Environment.SetEnvironmentVariable("INGESTION_API_TOKEN","synthetic-only");
   using var db=new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
   var row=new TelemetryRecord{DeviceId="sensor",IngestionId=new string('a',64),Timestamp=DateTime.UtcNow.AddMinutes(-1),Temperature=45,IntegrationJson=historicalInvalid?"{\"mode\":\"simulated\",\"scenarioRunId\":9,\"gatewayReceivedAt\":\"bad\"}":"{\"mode\":\"simulated\",\"scenarioRunId\":\"run\",\"gatewayReceivedAt\":\"2026-10-20T09:00:00+00:00\"}"};db.TelemetryRecords.Add(row);await db.SaveChangesAsync();
   var h=new Handler{Status=(HttpStatusCode)status};var factory=new Mock<IHttpClientFactory>();factory.Setup(f=>f.CreateClient("dashboard-ingestion")).Returns(new HttpClient(h));var fn=new TelemetryFunction(new LoggerFactory(),db,factory.Object);
   await fn.DrainPendingAsync(default);row.NextDeliveryAttemptAt=DateTime.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();h.Status=HttpStatusCode.OK;
   await fn.DrainPendingAsync(default);
   Assert.Equal(historicalInvalid?0:1,h.Calls);Assert.Null(row.DashboardForwardedAt);Assert.NotNull(row.DeliveryQuarantinedAt);Assert.NotNull(row.DeliveryErrorCode);
  }finally{Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL",oldUrl);Environment.SetEnvironmentVariable("INGESTION_API_TOKEN",oldToken);}
 }
 [Theory][InlineData(0)][InlineData(404)][InlineData(409)][InlineData(408)][InlineData(429)][InlineData(503)]
 public async Task TransientResponsesCanRecoverWithoutChangingSourceIdentity(int status){
  var oldUrl=Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");var oldToken=Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
  try{
   Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL","https://fixture.test/api/internal/telemetry");Environment.SetEnvironmentVariable("INGESTION_API_TOKEN","synthetic-only");
   using var db=new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);var id=new string('a',64);
   var row=new TelemetryRecord{DeviceId="sensor",IngestionId=id,Timestamp=DateTime.UtcNow.AddMinutes(-1),Temperature=45};db.TelemetryRecords.Add(row);await db.SaveChangesAsync();
   var h=new Handler{Status=(HttpStatusCode)status,Timeout=status==0};var factory=new Mock<IHttpClientFactory>();factory.Setup(f=>f.CreateClient("dashboard-ingestion")).Returns(new HttpClient(h));var fn=new TelemetryFunction(new LoggerFactory(),db,factory.Object);
   await fn.DrainPendingAsync(default);Assert.Null(row.DashboardForwardedAt);row.NextDeliveryAttemptAt=DateTime.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();h.Status=HttpStatusCode.OK;h.Timeout=false;await fn.DrainPendingAsync(default);
   Assert.Equal(2,h.Calls);Assert.NotNull(row.DashboardForwardedAt);Assert.Equal(id,row.IngestionId);
  }finally{Environment.SetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL",oldUrl);Environment.SetEnvironmentVariable("INGESTION_API_TOKEN",oldToken);}
 }
}
