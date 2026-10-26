using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFactory.Services.TelemetryService;
using SmartFactory.Services.TelemetryService.Application.DTOs;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;
namespace SmartFactory.Tests;
public class ProvenancePolicyTests {
 public static IEnumerable<object[]> Cases() {
  using var d=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","provenance-v1.json")));
  foreach(var c in d.RootElement.GetProperty("cases").EnumerateArray())yield return new object[]{c.GetProperty("name").GetString()!,c.Clone()};
 }
 [Theory][MemberData(nameof(Cases))]
 public async Task RejectsBeforePersistenceAndNormalizesValidInput(string name,JsonElement c) {
  using var db=new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
  var input=new TelemetryData{DeviceId="sensor",Timestamp=1792486800000,Temperature=45,Integration=c.GetProperty("integration")};
  var valid=c.GetProperty("valid").GetBoolean();
  Assert.Equal(valid,TelemetryFunction.ValidTelemetry(input));
  var record=await new TelemetryFunction(new LoggerFactory(),db).Run(JsonSerializer.Serialize(input));
  Assert.Equal(valid,record is not null);Assert.Equal(valid?1:0,db.TelemetryRecords.Count());
  if(valid && c.GetProperty("integration").ValueKind!=JsonValueKind.Null){
   using var normalized=JsonDocument.Parse(record!.IntegrationJson!);
   Assert.True(JsonEqual(c.GetProperty("normalized"),normalized.RootElement),name+": normalized provenance differs");
  }
 }
 private static bool JsonEqual(JsonElement a,JsonElement b)=>a.ValueKind==b.ValueKind && (a.ValueKind switch {
  JsonValueKind.Object=>a.EnumerateObject().Count()==b.EnumerateObject().Count()&&a.EnumerateObject().All(p=>b.TryGetProperty(p.Name,out var v)&&JsonEqual(p.Value,v)),
  JsonValueKind.Array=>a.GetArrayLength()==b.GetArrayLength()&&a.EnumerateArray().Zip(b.EnumerateArray()).All(v=>JsonEqual(v.First,v.Second)),
  _=>a.ToString()==b.ToString()
 });
}
