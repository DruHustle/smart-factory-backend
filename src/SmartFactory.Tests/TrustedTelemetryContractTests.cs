using SmartFactory.Services.TelemetryService;
using SmartFactory.Services.TelemetryService.Application.DTOs;
namespace SmartFactory.Tests;
public class TrustedTelemetryContractTests
{
    [Theory]
    [InlineData("sensor.temp:01",true)] [InlineData("legacy_01",true)] [InlineData("a",true)]
    [InlineData("bad/topic",false)] [InlineData("bad#id",false)] [InlineData(":bad",false)]
    public void PreservesCompatibleIdentifiers(string id,bool valid) => Assert.Equal(valid,TelemetryFunction.ValidTelemetry(new TelemetryData {DeviceId=id,Timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Power=1.5,Units=new(){["power"]="kW"}}));
    [Fact]
    public void RejectsDimensionallyIncompatibleUnitsAndUnknownMetricNames()
    {
        var sample=new TelemetryData {DeviceId="valid:01",Timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Vibration=2,Units=new(){["vibration"]="g"}};
        Assert.False(TelemetryFunction.ValidTelemetry(sample));
        sample.Units=new(){["arbitrary"]="W"};Assert.False(TelemetryFunction.ValidTelemetry(sample));
        sample.Units=new(){["vibration"]="m/s"};Assert.True(TelemetryFunction.ValidTelemetry(sample));
    }
    [Fact]
    public void RequiresBoundedConsistentMappingEvidenceAndRejectsNullValues()
    {
        var sample=new TelemetryData {DeviceId="mapped:01",Timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Power=1.5,Units=new(){["power"]="kW"},Mapping=new(){["power"]=null!}};
        Assert.False(TelemetryFunction.ValidTelemetry(sample));
        sample.Mapping["power"]=new TelemetryMapping{RawValue=15,RawUnit="register",Scale=.1,Offset=0,Version="sf-mapping-v1"};
        Assert.True(TelemetryFunction.ValidTelemetry(sample));
        sample.Power=15;Assert.False(TelemetryFunction.ValidTelemetry(sample));
    }
    [Theory]
    [InlineData("factory/site:01/line.01/sensor.temp:01/telemetry",true)]
    [InlineData("factory/a/b/c/heartbeat",true)]
    [InlineData("factory/site:01/line%2E01/sensor%2Etemp:01/telemetry",true)]
    [InlineData("factory/a/b/sensor%2Fbad/telemetry",false)]
    [InlineData("factory/a/b/sensor%252Ebad/telemetry",false)]
    [InlineData("factory/bad#site/b/c/telemetry",false)]
    [InlineData("factory/a/b/c/commands",false)]
    public void ValidatesEveryTopicIdentity(string topic,bool valid) => Assert.Equal(valid,SmartFactory.Services.TelemetryService.Infrastructure.Services.MqttTelemetrySubscriberService.CompatibleTopic(topic.Split('/')));
}
