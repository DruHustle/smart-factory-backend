using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using SmartFactory.Contracts;

namespace SmartFactory.Services.DeviceService.Application.Services;

public sealed partial class EdgeConfigurationPublisher
{
    private static readonly JsonSerializerOptions ConnectivityJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Build a bounded, revisioned read-only profile without altering legacy asset mappings.</summary>
    public static byte[] BuildConnectivityConfigurationCommand(JsonElement request)
    {
        var configuration = IndustrialConnectivityContract.ValidateConfiguration(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { action = "replace_connectivity_configuration", configuration }, ConnectivityJsonOptions);
        if (bytes.Length > 64 * 1024) throw new ArgumentException("Connectivity command exceeds the 64 KB limit.");
        return bytes;
    }

    /// <summary>Probe one supplied connection through its commissioned gateway; never retained.</summary>
    public static byte[] BuildConnectivityProbeCommand(JsonElement request, DateTimeOffset? now = null)
    {
        var probe = IndustrialConnectivityContract.ValidateProbe(request, (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "probe_connectivity",
            gatewayDeviceId = probe.GetProperty("gatewayDeviceId").GetString(),
            commandId = probe.GetProperty("commandId").GetString(),
            expiresAt = decimal.ToInt64(probe.GetProperty("expiresAt").GetDecimal()),
            connection = probe.GetProperty("connection"),
        }, ConnectivityJsonOptions);
        if (bytes.Length > 64 * 1024) throw new ArgumentException("Connectivity probe exceeds the 64 KB limit.");
        return bytes;
    }

    public Task PublishConnectivityAsync(JsonElement request, CancellationToken ct)
    {
        var bytes = BuildConnectivityConfigurationCommand(request);
        return PublishConnectivityCommandAsync(request.GetProperty("gatewayDeviceId").GetString()!, bytes, retained: true, ct);
    }

    public Task ProbeConnectivityAsync(JsonElement request, CancellationToken ct)
    {
        var bytes = BuildConnectivityProbeCommand(request);
        return PublishConnectivityCommandAsync(request.GetProperty("gatewayDeviceId").GetString()!, bytes, retained: false, ct);
    }

    public static string BuildConnectivityCommandTopic(string site, string line, string gatewayDeviceId, bool retained, string? template = null)
    {
        if (!SafeTopicPart.IsMatch(site) || !SafeTopicPart.IsMatch(line) || !SafeTopicPart.IsMatch(gatewayDeviceId))
            throw new ArgumentException("Gateway routing identities are invalid.");
        var topic = (template ?? "factory/{site}/{line}/{device}/commands")
            .Replace("{site}", TopicIdentity.Encode(site), StringComparison.Ordinal)
            .Replace("{line}", TopicIdentity.Encode(line), StringComparison.Ordinal)
            .Replace("{device}", TopicIdentity.Encode(gatewayDeviceId), StringComparison.Ordinal);
        // Each desired-state document needs its own retained topic. The legacy
        // asset profile stays retained on /commands and is never overwritten here.
        if (retained) topic += "/connectivity";
        if (topic.Contains('{') || topic.Contains('}') || topic.Contains('+') || topic.Contains('#') || Encoding.UTF8.GetByteCount(topic) > 255)
            throw new InvalidOperationException("MQTT command topic template is invalid.");
        return topic;
    }

    private async Task PublishConnectivityCommandAsync(string gatewayDeviceId, byte[] command, bool retained, CancellationToken ct)
    {
        var broker = ResolveBroker();
        var site = configuration["EDGE_SITE_ID"] ?? "factory-a";
        var line = configuration["EDGE_LINE_ID"] ?? "line-1";
        var topic = BuildConnectivityCommandTopic(site, line, gatewayDeviceId, retained, configuration["EDGE_COMMAND_TOPIC_TEMPLATE"]);
        using var client = new MqttFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder().WithTcpServer(broker.Host, broker.Port)
            .WithClientId($"smart-factory-connectivity-{Guid.NewGuid():N}").WithCleanSession().WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrWhiteSpace(broker.Username)) options.WithCredentials(broker.Username, broker.Password ?? "");
        if (broker.UseTls) options.WithTlsOptions(tls => tls.UseTls());
        await client.ConnectAsync(options.Build(), ct);
        try
        {
            var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(command)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(retained).Build();
            var result = await client.PublishAsync(message, ct);
            if (!result.IsSuccess) throw new IOException("MQTT broker rejected the connectivity command.");
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
        }
    }
}
