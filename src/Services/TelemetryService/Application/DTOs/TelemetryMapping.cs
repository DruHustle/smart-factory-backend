using System.Text.Json.Serialization;
namespace SmartFactory.Services.TelemetryService.Application.DTOs;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TelemetryMapping
{
    [JsonRequired, JsonPropertyName("rawValue")] public double RawValue { get; set; }
    [JsonRequired, JsonPropertyName("rawUnit")] public string RawUnit { get; set; } = "";
    [JsonRequired, JsonPropertyName("scale")] public double Scale { get; set; }
    [JsonRequired, JsonPropertyName("offset")] public double Offset { get; set; }
    [JsonRequired, JsonPropertyName("version")] public string Version { get; set; } = "";
}
