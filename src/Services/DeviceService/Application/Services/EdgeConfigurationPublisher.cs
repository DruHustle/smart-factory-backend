using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace SmartFactory.Services.DeviceService.Application.Services;

public sealed record EdgeConfigurationRequest(int SchemaVersion, string GatewayDeviceId, List<EdgeAssetConfiguration> Assets);
public sealed record EdgeAssetConfiguration(string AssetId, string AssetName, string Protocol, string? Endpoint, List<Dictionary<string, JsonElement>> TagMappings);

/// <summary>Publishes a complete desired-state profile; commands cannot execute arbitrary code.</summary>
public sealed class EdgeConfigurationPublisher(IConfiguration configuration, IHostEnvironment environment)
{
    private static readonly Regex SafeTopicPart = new("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static byte[] BuildConfigurationCommand(EdgeConfigurationRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(new { action = "replace_asset_configuration", configuration = request },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    public async Task PublishAsync(EdgeConfigurationRequest request, CancellationToken ct)
    {
        if (request.SchemaVersion != 1 || !SafeTopicPart.IsMatch(request.GatewayDeviceId) || request.Assets is null || request.Assets.Count > 25)
            throw new ArgumentException("Edge profile version, gateway id, or asset count is invalid.");
        foreach (var asset in request.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.AssetId) || asset.AssetId.Length > 128 || asset.AssetId.Any(char.IsControl) ||
                !new[] { "mqtt", "opcua", "modbus_tcp", "modbus_rtu", "serial", "ada031_v4_serial" }.Contains(asset.Protocol, StringComparer.Ordinal) ||
                (asset.Endpoint?.Length ?? 0) > 512 || (asset.Endpoint is not null && Regex.IsMatch(asset.Endpoint, @"://[^/]*@")) ||
                asset.TagMappings.Count > 100)
                throw new ArgumentException("Edge asset profile contains an invalid identifier, protocol, endpoint, or mapping count.");
        }

        var broker = ResolveBroker();

        var site = configuration["EDGE_SITE_ID"] ?? "factory-a";
        var line = configuration["EDGE_LINE_ID"] ?? "line-1";
        if (!SafeTopicPart.IsMatch(site) || !SafeTopicPart.IsMatch(line)) throw new InvalidOperationException("EDGE_SITE_ID and EDGE_LINE_ID must be safe MQTT topic segments.");
        var topicTemplate = configuration["EDGE_COMMAND_TOPIC_TEMPLATE"] ?? "factory/{site}/{line}/{device}/commands";
        var topic = topicTemplate.Replace("{site}", site, StringComparison.Ordinal)
            .Replace("{line}", line, StringComparison.Ordinal).Replace("{device}", request.GatewayDeviceId, StringComparison.Ordinal);
        if (topic.Contains('{') || topic.Contains('}') || topic.Contains('+') || topic.Contains('#')) throw new InvalidOperationException("MQTT command topic template is invalid.");

