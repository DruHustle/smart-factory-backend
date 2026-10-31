using System.Text.Json;
using System.Text.Encodings.Web;
using SmartFactory.Contracts;
using SmartFactory.Services.TelemetryService.Application.DTOs;

namespace SmartFactory.Services.TelemetryService;

/// <summary>Validates the additive connectivity envelope without changing legacy provenance.</summary>
public static class ConnectivityTelemetry
{
    private static readonly JsonSerializerOptions LegacyTransportOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions ConnectivityTransportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Retain UTF-8 wire budgets when adding trusted transport flags; legacy serialization is unchanged.</summary>
    public static string SerializeForPersistence(TelemetryData data) => JsonSerializer.Serialize(data,
        data.Connectivity is { ValueKind: not JsonValueKind.Null } ? ConnectivityTransportOptions : LegacyTransportOptions);

    public static bool TryNormalize(JsonElement? input, out JsonElement? normalized, out string error)
    {
        normalized = null;
        error = "";
        if (input is null || input.Value.ValueKind == JsonValueKind.Null) return true;
        try
        {
            normalized = IndustrialConnectivityContract.ValidateObservation(input.Value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            error = "invalid_connectivity_observation";
            return false;
        }
    }
}
