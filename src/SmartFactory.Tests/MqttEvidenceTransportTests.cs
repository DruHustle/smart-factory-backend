using MQTTnet.Formatter;
using SmartFactory.Services.TelemetryService.Infrastructure.Services;

namespace SmartFactory.Tests;

public sealed class MqttEvidenceTransportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("5")]
    public void DefaultAndExplicitFiveRequireAcknowledgedSubscription(string? setting)
    {
        var policy = MqttEvidenceTransport.Parse(setting);
        Assert.Equal(MqttProtocolVersion.V500, policy.Protocol);
        Assert.False(policy.Verified(false));
        Assert.True(policy.Verified(true));
    }
    [Fact]
    public void ExplicitLegacyIntakeNeverVerifiesRetainFlagsEvenAfterSubscription()
    {
        var policy = MqttEvidenceTransport.Parse("3.1.1-historical");
        Assert.Equal(MqttProtocolVersion.V311, policy.Protocol);
        Assert.False(policy.Verified(false));
        Assert.False(policy.Verified(true));
    }
    [Theory]
    [InlineData("3")]
    [InlineData("3.1.1")]
    [InlineData("auto")]
    public void AmbiguousOrAutomaticDowngradeIsRejected(string setting) =>
        Assert.Throws<InvalidOperationException>(() => MqttEvidenceTransport.Parse(setting));
}
