using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace SmartFactory.BuildingBlocks.DashboardAccess;

public sealed record DashboardUser(int Id, string OpenId, string? Name, string? Email, string Role);

public sealed class DashboardStore : IDisposable
{
    public NpgsqlDataSource Source { get; }
    public DashboardStore(string connection, bool production)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (production && settings.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException("Dashboard PostgreSQL requires VerifyFull TLS in production.");
        settings.Timeout = 5;
        settings.CommandTimeout = 15;
        // Budget every process together with the six BaSyx services. The Free
        // Aiven instance has 20 slots; the complete steady-state budget is 15.
        settings.MaxPoolSize = 1;
        settings.MinPoolSize = 0;
        settings.ConnectionIdleLifetime = 30;
        Source = NpgsqlDataSource.Create(settings.ConnectionString);
    }
    public async Task<DashboardUser?> GetUser(int id, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("SELECT id, \"openId\", name, email, role::text FROM users WHERE id = $1 AND disabled_at IS NULL AND password_change_required=false");
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4) == "user" ? "viewer" : reader.GetString(4));
    }
    public void Dispose() => Source.Dispose();
}

public static class DashboardRuntime
{
    public static void AddDashboardAccess(this WebApplicationBuilder builder)
    {
        var connection = builder.Configuration["DASHBOARD_DATABASE_CONNECTION"]
            ?? throw new InvalidOperationException("DASHBOARD_DATABASE_CONNECTION is required.");
        var token = builder.Configuration["DASHBOARD_SERVICE_TOKEN"] ?? "";
        if (Encoding.UTF8.GetByteCount(token) < 32)
            throw new InvalidOperationException("DASHBOARD_SERVICE_TOKEN must contain at least 32 bytes.");
        builder.Services.AddSingleton(new DashboardStore(connection, builder.Environment.IsProduction()));
        builder.Services.AddSingleton<IDashboardSessionValidator, DashboardSessionValidator>();
    }
    public static bool ValidToken(string provided, string expected) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public static void UseDashboardAccess(this WebApplication app, string schemaProbe)
    {
        var token = app.Configuration["DASHBOARD_SERVICE_TOKEN"]!;
        var service = app.Environment.ApplicationName switch { "IdentityService" => "identity-service", "AnalyticsService" => "analytics-service", "NotificationService" => "notification-service", _ => "dotnet-dashboard" };
        app.Use(async (context, next) => {
            if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
            if (!ValidToken(context.Request.Headers["X-Service-Token"].ToString(), token) ||
                !int.TryParse(context.Request.Headers["X-User-Id"], out var id) || id < 1)
            { context.Response.StatusCode = 401; await SecurityObservation.Record(context.RequestServices.GetRequiredService<DashboardStore>(), service, 401, app.Logger, context.RequestAborted); return; }
            var user = await context.RequestServices.GetRequiredService<DashboardStore>().GetUser(id, context.RequestAborted);
            if (user is null || !int.TryParse(context.Request.Headers["X-Auth-Version"], out var version) ||
                !await context.RequestServices.GetRequiredService<IDashboardSessionValidator>().Current(user.OpenId, context.Request.Headers["X-Session-Id"].ToString(), version, user.Role, context.RequestAborted)) { context.Response.StatusCode = 401; await SecurityObservation.Record(context.RequestServices.GetRequiredService<DashboardStore>(), service, 401, app.Logger, context.RequestAborted); return; }
            context.Items["dashboardUser"] = user;
            await next();
            if(context.Response.StatusCode==403) await SecurityObservation.Record(context.RequestServices.GetRequiredService<DashboardStore>(),service,403,app.Logger,context.RequestAborted);
        });
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (DashboardStore store, CancellationToken ct) => {
            try {
                await using var command = store.Source.CreateCommand(schemaProbe);
                await command.ExecuteScalarAsync(ct);
                return Results.Ok(new { status = "ready" });
            } catch { return Results.StatusCode(503); }
        });
    }
    public static DashboardUser User(HttpContext context) => (DashboardUser)context.Items["dashboardUser"]!;
}
