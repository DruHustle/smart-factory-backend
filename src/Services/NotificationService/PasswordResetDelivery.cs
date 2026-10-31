using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartFactory.BuildingBlocks.DashboardAccess;

namespace SmartFactory.Services.NotificationService;

public sealed record PasswordResetDelivery(
    long Id, int UserId, string Email, string TokenHash, int AuthVersion,
    string Payload, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt,
    int Attempts, DateTimeOffset LeaseUntil);

public sealed record PasswordResetAccount(
    int Id, string OpenId, string? Email, int AuthVersion, DateTimeOffset? DisabledAt);

/// <summary>The worker holds the mail credential; the API queues only authenticated ciphertext.</summary>
public sealed class PasswordResetDeliveryOptions
{
    private readonly byte[]? key;
    public string? Origin { get; }
    public bool Configured => key is not null && Origin is not null;

    public PasswordResetDeliveryOptions(string serviceToken, string? origin, bool development = false)
    {
        Origin = CanonicalOrigin(origin, development);
        if (Encoding.UTF8.GetByteCount(serviceToken) >= 32)
            key = SHA256.HashData(Encoding.UTF8.GetBytes("smart-factory:password-reset:key:v1:" + serviceToken));
    }

    public static PasswordResetDeliveryOptions FromConfiguration(IConfiguration configuration, bool development) => new(
        configuration["DASHBOARD_SERVICE_TOKEN"] ?? "",
        configuration["PASSWORD_RESET_ORIGIN"] ?? configuration["ALLOWED_ORIGIN"],
        development);

    public static string? CanonicalOrigin(string? value, bool development)
    {
        var input = value?.Trim();
        if (string.IsNullOrEmpty(input) || !Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        var loopback = uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
        if (uri.Scheme != "https" && !(development && uri.Scheme == "http" && loopback)) return null;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return input == origin || input == origin + "/" ? origin : null;
    }

    public bool TryDecryptLink(PasswordResetDelivery delivery, out string? resetUrl)
    {
        resetUrl = null;
        if (!Configured || delivery.Id <= 0 || delivery.UserId <= 0 || delivery.AuthVersion < 0 ||
            delivery.Payload.Length is < 1 or > 8192 ||
            !Regex.IsMatch(delivery.TokenHash, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)) return false;
        byte[]? plaintext = null;
        try
        {
            using var envelope = JsonDocument.Parse(delivery.Payload);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
                root.GetProperty("v").GetInt32() != 1) return false;
            var iv = Convert.FromBase64String(root.GetProperty("iv").GetString()!);
            var tag = Convert.FromBase64String(root.GetProperty("tag").GetString()!);
            var data = Convert.FromBase64String(root.GetProperty("data").GetString()!);
            if (iv.Length != 12 || tag.Length != 16 || data.Length is < 1 or > 4096) return false;
            plaintext = new byte[data.Length];
            var associated = Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"smart-factory:password-reset:v1:{delivery.Id}:{delivery.UserId}:{delivery.AuthVersion}:{delivery.Email}"));
            using (var aes = new AesGcm(key!, 16)) aes.Decrypt(iv, data, tag, plaintext, associated);
            using var message = JsonDocument.Parse(plaintext);
            var payload = message.RootElement;
            if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 2) return false;
            var token = payload.GetProperty("token").GetString();
            var link = payload.GetProperty("resetUrl").GetString();
            if (token is null || !Regex.IsMatch(token, "\\A[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant)) return false;
            var decoded = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
            if (decoded.Length != 32 || Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_') != token)
                return false;
            var expectedHash = Convert.FromHexString(delivery.TokenHash);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(Encoding.UTF8.GetBytes(token))) ||
                link != Origin + "/#/reset-password?token=" + token) return false;
            resetUrl = link;
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or
            KeyNotFoundException or ArgumentException or CryptographicException)
        {
            return false;
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

public static class PasswordResetDeliveryPolicy
{
    public const int MaximumAttempts = 4;

    public static string? Rejection(PasswordResetDelivery delivery, PasswordResetAccount? account, DateTimeOffset now)
    {
        if (delivery.UsedAt is not null) return "already_used";
        if (delivery.ExpiresAt <= now) return "expired";
        if (account is null || account.Id != delivery.UserId || account.DisabledAt is not null ||
            account.AuthVersion != delivery.AuthVersion || string.IsNullOrWhiteSpace(account.OpenId) ||
            string.IsNullOrEmpty(account.Email) || !string.Equals(account.Email, delivery.Email, StringComparison.OrdinalIgnoreCase))
            return "account_changed";
        return DashboardSessionValidator.Shared(account.OpenId, account.Email) ? "shared_identity" : null;
    }

    // Inbox identities are positive. Negative reset identities keep Resend idempotency
    // and provider correlation separate without changing the shared sender contract.
    public static long ProviderIdentity(long resetId) => resetId > 0 ? -resetId :
        throw new ArgumentOutOfRangeException(nameof(resetId));

    public static string Body(string resetUrl, DateTimeOffset expiresAt) =>
        "A password reset was requested for your Smart Factory account.\n\n" +
        "Choose a new password using this one-time link:\n" + resetUrl + "\n\n" +
        "This link expires at " + expiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) +
        ". It cannot be used after a successful reset.\n\n" +
        "If you did not request this reset, ignore this email. Your password has not been changed.";
}