        var command = BuildConfigurationCommand(request);
        if (command.Length > 64 * 1024) throw new ArgumentException("Edge configuration exceeds the 64 KB MQTT message limit.");
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var builder = new MqttClientOptionsBuilder().WithTcpServer(broker.Host, broker.Port).WithClientId($"smart-factory-provisioner-{Guid.NewGuid():N}").WithCleanSession().WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrWhiteSpace(broker.Username)) builder.WithCredentials(broker.Username, broker.Password ?? "");
        if (broker.UseTls) builder.WithTlsOptions(options => options.UseTls());
        await client.ConnectAsync(builder.Build(), ct);
        try
        {
            var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(command)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(true).Build();
            var result = await client.PublishAsync(message, ct);
            if (!result.IsSuccess) throw new IOException($"MQTT broker rejected the desired-state publication ({result.ReasonCode}).");
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
        }
    }

    /// <summary>
    /// Build the exact command consumed by the gateway. Schema 2 supports the
    /// programmed profiles and neutral/stop controls while schema 1 remains
    /// available for older jog-only clients during a rolling deployment.
    /// </summary>
    public static byte[] BuildAda031ControlCommand(Ada031ControlRequest request)
    {
        ValidateAda031Control(request);
        if (request.SchemaVersion == 1)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                action = "ada031_control",
                commandId = request.CommandId,
                targetAssetId = request.AssetId,
                joint = request.Joint,
                direction = request.Direction,
                expiresAt = request.ExpiresAt,
            });
        }

        var command = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["action"] = request.Action,
            ["commandId"] = request.CommandId,
            ["targetAssetId"] = request.AssetId,
            ["expiresAt"] = request.ExpiresAt,
        };
        if (request.Action == "jog")
        {
            command["joint"] = request.Joint;
            command["direction"] = request.Direction;
        }
        else if (request.Action == "set_profile")
        {
            command["profile"] = request.Profile;
        }
        return JsonSerializer.SerializeToUtf8Bytes(command);
    }

    /// <summary>Publish one short-lived ADA031 command without MQTT retained state.</summary>
    public async Task PublishAda031ControlAsync(Ada031ControlRequest request, CancellationToken ct)
    {
        ValidateAda031Control(request);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (request.ExpiresAt <= now || request.ExpiresAt > now + 30_000)
            throw new ArgumentException("ADA031 command must expire within 30 seconds.");

        var broker = ResolveBroker();

        var site = configuration["EDGE_SITE_ID"] ?? "factory-a";
        var line = configuration["EDGE_LINE_ID"] ?? "line-1";
        if (!SafeTopicPart.IsMatch(site) || !SafeTopicPart.IsMatch(line)) throw new InvalidOperationException("EDGE_SITE_ID and EDGE_LINE_ID must be safe MQTT topic segments.");
        var topic = $"factory/{site}/{line}/{request.GatewayDeviceId}/commands";
        var command = BuildAda031ControlCommand(request);

        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var builder = new MqttClientOptionsBuilder().WithTcpServer(broker.Host, broker.Port)
            .WithClientId($"smart-factory-control-{Guid.NewGuid():N}").WithCleanSession().WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrWhiteSpace(broker.Username)) builder.WithCredentials(broker.Username, broker.Password ?? "");
        if (broker.UseTls) builder.WithTlsOptions(options => options.UseTls());
        await client.ConnectAsync(builder.Build(), ct);
        try
        {
            var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(command)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build();
            var result = await client.PublishAsync(message, ct);
            if (!result.IsSuccess) throw new IOException($"MQTT broker rejected the ADA031 command ({result.ReasonCode}).");
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
        }
    }

    /// <summary>Publish one short-lived, fixed-pin WROVER indicator command.</summary>
    public async Task PublishGpioControlAsync(GpioControlRequest request, CancellationToken ct)
    {
        if (request.SchemaVersion != 1 || !SafeTopicPart.IsMatch(request.GatewayDeviceId) ||
            !SafeTopicPart.IsMatch(request.TargetDeviceId) ||
            !Regex.IsMatch(request.CommandId, "^[A-Za-z0-9_.-]{8,64}$", RegexOptions.CultureInvariant) ||
            request.Pin != 18 || request.Value is not (0 or 1) || request.HoldMs is < 1 or > 2_000)
            throw new ArgumentException("WROVER indicator command fields are invalid.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (request.ExpiresAt <= now || request.ExpiresAt > now + 30_000)
            throw new ArgumentException("WROVER indicator command must expire within 30 seconds.");

        var broker = ResolveBroker();

        var site = configuration["EDGE_SITE_ID"] ?? "factory-a";
        var line = configuration["EDGE_LINE_ID"] ?? "line-1";
        if (!SafeTopicPart.IsMatch(site) || !SafeTopicPart.IsMatch(line)) throw new InvalidOperationException("EDGE_SITE_ID and EDGE_LINE_ID must be safe MQTT topic segments.");
        var topic = $"factory/{site}/{line}/{request.GatewayDeviceId}/commands";
        var command = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = request.SchemaVersion,
            action = "set_gpio",
            commandId = request.CommandId,
            targetDeviceId = request.TargetDeviceId,
            pin = request.Pin,
            value = request.Value,
            holdMs = request.HoldMs,
            expiresAt = request.ExpiresAt,
        });

        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var builder = new MqttClientOptionsBuilder().WithTcpServer(broker.Host, broker.Port)
            .WithClientId($"smart-factory-indicator-{Guid.NewGuid():N}").WithCleanSession().WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrWhiteSpace(broker.Username)) builder.WithCredentials(broker.Username, broker.Password ?? "");
        if (broker.UseTls) builder.WithTlsOptions(options => options.UseTls());
        await client.ConnectAsync(builder.Build(), ct);
        try
        {
            var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(command)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build();
            var result = await client.PublishAsync(message, ct);
            if (!result.IsSuccess) throw new IOException($"MQTT broker rejected the GPIO command ({result.ReasonCode}).");
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
        }
    }

    private static readonly HashSet<(string Joint, string Direction)> Ada031Commands = new()
    {
        ("base", "increase"), ("base", "decrease"),
        ("shoulder", "increase"), ("shoulder", "decrease"),
        ("elbow", "increase"), ("elbow", "decrease"),
        ("wrist_rotation", "increase"), ("wrist_rotation", "decrease"),
        ("gripper", "increase"), ("gripper", "decrease"),
    };

    private static readonly HashSet<string> Ada031Profiles = new(StringComparer.Ordinal)
    {
        "pick_and_place_repeat", "demonstration_moves",
    };

    private BrokerSettings ResolveBroker()
    {
        static string? FirstConfigured(string? primary, string? secondary) =>
            !string.IsNullOrWhiteSpace(primary) ? primary : !string.IsNullOrWhiteSpace(secondary) ? secondary : null;

        var host = FirstConfigured(configuration["MqttBrokerHost"], configuration["Mqtt:Host"])
            ?? throw new InvalidOperationException("MQTT broker is not configured.");
        var portText = FirstConfigured(configuration["MqttBrokerPort"], configuration["Mqtt:Port"]);
        var port = 1883;
        if (portText is not null && (!int.TryParse(portText, out port) || port is < 1 or > 65_535))
            throw new InvalidOperationException("MQTT broker port must be an integer from 1 to 65535.");
        var username = FirstConfigured(configuration["MqttUsername"], configuration["Mqtt:Username"]);
        var password = FirstConfigured(configuration["MqttPassword"], configuration["Mqtt:Password"]);
        var tlsSetting = FirstConfigured(configuration["MqttUseTls"], configuration["Mqtt:UseTls"]);
        if (tlsSetting is not null && tlsSetting is not ("0" or "1") && !bool.TryParse(tlsSetting, out _))
            throw new InvalidOperationException("MqttUseTls must be true, false, 1, or 0.");
        var useTls = string.Equals(tlsSetting, "true", StringComparison.OrdinalIgnoreCase) || tlsSetting == "1";
        if (environment.IsProduction() && (!useTls || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)))
            throw new InvalidOperationException("Production MQTT requires TLS and a dedicated broker username/password.");
        return new BrokerSettings(host, port, username, password, useTls);
    }

    private static void ValidateAda031Control(Ada031ControlRequest request)
    {
        if (request.SchemaVersion is not (1 or 2) || !SafeTopicPart.IsMatch(request.GatewayDeviceId ?? "") ||
            string.IsNullOrWhiteSpace(request.AssetId) || request.AssetId.Length > 128 || request.AssetId.Any(char.IsControl) ||
            !Regex.IsMatch(request.CommandId ?? "", "^[A-Za-z0-9_.-]{8,64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("ADA031 command identity is invalid.");
        if (request.SchemaVersion == 1 && request.Action is not (null or "jog" or "ada031_control"))
            throw new ArgumentException("ADA031 schema 1 supports jog commands only.");

        var action = request.SchemaVersion == 1 ? "jog" : request.Action;
        var validShape = action switch
        {
            "jog" => request.Profile is null && request.Joint is not null && request.Direction is not null &&
                Ada031Commands.Contains((request.Joint, request.Direction)),
            "set_profile" => request.SchemaVersion == 2 && request.Joint is null && request.Direction is null &&
                request.Profile is not null && Ada031Profiles.Contains(request.Profile),
            "neutral" or "stop_program" => request.SchemaVersion == 2 && request.Joint is null &&
                request.Direction is null && request.Profile is null,
            _ => false,
        };
        if (!validShape) throw new ArgumentException("ADA031 command action or fields are invalid.");
    }

    private sealed record BrokerSettings(string Host, int Port, string? Username, string? Password, bool UseTls);
}

public sealed record Ada031ControlRequest
{
    public int SchemaVersion { get; init; }
    public string GatewayDeviceId { get; init; } = "";
    public string AssetId { get; init; } = "";
    public string? Action { get; init; }
    public string? Joint { get; init; }
    public string? Direction { get; init; }
    public string? Profile { get; init; }
    public string CommandId { get; init; } = "";
    public long ExpiresAt { get; init; }
}
public sealed record GpioControlRequest(int SchemaVersion, string GatewayDeviceId, string TargetDeviceId, int Pin, int Value, int HoldMs, string CommandId, long ExpiresAt);
