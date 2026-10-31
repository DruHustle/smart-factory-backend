using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartFactory.Contracts;

/// <summary>
/// Independently validates the versioned, read-only industrial connectivity wire contract.
/// Compiled into the provisioning and telemetry services; no shared runtime or new consumer.
/// </summary>
internal static class IndustrialConnectivityContract
{
    internal const string ConfigurationVersion = "industrial-connectivity/v1";
    internal const string ObservationVersion = "industrial-observation/v1";
    private const long SafeInteger = 9_007_199_254_740_991;
    private static readonly Regex Identity = new("\\A[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Reference = new("\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Property = new("\\A[A-Za-z][A-Za-z0-9_.-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Code = new("\\A[A-Za-z0-9_.:-]{1,128}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Interface = new("\\A[A-Za-z0-9][A-Za-z0-9_.:-]{0,14}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Utc = new("\\A(?!0000)([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\\.([0-9]{1,9}))?Z\\z", RegexOptions.CultureInvariant);
    private static readonly Regex UuidIdentity = new("\\A(?:[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}|00000000-0000-0000-0000-000000000000|ffffffff-ffff-ffff-ffff-ffffffffffff)\\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] Types = ["boolean", "int16", "uint16", "int32", "uint32", "int64", "uint64", "float32", "float64", "string"];
    private static readonly string[] Protocols = ["opcua", "modbus_tcp", "modbus_rtu", "mqtt", "opcua_fx"];

    internal static JsonElement ValidateConfiguration(JsonElement configuration)
    {
        // Leave headroom for the action/configuration MQTT command wrapper.
        LimitBytes(configuration, 64_000);
        Shape(configuration, ["schemaVersion", "gatewayDeviceId", "revision", "connections"]);
        Choice(configuration, "schemaVersion", [ConfigurationVersion]);
        Id(configuration, "gatewayDeviceId");
        Integer(configuration, "revision", 1, SafeInteger);
        var connections = Array(configuration, "connections", 16);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections.EnumerateArray())
        {
            ValidateConnection(connection);
            Require(identities.Add(Text(connection, "connectionId", 64)), "Duplicate connection identity.");
        }
        return Canonical(configuration);
    }

    internal static JsonElement ValidateProbe(JsonElement request, long nowMs)
    {
        LimitBytes(request, 64 * 1024);
        Shape(request, ["gatewayDeviceId", "commandId", "expiresAt", "connection"]);
        Id(request, "gatewayDeviceId");
        Uuid(request, "commandId");
        Integer(request, "expiresAt", nowMs + 1, nowMs + 60_000);
        ValidateConnection(request.GetProperty("connection"));
        return Canonical(request);
    }

    internal static void ValidateConnection(JsonElement connection)
    {
        Shape(connection, ["connectionId", "name", "protocol", "endpoint", "enabled", "intervalMs", "tags"],
            ["credentialRef", "mode", "scenarioRunId", "fx", "tsn"]);
        Id(connection, "connectionId");
        Require(Text(connection, "connectionId", 64) != "gateway-configuration", "Connection identity is reserved for gateway acknowledgements.");
        Text(connection, "name", 120);
        var protocol = Choice(connection, "protocol", Protocols);
        Boolean(connection, "enabled");
        Integer(connection, "intervalMs", 100, 60_000);
        OptionalPattern(connection, "credentialRef", Reference, 64);
        var mode = connection.TryGetProperty("mode", out _) ? Choice(connection, "mode", ["live", "simulated"]) : "live";
        if (connection.TryGetProperty("scenarioRunId", out _))
        {
            Pattern(connection, "scenarioRunId", Code, 128);
            Require(mode == "simulated", "Scenario identity requires simulated mode.");
        }
        Require(mode != "simulated" || connection.TryGetProperty("scenarioRunId", out _), "Simulated connections require a scenario identity.");

        var fxMode = "diagnostics";
        if (protocol == "opcua_fx")
        {
            Require(connection.TryGetProperty("fx", out var fx), "OPC UA FX requires an explicit acquisition mode.");
            fxMode = ValidateFxConfiguration(fx);
        }
        else Require(!connection.TryGetProperty("fx", out _), "FX options require the OPC UA FX protocol.");
        ValidateEndpoint(Text(connection, "endpoint", 512), protocol, fxMode);
        if (connection.TryGetProperty("tsn", out var tsn)) ValidateTsnConfiguration(tsn);

        var tags = Array(connection, "tags", 100);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var fieldIndexes = new HashSet<long>();
        foreach (var tag in tags.EnumerateArray())
        {
            Shape(tag, ["tagId", "name", "dataType", "source"], ["unit", "assetId", "property", "scale", "offset"]);
            Id(tag, "tagId");
            Require(identities.Add(Text(tag, "tagId", 64)), "Duplicate tag identity.");
            Text(tag, "name", 120);
            var dataType = Choice(tag, "dataType", Types);
            OptionalText(tag, "unit", 32);
            OptionalText(tag, "assetId", 128);
            OptionalPattern(tag, "property", Property, 64);
            Require(!tag.TryGetProperty("property", out _) || tag.TryGetProperty("assetId", out _), "An asset property requires an asset binding.");
            var scale = OptionalNumber(tag, "scale") ?? 1;
            var offset = OptionalNumber(tag, "offset") ?? 0;
            if (dataType is "boolean" or "string" or "int64" or "uint64")
                Require(scale == 1 && offset == 0, "This data type requires identity scaling.");
            var source = tag.GetProperty("source");
            if (protocol is "opcua" || protocol == "opcua_fx" && fxMode == "diagnostics")
            {
                Shape(source, ["nodeId"], ["namespaceUri"]);
                Text(source, "nodeId", 512);
                OptionalText(source, "namespaceUri", 256);
            }
            else if (protocol is "modbus_tcp" or "modbus_rtu")
            {
                Shape(source, ["address", "registerType", "unitId"], ["count", "byteOrder", "wordOrder"]);
                var address = Integer(source, "address", 0, 65_535);
                Integer(source, "unitId", 1, 247);
                var register = Choice(source, "registerType", ["coil", "discrete_input", "input_register", "holding_register"]);
                var boolean = register is "coil" or "discrete_input";
                Require(boolean ? dataType == "boolean" : dataType != "string", "Modbus register table and data type are incompatible.");
                var expected = dataType switch { "int64" or "uint64" or "float64" => 4, "int32" or "uint32" or "float32" => 2, _ => 1 };
                var count = source.TryGetProperty("count", out _) ? Integer(source, "count", 1, 4) : expected;
                Require(count == expected && address + count <= 65_536, "Modbus register width or address span is invalid.");
                OptionalChoice(source, "byteOrder", ["big", "little"]);
                OptionalChoice(source, "wordOrder", ["big", "little"]);
            }
            else if (protocol == "mqtt")
            {
                Shape(source, ["topic", "path"], ["timestampPath", "qualityPath"]);
                var topic = Text(source, "topic", 256);
                Require(!topic.Contains('+') && !topic.Contains('#') && Encoding.UTF8.GetByteCount(topic) <= 256, "MQTT sources require a bounded literal topic.");
                Pointer(source, "path");
                if (source.TryGetProperty("timestampPath", out _)) Pointer(source, "timestampPath");
                if (source.TryGetProperty("qualityPath", out _)) Pointer(source, "qualityPath");
            }
            else
            {
                Shape(source, ["fieldIndex"]);
                var index = Integer(source, "fieldIndex", 0, 99);
                Require(fieldIndexes.Add(index), "Duplicate UADP field index.");
            }
        }
        if (protocol == "opcua_fx" && fxMode == "udp_uadp")
            Require(fieldIndexes.Count > 0 && Enumerable.Range(0, fieldIndexes.Count).All(index => fieldIndexes.Contains(index)),
                "UADP requires a complete nonempty field layout starting at index zero.");
    }

    internal static JsonElement ValidateObservation(JsonElement observation)
    {
        LimitBytes(observation, 15_000);
        Shape(observation, ["schemaVersion", "connectionId", "configurationRevision", "eventId", "observedAt", "state", "tags"],
            ["errorCode", "commandId", "discoveredTags", "fx", "tsn"]);
        Choice(observation, "schemaVersion", [ObservationVersion]);
        Id(observation, "connectionId");
        Integer(observation, "configurationRevision", 1, SafeInteger);
        Uuid(observation, "eventId");
        Timestamp(observation, "observedAt");
        Choice(observation, "state", ["connected", "degraded", "disconnected", "waiting", "disabled"]);
        OptionalPattern(observation, "errorCode", Code, 128);
        if (observation.TryGetProperty("commandId", out _)) Uuid(observation, "commandId");
        var tags = Array(observation, "tags", 20);
        if (Text(observation, "connectionId", 64) == "gateway-configuration")
            Require(tags.GetArrayLength() == 0 && Text(observation, "state", 32) is "waiting" or "disabled" &&
                !new[] { "errorCode", "commandId", "discoveredTags", "fx", "tsn" }.Any(key => observation.TryGetProperty(key, out _)),
                "Gateway configuration acknowledgement has an invalid shape.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags.EnumerateArray())
        {
            Shape(tag, ["tagId", "dataType", "value", "quality"], ["unit", "nativeStatus", "sourceTimestamp", "serverTimestamp", "qualityBasis"]);
            Id(tag, "tagId");
            Require(identities.Add(Text(tag, "tagId", 64)), "Duplicate observed tag identity.");
            var type = Choice(tag, "dataType", Types);
            ValidateValue(tag.GetProperty("value"), type);
            var quality = Choice(tag, "quality", ["good", "bad", "uncertain", "unknown"]);
            Require(quality != "good" || tag.GetProperty("value").ValueKind != JsonValueKind.Null, "Good quality requires a value.");
            OptionalText(tag, "unit", 32);
            OptionalText(tag, "nativeStatus", 128);
            OptionalChoice(tag, "qualityBasis", ["protocol", "communication", "payload", "unauthenticated_udp"]);
            if (tag.TryGetProperty("sourceTimestamp", out _)) Timestamp(tag, "sourceTimestamp");
            if (tag.TryGetProperty("serverTimestamp", out _)) Timestamp(tag, "serverTimestamp");
        }
        if (observation.TryGetProperty("discoveredTags", out _))
            foreach (var tag in Array(observation, "discoveredTags", 20).EnumerateArray())
            {
                Shape(tag, ["name", "nodeId", "namespaceUri", "dataType"]);
                Text(tag, "name", 120);
                Text(tag, "nodeId", 512);
                Text(tag, "namespaceUri", 256);
                Text(tag, "dataType", 32);
            }
        if (observation.TryGetProperty("fx", out var fx)) ValidateFxObservation(fx);
        if (observation.TryGetProperty("tsn", out var tsn))
        {
            ValidateTsnObservation(tsn);
            Require(DateTimeOffset.Parse(tsn.GetProperty("observedAt").GetString()!, CultureInfo.InvariantCulture) <=
                DateTimeOffset.Parse(observation.GetProperty("observedAt").GetString()!, CultureInfo.InvariantCulture).AddSeconds(1),
                "Network evidence cannot follow the observation.");
        }
        return Canonical(observation);
    }

    private static string ValidateFxConfiguration(JsonElement fx)
    {
        Shape(fx, ["mode"], ["publisherId", "publisherIdType", "writerGroupId", "dataSetWriterId", "metadataMajorVersion", "metadataMinorVersion", "interfaceAddress", "allowUnsecured"]);
        var mode = Choice(fx, "mode", ["diagnostics", "udp_uadp"]);
        if (mode == "diagnostics")
            Require(fx.EnumerateObject().Count() == 1, "FX diagnostics cannot configure cyclic communication.");
        else
        {
            var publisher = Text(fx, "publisherId", 128);
            var type = Choice(fx, "publisherIdType", ["Byte", "UInt16", "UInt32", "UInt64", "String"]);
            if (type != "String")
            {
                var max = type switch { "Byte" => new BigInteger(byte.MaxValue), "UInt16" => ushort.MaxValue, "UInt32" => uint.MaxValue, _ => ulong.MaxValue };
                DecimalInteger(publisher, BigInteger.Zero, max);
            }
            Integer(fx, "writerGroupId", 1, 65_535);
            Integer(fx, "dataSetWriterId", 1, 65_535);
            Integer(fx, "metadataMajorVersion", 0, uint.MaxValue);
            Integer(fx, "metadataMinorVersion", 0, uint.MaxValue);
            Require(Boolean(fx, "allowUnsecured"), "Unauthenticated UADP observation requires explicit commissioning.");
            if (fx.TryGetProperty("interfaceAddress", out _))
                Require(IPAddress.TryParse(Text(fx, "interfaceAddress", 64), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork, "UADP interface must be an IPv4 address.");
        }
        return mode;
    }

    private static void ValidateTsnConfiguration(JsonElement tsn)
    {
        Shape(tsn, ["interface"], ["ptpDomain", "collectorRef"]);
        Pattern(tsn, "interface", Interface, 15);
        if (tsn.TryGetProperty("ptpDomain", out _)) Integer(tsn, "ptpDomain", 0, 127);
        OptionalPattern(tsn, "collectorRef", Reference, 64);
    }

    private static void ValidateFxObservation(JsonElement fx)
    {
        Shape(fx, ["mode", "modelVerified", "components", "warnings", "determinism"],
            ["publisherId", "writerGroupId", "dataSetWriterId", "receivedMessages", "droppedMessages", "lastSequenceNumber"]);
        var mode = Choice(fx, "mode", ["diagnostics", "udp_uadp"]);
        var verified = Boolean(fx, "modelVerified");
        Require(mode != "udp_uadp" || !verified, "UDP observation alone does not verify an FX information model.");
        Choice(fx, "determinism", ["unverified"]);
        foreach (var component in Array(fx, "components", 20).EnumerateArray())
        {
            Shape(component, ["name", "nodeId", "namespaceUri", "kind"], ["state"]);
            Text(component, "name", 120);
            Text(component, "nodeId", 512);
            Text(component, "namespaceUri", 256);
            Choice(component, "kind", ["automation_component", "functional_entity", "connection_endpoint", "connection_manager"]);
            OptionalText(component, "state", 128);
        }
        foreach (var warning in Array(fx, "warnings", 8).EnumerateArray())
            Require(warning.ValueKind == JsonValueKind.String && Code.IsMatch(warning.GetString()!), "FX warning must be a bounded diagnostic code.");
        OptionalText(fx, "publisherId", 128);
        foreach (var field in new[] { "writerGroupId", "dataSetWriterId" }) if (fx.TryGetProperty(field, out _)) Integer(fx, field, 1, 65_535);
        foreach (var field in new[] { "receivedMessages", "droppedMessages" }) if (fx.TryGetProperty(field, out _)) Integer(fx, field, 0, SafeInteger);
        if (fx.TryGetProperty("lastSequenceNumber", out _)) Integer(fx, "lastSequenceNumber", 0, 65_535);
    }

    private static void ValidateTsnObservation(JsonElement tsn)
    {
        Shape(tsn, ["interface", "observedAt", "clock", "capabilities", "qdiscs", "counters", "evidence", "determinism"]);
        Pattern(tsn, "interface", Interface, 15);
        Timestamp(tsn, "observedAt");
        Choice(tsn, "determinism", ["unverified"]);
        var clock = tsn.GetProperty("clock");
        Shape(clock, ["state"], ["domain", "grandmasterId", "offsetNs", "meanPathDelayNs", "portState"]);
        Choice(clock, "state", ["synchronized", "unsynchronized", "unknown"]);
        if (clock.TryGetProperty("domain", out _)) Integer(clock, "domain", 0, 127);
        OptionalText(clock, "grandmasterId", 64);
        OptionalNumber(clock, "offsetNs");
        OptionalNumber(clock, "meanPathDelayNs");
        OptionalText(clock, "portState", 32);
        var capabilities = tsn.GetProperty("capabilities");
        Shape(capabilities, ["hardwareTimestamping"], ["ptpHardwareClock"]);
        Choice(capabilities, "hardwareTimestamping", ["supported", "unsupported", "unknown"]);
        OptionalText(capabilities, "ptpHardwareClock", 64);
        foreach (var qdisc in Array(tsn, "qdiscs", 8).EnumerateArray())
        {
            Shape(qdisc, ["kind"], ["handle", "parent"]);
            Text(qdisc, "kind", 32);
            OptionalText(qdisc, "handle", 32);
            OptionalText(qdisc, "parent", 32);
        }
        var counters = tsn.GetProperty("counters");
        var names = new[] { "rxDropped", "txDropped", "rxErrors", "txErrors" };
        Shape(counters, [], names);
        foreach (var name in names) if (counters.TryGetProperty(name, out _)) Integer(counters, name, 0, SafeInteger);
        foreach (var evidence in Array(tsn, "evidence", 8).EnumerateArray())
        {
            Shape(evidence, ["source", "status"]);
            Choice(evidence, "source", ["pmc", "ethtool", "tc", "sysfs"]);
            Choice(evidence, "status", ["observed", "unavailable"]);
        }
    }

    private static void ValidateEndpoint(string endpoint, string protocol, string fxMode)
    {
        Require(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri), "Connection endpoint must be an absolute protocol URI.");
        Require(string.IsNullOrEmpty(uri!.UserInfo) && string.IsNullOrEmpty(uri.Fragment), "Credentials and fragments are not allowed in endpoints.");
        var expected = protocol switch { "opcua" => "opc.tcp", "opcua_fx" => fxMode == "udp_uadp" ? "opc.udp" : "opc.tcp", "modbus_tcp" => "tcp", "modbus_rtu" => "serial", _ => "mqtt" };
        Require(uri.Scheme == expected || protocol == "mqtt" && uri.Scheme == "mqtts", "Endpoint protocol does not match the driver.");
        if (protocol == "modbus_rtu")
        {
            Require(string.IsNullOrEmpty(uri.Host) && uri.AbsolutePath.StartsWith("/dev/", StringComparison.Ordinal) && !endpoint.Contains("..", StringComparison.Ordinal) && !uri.AbsolutePath.Contains('%'), "Serial endpoints require a local /dev path.");
            var options = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                Require(pair.Length == 2 && options.Add(pair[0]), "Invalid serial endpoint options.");
                var value = pair[1];
                Require(pair[0] switch
                {
                    "baudrate" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var baud) && baud is >= 300 and <= 4_000_000,
                    "bytesize" => value is "7" or "8", "parity" => value is "N" or "E" or "O", "stopbits" => value is "1" or "2", _ => false,
                }, "Unsupported serial endpoint option.");
            }
        }
        else
        {
            Require(!string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.Query) && uri.Port is not 0 && uri.Port <= 65_535, "Invalid network endpoint.");
            if (protocol is "modbus_tcp" or "mqtt" || fxMode == "udp_uadp") Require(uri.AbsolutePath is "" or "/", "This protocol endpoint cannot contain a path.");
            if (fxMode == "udp_uadp") Require(uri.Port > 0, "UADP requires an explicit UDP port.");
        }
    }

    private static void ValidateValue(JsonElement value, string type)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (type == "boolean") Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "Boolean observation has an incompatible value.");
        else if (type == "string") Require(value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 1024, "String observation is invalid.");
        else if (type is "int64" or "uint64")
        {
            Require(value.ValueKind == JsonValueKind.String, "64-bit integer observations must use decimal strings.");
            DecimalInteger(value.GetString()!, type == "int64" ? long.MinValue : BigInteger.Zero, type == "int64" ? long.MaxValue : new BigInteger(ulong.MaxValue));
        }
        // dataType describes source encoding. Engineering scaling may make an integer source fractional.
        else Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number), "Numeric observation is invalid.");
    }

    private static void DecimalInteger(string value, BigInteger min, BigInteger max) => Require(
        Regex.IsMatch(value, "\\A(?:0|-?[1-9][0-9]{0,19})\\z", RegexOptions.CultureInvariant) &&
        BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max,
        "Decimal integer is outside its declared range.");

    private static void Shape(JsonElement item, string[] required, string[]? optional = null)
    {
        Require(item.ValueKind == JsonValueKind.Object, "Expected a structured connectivity object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(required.Concat(optional ?? []), StringComparer.Ordinal);
        foreach (var field in item.EnumerateObject()) Require(allowed.Contains(field.Name) && seen.Add(field.Name), "Unknown or duplicate connectivity field.");
        Require(required.All(seen.Contains), "A required connectivity field is missing.");
    }

    private static string Text(JsonElement item, string name, int max, bool allowEmpty = false)
    {
        Require(item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String, "Invalid connectivity text field.");
        var text = value.GetString()!;
        Require(text.Length <= max && (allowEmpty || !string.IsNullOrWhiteSpace(text)) && !text.Any(char.IsControl), "Connectivity text is empty, too long, or contains control characters.");
        return text;
    }
    private static void OptionalText(JsonElement item, string name, int max, bool allowEmpty = false) { if (item.TryGetProperty(name, out _)) Text(item, name, max, allowEmpty); }
    private static void Id(JsonElement item, string name) => Pattern(item, name, Identity, 64);
    private static void Pattern(JsonElement item, string name, Regex regex, int max) => Require(regex.IsMatch(Text(item, name, max)), "Connectivity identity or reference is invalid.");
    private static void OptionalPattern(JsonElement item, string name, Regex regex, int max) { if (item.TryGetProperty(name, out _)) Pattern(item, name, regex, max); }
    private static string Choice(JsonElement item, string name, string[] choices) { var value = Text(item, name, 128); Require(choices.Contains(value, StringComparer.Ordinal), "Unsupported connectivity field value."); return value; }
    private static void OptionalChoice(JsonElement item, string name, string[] choices) { if (item.TryGetProperty(name, out _)) Choice(item, name, choices); }
    private static long Integer(JsonElement item, string name, long min, long max)
    {
        Require(item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && decimal.Truncate(number) == number && number >= min && number <= max, "Connectivity integer is outside its permitted range.");
        return decimal.ToInt64(value.GetDecimal());
    }
    private static double? OptionalNumber(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number), "Connectivity number must be finite.");
        return value.GetDouble();
    }
    private static bool Boolean(JsonElement item, string name)
    {
        Require(item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False, "Connectivity flag must be boolean.");
        return value.GetBoolean();
    }
    private static JsonElement Array(JsonElement item, string name, int max)
    {
        Require(item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= max, "Connectivity list exceeds its permitted size.");
        return value;
    }
    private static void Uuid(JsonElement item, string name) => Require(UuidIdentity.IsMatch(Text(item, name, 36)), "Connectivity event or command identity must be a UUID.");
    private static void Pointer(JsonElement item, string name)
    {
        var pointer = Text(item, name, 256, allowEmpty: true);
        Require((pointer.Length == 0 || pointer.StartsWith('/')) && !Regex.IsMatch(pointer, "~(?![01])", RegexOptions.CultureInvariant), "MQTT fields require JSON pointers.");
    }
    private static void Timestamp(JsonElement item, string name)
    {
        var value = Text(item, name, 35);
        var match = Utc.Match(value);
        Require(match.Success, "Connectivity timestamps must be UTC ISO timestamps.");
        try
        {
            var fields = Enumerable.Range(1, 6).Select(i => int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
            _ = new DateTime(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("Connectivity timestamp is not a valid date."); }
    }
    private static void LimitBytes(JsonElement item, int max) => Require(Encoding.UTF8.GetByteCount(item.GetRawText()) <= max, "Connectivity payload exceeds its byte limit.");
    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }

    internal static JsonElement Canonical(JsonElement item)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) WriteCanonical(writer, item);
        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in item.EnumerateObject().OrderBy(field => field.Name, StringComparer.Ordinal)) { writer.WritePropertyName(field.Name); WriteCanonical(writer, field.Value); }
            writer.WriteEndObject();
        }
        else if (item.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var entry in item.EnumerateArray()) WriteCanonical(writer, entry);
            writer.WriteEndArray();
        }
        else item.WriteTo(writer);
    }
}
