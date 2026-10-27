using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace SmartFactory.Services.TelemetryService;
public static class TelemetryMigrations
{
    public static string DirectoryPath => Environment.GetEnvironmentVariable("TELEMETRY_MIGRATIONS_DIR") ?? "deploy/migrations";
    public static string[] Files => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(DirectoryPath,"manifest.json")))
        ?? throw new InvalidOperationException("Telemetry migration manifest unavailable.");
    public static async Task<string> HashAsync(string name) => Convert.ToHexString(SHA256.HashData(
        await File.ReadAllBytesAsync(Path.Combine(DirectoryPath,name)))).ToLowerInvariant();
    public static async Task<string> DigestAsync()
    {
        var hashes=await Task.WhenAll(Files.Select(HashAsync));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",hashes)))).ToLowerInvariant();
    }
}
