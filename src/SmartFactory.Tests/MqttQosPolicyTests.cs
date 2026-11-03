using MQTTnet.Client;
using MQTTnet.Protocol;
using SmartFactory.Services.TelemetryService.Infrastructure.Services;

namespace SmartFactory.Tests;

public sealed class MqttQosPolicyTests
{
    [Theory]
    [InlineData(null, MqttQualityOfServiceLevel.AtLeastOnce)]
    [InlineData("0", MqttQualityOfServiceLevel.AtMostOnce)]
    [InlineData("1", MqttQualityOfServiceLevel.AtLeastOnce)]
    [InlineData("2", MqttQualityOfServiceLevel.ExactlyOnce)]
    public void ParsesOnlySupportedLevels(string? value, MqttQualityOfServiceLevel expected) =>
        Assert.Equal(expected, MqttTelemetrySubscriberService.ParseUplinkQos(value));

    [Theory]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData(" 1")]
    public void RejectsInvalidConfiguration(string value) =>
        Assert.Throws<InvalidOperationException>(() => MqttTelemetrySubscriberService.ParseUplinkQos(value));

    [Fact]
    public void AcceptsOnlyScopedGatewayAckTopics()
    {
        Assert.True(MqttTelemetrySubscriberService.CompatibleQosAckTopic("factory/site/line/gateway/commands/ack".Split('/')));
        Assert.False(MqttTelemetrySubscriberService.CompatibleQosAckTopic("factory/site/line/gateway/commands".Split('/')));
        Assert.False(MqttTelemetrySubscriberService.CompatibleQosAckTopic("factory/site/line/%2F/commands/ack".Split('/')));
    }

    [Fact]
    public void DoesNotClaimReadinessWhenBrokerGrantsLowerQos()
    {
        Assert.True(MqttTelemetrySubscriberService.IsRequestedGrant(MqttClientSubscribeResultCode.GrantedQoS2, MqttQualityOfServiceLevel.ExactlyOnce));
        Assert.False(MqttTelemetrySubscriberService.IsRequestedGrant(MqttClientSubscribeResultCode.GrantedQoS1, MqttQualityOfServiceLevel.ExactlyOnce));
        Assert.False(MqttTelemetrySubscriberService.IsRequestedGrant(MqttClientSubscribeResultCode.UnspecifiedError, MqttQualityOfServiceLevel.AtLeastOnce));
    }
}
