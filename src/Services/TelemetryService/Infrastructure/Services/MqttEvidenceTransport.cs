using MQTTnet.Formatter;

namespace SmartFactory.Services.TelemetryService.Infrastructure.Services;

/// <summary>Legacy intake preserves evidence, never invents verified retain flags.</summary>
public sealed class MqttEvidenceTransport
{
    public MqttProtocolVersion Protocol { get; }
    public bool PreservesRetainFlags => Protocol == MqttProtocolVersion.V500;
    private MqttEvidenceTransport(MqttProtocolVersion protocol) => Protocol = protocol;
    public static MqttEvidenceTransport Parse(string? setting) => setting switch
    {
        null or "5" => new(MqttProtocolVersion.V500),
        "3.1.1-historical" => new(MqttProtocolVersion.V311),
        _ => throw new InvalidOperationException("MqttProtocolVersion must be 5 or the explicit 3.1.1-historical evidence mode."),
    };
    public bool Verified(bool subscriptionReady) => subscriptionReady && PreservesRetainFlags;
}
