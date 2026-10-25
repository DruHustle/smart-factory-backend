using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartFactory.Services.TelemetryService;

/// <summary>Smart Factory ingress policy 1. Independently implemented in this service.</summary>
public static class TelemetryProvenance
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "mode", "scenarioRunId", "replayRunId", "sessionId", "sequence", "sourceQuality", "sourceConnection", "timestampOrigin", "retained", "gatewayReceivedAt", "sourceEvents" };
    private static readonly Regex Identifier = new(@"\A[A-Za-z0-9:_.-]{1,128}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Utc = new(@"\A(?!0000)([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?(?:Z|\+00:00)\z", RegexOptions.CultureInvariant);
    private static readonly Regex Uuid = new(@"\A(?:[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}|00000000-0000-0000-0000-000000000000|ffffffff-ffff-ffff-ffff-ffffffffffff)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private sealed class Invalid(string code) : Exception { public string Code { get; } = code; }

    public static bool TryNormalize(JsonElement? input, out JsonElement? normalized, out string error)
    {
        normalized = null; error = "";
        if (input is null || input.Value.ValueKind == JsonValueKind.Null) return true;
        try
        {
            var p = input.Value;
            if (p.ValueKind != JsonValueKind.Object || p.EnumerateObject().Any(v => !Fields.Contains(v.Name))) throw new Invalid("fields");
            string Str(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new Invalid(name);
            string Enum(string name, string[] choices, string? fallback = null)
            {
                var s = !p.TryGetProperty(name, out _) && fallback is not null ? fallback : Str(p, name);
                return choices.Contains(s, StringComparer.Ordinal) ? s : throw new Invalid(name);
            }
            var mode = Enum("mode", ["live", "simulated", "replay"]);
            var value = new Dictionary<string, object?> { ["mode"] = mode };
            foreach (var key in new[] { "scenarioRunId", "replayRunId", "sessionId" })
                if (p.TryGetProperty(key, out _))
                {
                    var id = Str(p, key); if (!Identifier.IsMatch(id)) throw new Invalid(key); value[key] = id;
                }
            if (mode == "simulated" && !value.ContainsKey("scenarioRunId")) throw new Invalid("scenarioRunId");
            if (mode == "replay" && !value.ContainsKey("replayRunId")) throw new Invalid("replayRunId");
            if (mode != "replay" && value.ContainsKey("replayRunId")) throw new Invalid("replay_mode");
            if (p.TryGetProperty("sequence", out var sequence))
            {
                if (sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetDouble(out var n) || !double.IsFinite(n) || n < 0 || n > 9007199254740991 || Math.Floor(n) != n) throw new Invalid("sequence");
                value["sequence"] = (long)n;
            }
            value["sourceQuality"] = Enum("sourceQuality", ["good", "bad", "uncertain", "unknown"], "unknown");
            value["sourceConnection"] = Enum("sourceConnection", ["online", "offline", "unknown"], "unknown");
            value["timestampOrigin"] = Enum("timestampOrigin", ["device", "gateway"], "device");
            if (p.TryGetProperty("retained", out var retained) && retained.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new Invalid("retained");
            value["retained"] = retained.ValueKind == JsonValueKind.True;
            var stamp = Str(p, "gatewayReceivedAt"); var match = stamp.Length <= 35 ? Utc.Match(stamp) : Match.Empty;
            if (!match.Success) throw new Invalid("gatewayReceivedAt");
            var fields = Enumerable.Range(1, 6).Select(i => int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
            var ms = int.Parse(match.Groups[7].Value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
            DateTime date;
            try { date = new DateTime(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], ms, DateTimeKind.Utc); }
            catch (ArgumentOutOfRangeException) { throw new Invalid("gatewayReceivedAt"); }
            value["gatewayReceivedAt"] = date.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var events = new List<Dictionary<string, object?>>();
            if (p.TryGetProperty("sourceEvents", out var array))
            {
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 20) throw new Invalid("sourceEvents");
                foreach (var e in array.EnumerateArray())
                {
                    var keys = new HashSet<string>(["id", "origin", "code", "state", "componentId"], StringComparer.Ordinal);
                    if (e.ValueKind != JsonValueKind.Object || !keys.SetEquals(e.EnumerateObject().Select(v => v.Name))) throw new Invalid("sourceEvents.fields");
                    var id = Str(e, "id"); var code = Str(e, "code"); var state = Str(e, "state"); var component = Str(e, "componentId");
                    if (!Identifier.IsMatch(id) || !Identifier.IsMatch(code)) throw new Invalid("sourceEvents.identifier");
                    if (Str(e, "origin") != "source" || state is not ("active" or "cleared")) throw new Invalid("sourceEvents.enum");
                    if (!Uuid.IsMatch(component)) throw new Invalid("sourceEvents.componentId");
                    events.Add(new() { ["id"] = id, ["origin"] = "source", ["code"] = code, ["state"] = state, ["componentId"] = component.ToLowerInvariant() });
                }
            }
            value["sourceEvents"] = events;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            if (bytes.Length > 8192) throw new Invalid("bytes");
            normalized = JsonSerializer.Deserialize<JsonElement>(bytes);
            return true;
        }
        catch (Invalid e) { error = "invalid_provenance:" + e.Code; return false; }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or OverflowException or InvalidOperationException) { error = "invalid_provenance:encoding"; return false; }
    }
}
