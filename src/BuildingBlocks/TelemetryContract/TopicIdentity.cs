using System.Text;
using System.Text.RegularExpressions;
namespace SmartFactory.Contracts;
/// <summary>mqtt-topic-v1: canonical IDs remain unchanged in payloads; dots use %2E in topic levels.</summary>
internal static class TopicIdentity
{
    private static readonly Regex Identifier=new("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$",RegexOptions.CultureInvariant);
    public static string Encode(string value)
    {
        if(!Identifier.IsMatch(value))throw new ArgumentException("Invalid canonical MQTT identity.");
        return value.Replace(".","%2E",StringComparison.Ordinal);
    }
    public static bool TryDecode(string value,out string identity)
    {
        identity=value.Replace("%2E",".",StringComparison.OrdinalIgnoreCase);
        return Identifier.IsMatch(identity); // Any other percent escape remains invalid.
    }
    public static string Decode(string value)=>TryDecode(value,out var identity)?identity:throw new ArgumentException("Invalid MQTT topic identity.");
    public static string Build(string site,string line,string device,string kind)
    {
        if(kind is not ("telemetry" or "heartbeat" or "commands" or "control-ack"))throw new ArgumentException("Unsupported MQTT topic kind.");
        var topic=$"factory/{Encode(site)}/{Encode(line)}/{Encode(device)}/{kind}";
        if(Encoding.UTF8.GetByteCount(topic)>255)throw new ArgumentException("Encoded MQTT topic exceeds the broker's 255-byte route bound.");
        return topic;
    }
}
