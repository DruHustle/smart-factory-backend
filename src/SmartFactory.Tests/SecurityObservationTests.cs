using System.Text.Json;
using SmartFactory.BuildingBlocks.DashboardAccess;
using Xunit;
namespace SmartFactory.Tests;
public class SecurityObservationTests {
 [Theory][InlineData(401,"auth.authentication_failed")][InlineData(403,"auth.authorization_denied")]
 public void RealRejectionsHaveBoundedTypedEvidence(int status,string type){
  var id=Guid.NewGuid();using var doc=JsonDocument.Parse(SecurityObservation.Envelope("dotnet-identity",status,id,new DateTimeOffset(2026,10,26,9,0,0,TimeSpan.FromHours(2))));
  var e=doc.RootElement;Assert.Equal(type,e.GetProperty("eventType").GetString());Assert.Equal("2026-10-26T07:00:00.000Z",e.GetProperty("occurredAt").GetString());Assert.Equal(id.ToString(),e.GetProperty("sourceEventId").GetString());Assert.Equal("live",e.GetProperty("mode").GetString());Assert.False(e.TryGetProperty("userId",out _));Assert.Equal(JsonValueKind.Null,e.GetProperty("assetRef").ValueKind);
 }
 [Theory][InlineData("simulated","scenarioRunId")][InlineData("replay","replayRunId")]
 public void GeneratedServiceTestsKeepExplicitRunIdentity(string mode,string key){using var d=JsonDocument.Parse(SecurityObservation.Envelope("fixture",401,Guid.NewGuid(),DateTimeOffset.UtcNow,mode,"fixture-run"));Assert.Equal(mode,d.RootElement.GetProperty("mode").GetString());Assert.Equal("fixture-run",d.RootElement.GetProperty(key).GetString());}
 [Fact]public void SuccessfulOrUnavailableRequestsAreNotInventedAuthenticationFailures(){Assert.Null(SecurityObservation.RejectionType(200));Assert.Null(SecurityObservation.RejectionType(500));Assert.Throws<ArgumentException>(()=>SecurityObservation.Envelope("fixture",200,Guid.NewGuid(),DateTimeOffset.UtcNow));}
 [Theory][InlineData("")][InlineData(" ")][InlineData("https://private/path?token=secret")]
 public void SourceServiceIdentityCannotContainRequestData(string value)=>Assert.Throws<ArgumentException>(()=>SecurityObservation.Envelope(value,401,Guid.NewGuid(),DateTimeOffset.UtcNow));
 [Fact]public void SimulationCannotLoseRunMetadata()=>Assert.Throws<ArgumentException>(()=>SecurityObservation.Envelope("fixture",401,Guid.NewGuid(),DateTimeOffset.UtcNow,"simulated"));
}
