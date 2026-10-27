using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using SmartFactory.Contracts;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Formatter;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartFactory.Services.TelemetryService.Infrastructure.Services
{
    public class MqttTelemetrySubscriberService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<MqttTelemetrySubscriberService> _logger;
        private readonly IMqttClient _mqttClient;
        private readonly string _host;
        private readonly int _port;
        private readonly string _topic;
        private readonly string _heartbeatTopic;
        private readonly string? _username;
        private readonly string? _password;
        private readonly bool _useTls;
        private readonly string _clientId;
        private readonly bool _cleanSession;
        private int _subscriptionReady;

        public bool IsSubscriptionReady => _mqttClient.IsConnected && Volatile.Read(ref _subscriptionReady) == 1;

        public MqttTelemetrySubscriberService(IServiceProvider serviceProvider, IConfiguration configuration, IHostEnvironment environment, ILogger<MqttTelemetrySubscriberService> logger)
        {
            static string? FirstConfigured(string? primary, string? secondary) =>
                !string.IsNullOrWhiteSpace(primary) ? primary : !string.IsNullOrWhiteSpace(secondary) ? secondary : null;

            _serviceProvider = serviceProvider;
            _logger = logger;
            _mqttClient = new MqttFactory().CreateMqttClient();
            _host = FirstConfigured(configuration["Mqtt:Host"], Environment.GetEnvironmentVariable("MqttBrokerHost")) ?? "localhost";
            var portValue = FirstConfigured(configuration["Mqtt:Port"], Environment.GetEnvironmentVariable("MqttBrokerPort"));
            _port = 1883;
            if (portValue is not null && (!int.TryParse(portValue, out _port) || _port is < 1 or > 65_535))
                throw new InvalidOperationException("MQTT broker port must be an integer from 1 to 65535.");
            _topic = FirstConfigured(configuration["Mqtt:Topic"], Environment.GetEnvironmentVariable("MqttTopic")) ?? "factory/+/+/+/telemetry";
            _heartbeatTopic = FirstConfigured(configuration["Mqtt:HeartbeatTopic"], Environment.GetEnvironmentVariable("MqttHeartbeatTopic")) ?? "factory/+/+/+/heartbeat";
            _username = FirstConfigured(configuration["Mqtt:Username"], Environment.GetEnvironmentVariable("MqttUsername"));
            _password = FirstConfigured(configuration["Mqtt:Password"], Environment.GetEnvironmentVariable("MqttPassword"));
            var tlsValue = FirstConfigured(configuration["Mqtt:UseTls"], Environment.GetEnvironmentVariable("MqttUseTls"));
            if (tlsValue is not null && tlsValue is not ("0" or "1") && !bool.TryParse(tlsValue, out _))
                throw new InvalidOperationException("MqttUseTls must be true, false, 1, or 0.");
            _useTls = string.Equals(tlsValue, "true", StringComparison.OrdinalIgnoreCase) || tlsValue == "1";
            // A fixed ClientId plus persistent session lets the broker resume the
            // QoS 1 subscription after restart. Keep this consumer at one replica.
            _clientId = FirstConfigured(configuration["Mqtt:ClientId"], Environment.GetEnvironmentVariable("MqttClientId")) ?? "";
            _cleanSession = false;
            if (environment.IsProduction())
            {
                if (!_useTls || string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
                    throw new InvalidOperationException("Production MQTT requires TLS and a dedicated broker username/password.");
                if (string.IsNullOrWhiteSpace(_clientId))
                    throw new InvalidOperationException("Production MQTT requires a stable MqttClientId for its persistent broker session.");
            }
            else if (string.IsNullOrWhiteSpace(_clientId))
            {
                _clientId = $"smart-factory-telemetry-dev-{Environment.MachineName}";
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _mqttClient.DisconnectedAsync += _ =>
            {
                Interlocked.Exchange(ref _subscriptionReady, 0);
                return Task.CompletedTask;
            };

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                e.AutoAcknowledge = false;
                var payload = e.ApplicationMessage?.PayloadSegment.Array;
                if (payload == null || e.ApplicationMessage!.PayloadSegment.Count > 16 * 1024)
                {
                    _logger.LogWarning("Rejected empty or oversized MQTT telemetry.");
                    await e.AcknowledgeAsync(stoppingToken);
                    return;
                }
                var message = System.Text.Encoding.UTF8.GetString(payload, e.ApplicationMessage!.PayloadSegment.Offset, e.ApplicationMessage.PayloadSegment.Count);
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var parts = e.ApplicationMessage.Topic.Split('/');
                    if(!CompatibleTopic(parts)) { _logger.LogWarning("Rejected MQTT topic outside the compatible identity contract.");await e.AcknowledgeAsync(stoppingToken);return; }
                    if (parts.Length == 5 && parts[0] == "factory" && parts[4] == "heartbeat")
                    {
                        await ForwardHeartbeatAsync(scope.ServiceProvider, TopicIdentity.Decode(parts[3]), message, e.ApplicationMessage.Retain, IsSubscriptionReady, stoppingToken);
                        await e.AcknowledgeAsync(stoppingToken);
                        return;
                    }
                    var data = System.Text.Json.JsonSerializer.Deserialize<SmartFactory.Services.TelemetryService.Application.DTOs.TelemetryData>(message,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (e.ApplicationMessage.PayloadSegment.Count > 16 * 1024 || parts.Length != 5 || parts[0] != "factory" ||
                        parts[4] != "telemetry" || data is null || TopicIdentity.Decode(parts[3]) != data.DeviceId)
                    {
                        _logger.LogWarning("Rejected telemetry whose identity does not match its MQTT topic.");
                        await e.AcknowledgeAsync(stoppingToken);
                        return;
                    }
                    // Broker delivery metadata cannot be cleared by a publisher, including legacy payloads.
                    data.TransportRetained=data.TransportRetained || e.ApplicationMessage.Retain;
                    // Deliveries queued under an earlier subscription may have lost
                    // their original retain flag. Store them without asserting current state.
                    data.TransportRetainVerified=IsSubscriptionReady &&
                        (!string.IsNullOrEmpty(data.GatewayId)&&data.GatewayId!=data.DeviceId
                            ? data.TransportRetainVerified==true : data.TransportRetainVerified!=false);
                    message=JsonSerializer.Serialize(data,new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
                    var persisted = await scope.ServiceProvider.GetRequiredService<TelemetryFunction>().Run(message);
                    if (persisted is null)
                        _logger.LogWarning("Rejected invalid telemetry for device {DeviceId}.", data.DeviceId);
                    await e.AcknowledgeAsync(stoppingToken);
                }
                catch (System.Text.Json.JsonException)
                {
                    _logger.LogWarning("Rejected malformed MQTT telemetry JSON.");
                    await e.AcknowledgeAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Telemetry persistence failed; disconnecting without acknowledging the MQTT message.");
                    // MQTTnet receive callbacks must finish before disconnect waits
                    // for the receive loop; schedule it outside this callback.
                    _ = Task.Run(async () =>
                    {
                        try { if (_mqttClient.IsConnected) await _mqttClient.DisconnectAsync(); }
                        catch (Exception disconnectError) { _logger.LogDebug(disconnectError, "Disconnect after persistence failure."); }
                    });
                }
            };

            var builder = new MqttClientOptionsBuilder().WithTcpServer(_host, _port).WithClientId(_clientId).WithCleanSession(_cleanSession).WithProtocolVersion(MqttProtocolVersion.V500).WithSessionExpiryInterval(uint.MaxValue);
            if (!string.IsNullOrWhiteSpace(_username)) builder.WithCredentials(_username, _password);
            if (_useTls) builder.WithTlsOptions(options => options.UseTls());
            var options = builder.Build();
            var retryDelay = TimeSpan.FromSeconds(1);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_mqttClient.IsConnected)
                    {
                        await _mqttClient.ConnectAsync(options, stoppingToken);
                        var subscription = await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(_topic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainAsPublished(true).Build(), stoppingToken);
                        if (subscription.Items.Count == 0 || subscription.Items.Any(item =>
                                !item.ResultCode.ToString().StartsWith("GrantedQoS", StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("MQTT broker rejected the telemetry topic subscription.");
                        var heartbeatSubscription = await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(_heartbeatTopic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainAsPublished(true).Build(), stoppingToken);
                        if (heartbeatSubscription.Items.Count == 0 || heartbeatSubscription.Items.Any(item =>
                                !item.ResultCode.ToString().StartsWith("GrantedQoS", StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("MQTT broker rejected the heartbeat topic subscription.");
                        Interlocked.Exchange(ref _subscriptionReady, 1);
                        _logger.LogInformation("Connected to MQTT {Host}:{Port} (TLS: {Tls}) and subscribed to {Topic}.", _host, _port, _useTls, _topic);
                        retryDelay = TimeSpan.FromSeconds(1);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MQTT connect/subscribe failed; retrying in {DelaySeconds} seconds.", retryDelay.TotalSeconds);
                    Interlocked.Exchange(ref _subscriptionReady, 0);
                    if (_mqttClient.IsConnected)
                    {
                        try { await _mqttClient.DisconnectAsync(); }
                        catch (Exception disconnectError) { _logger.LogDebug(disconnectError, "MQTT disconnect during retry failed."); }
                    }
                    try { await Task.Delay(retryDelay, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _subscriptionReady, 0);
            if (_mqttClient.IsConnected) await _mqttClient.DisconnectAsync();
            await base.StopAsync(cancellationToken);
        }

        public static bool CompatibleTopic(string[] parts) => parts.Length==5 && parts[0]=="factory" && parts[4] is "telemetry" or "heartbeat" && parts.Skip(1).Take(3).All(p=>TopicIdentity.TryDecode(p,out _)) && System.Text.Encoding.UTF8.GetByteCount(string.Join("/",parts))<=255;

        private async Task ForwardHeartbeatAsync(IServiceProvider services, string topicDeviceId, string message, bool retained, bool retainVerified, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("gatewayId", out _)) return; // Sensor heartbeat; its Pi sends its own.
            if (!root.TryGetProperty("deviceId", out var id) || id.ValueKind != JsonValueKind.String ||
                id.GetString() != topicDeviceId || !Regex.IsMatch(topicDeviceId, "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$", RegexOptions.CultureInvariant) ||
                !root.TryGetProperty("timestamp", out var time) || !time.TryGetInt64(out var timestamp) ||
                timestamp <= 0 || timestamp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300_000 ||
                !root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String ||
                state.GetString() is not ("online" or "offline") || root.EnumerateObject().Count() != 3)
            {
                _logger.LogWarning("Rejected invalid gateway heartbeat on {TopicDeviceId}.", topicDeviceId);
                return;
            }
            var telemetrySink = Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");
            var token = Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
            if (string.IsNullOrWhiteSpace(telemetrySink)) return;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Dashboard heartbeat bridge is not configured.");
            var sink = new Uri(telemetrySink, UriKind.Absolute);
            if (!sink.AbsolutePath.EndsWith("/api/internal/telemetry", StringComparison.Ordinal))
                throw new InvalidOperationException("Telemetry sink must end in /api/internal/telemetry.");
            var heartbeatSink = new UriBuilder(sink) { Path = sink.AbsolutePath[..^"telemetry".Length] + "heartbeat", Query = "", Fragment = "" }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Post, heartbeatSink);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var upstreamVerified=!root.TryGetProperty("transportRetainVerified",out var verified) || verified.ValueKind==JsonValueKind.True;
            if(root.TryGetProperty("gatewayId",out var gateway)&&gateway.ValueKind==JsonValueKind.String&&gateway.GetString()!=topicDeviceId)
                upstreamVerified=root.TryGetProperty("transportRetainVerified",out verified)&&verified.ValueKind==JsonValueKind.True;
            request.Content = JsonContent.Create(new { deviceId = topicDeviceId, timestamp, status = state.GetString(), transportRetained=retained,transportRetainVerified=retainVerified&&upstreamVerified });
            using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("dashboard-ingestion");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Ignored heartbeat for unregistered gateway {DeviceId}.", topicDeviceId);
                return;
            }
            response.EnsureSuccessStatusCode();
        }
    }
}
