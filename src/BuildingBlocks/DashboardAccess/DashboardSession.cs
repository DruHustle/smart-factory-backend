using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SmartFactory.BuildingBlocks.DashboardAccess;

public interface IDashboardSessionValidator
{
    Task<bool> Current(string openId, string sessionId, int version, string? role, CancellationToken ct);
}
public sealed class DashboardSessionValidator(DashboardStore store, IConfiguration configuration) : IDashboardSessionValidator
{
    public static bool PublicDeployment(IConfiguration configuration) => configuration["PUBLIC_DEMO_DEPLOYMENT"] == "true" || configuration["ENABLE_DEMO_ACCOUNTS"] == "true";
    public static bool Shared(string openId, string? email) => openId.StartsWith("demo-", StringComparison.Ordinal) ||
        new[] { "admin@dev.local", "operator@dev.local", "tech@dev.local", "demo@dev.local" }.Contains(email?.ToLowerInvariant());
    public async Task<bool> Current(string openId, string sessionId, int version, string? role, CancellationToken ct)
    {
        if (PublicDeployment(configuration) || string.IsNullOrWhiteSpace(openId) || openId.Length > 255 ||
            !Guid.TryParseExact(sessionId, "D", out _) || version < 0) return false;
        await using var command = store.Source.CreateCommand("""
        SELECT "openId", email, role::text FROM users u WHERE "openId"=$1 AND disabled_at IS NULL
          AND password_change_required=false AND auth_version=$2
          AND NOT EXISTS(SELECT 1 FROM session_revocations r WHERE r.session_hash=$3 AND r.expires_at>now())
        """);
        command.Parameters.AddWithValue(openId); command.Parameters.AddWithValue(version);
        command.Parameters.AddWithValue(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || Shared(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))) return false;
        var currentRole = reader.GetString(2) == "user" ? "viewer" : reader.GetString(2);
        return role is null || currentRole == (role == "user" ? "viewer" : role);
    }
}
