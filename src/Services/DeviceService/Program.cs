using SmartFactory.BuildingBlocks.DashboardAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Net;
using Npgsql;
using SmartFactory.Services.DeviceService.Domain.Interfaces;
using SmartFactory.Services.DeviceService.Infrastructure.Data;
using SmartFactory.Services.DeviceService.Infrastructure.Repositories;
using SmartFactory.Services.DeviceService.Application.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 55 * 1024 * 1024);
var jwtSecret = builder.Configuration["JWT_SECRET"] ?? Environment.GetEnvironmentVariable("JWT_SECRET");
if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
    throw new InvalidOperationException("JWT_SECRET must be configured with at least 32 bytes for DeviceService.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.Events = new JwtBearerEvents { OnTokenValidated = async context => {
        var principal = context.Principal!;
        var validator = context.HttpContext.RequestServices.GetService<IDashboardSessionValidator>();
        if (validator is null || !int.TryParse(principal.FindFirst("sv")?.Value, out var version) || principal.FindFirst("pc")?.Value == "true") { context.Fail("Session unavailable"); return; }
        try { if (!await validator.Current(principal.FindFirst("openId")?.Value ?? "", principal.FindFirst("sid")?.Value ?? "", version, principal.FindFirst("role")?.Value, context.HttpContext.RequestAborted)) context.Fail("Session no longer active"); }
        catch { context.Fail("Session verification unavailable"); }
    }};

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = "smart-factory-iot", ValidateAudience = true, ValidAudience = "smart-factory-iot-api",
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ValidateLifetime = true, RoleClaimType = "role", NameClaimType = "name", ClockSkew = TimeSpan.FromSeconds(30),
    };
});
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Name = "Authorization", In = ParameterLocation.Header, Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = ValidatePostgresTls(connectionString, builder.Environment.IsProduction());
    var pooledConnection = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 1, MinPoolSize = 0, ConnectionIdleLifetime = 30 };
    connectionString = pooledConnection.ConnectionString;
    builder.Services.AddDbContext<DeviceDbContext>(options => options.UseNpgsql(connectionString));
}
else if (builder.Environment.IsDevelopment()) builder.Services.AddDbContext<DeviceDbContext>(options => options.UseInMemoryDatabase("DeviceDb"));
else throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required outside Development.");

builder.Services.AddScoped<EdgeConfigurationPublisher>();
builder.Services.AddSingleton<RedisAasChangePublisher>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RedisAasChangePublisher>());
builder.Services.AddHttpClient<AasProvisioningService>(client => client.Timeout = TimeSpan.FromMinutes(2)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IDeviceRepository, DeviceRepository>();
builder.Services.AddHealthChecks();

var securityConnection=builder.Configuration["DASHBOARD_DATABASE_CONNECTION"];
if(!string.IsNullOrWhiteSpace(securityConnection)) {
    builder.Services.AddSingleton(new DashboardStore(securityConnection,builder.Environment.IsProduction()));
    builder.Services.AddSingleton<IDashboardSessionValidator, DashboardSessionValidator>();
}
var app = builder.Build();
app.Use(async (context,next)=>{
    await next();
    if(context.Request.Path.StartsWithSegments("/api") && SecurityObservation.RejectionType(context.Response.StatusCode) is not null && context.RequestServices.GetService<DashboardStore>() is { } securityStore)
        await SecurityObservation.Record(securityStore,"dotnet-device-service",context.Response.StatusCode,app.Logger,context.RequestAborted);
});
var applyMigrations = builder.Configuration.GetValue<bool>("APPLY_DATABASE_MIGRATIONS");
var migrationOnly = builder.Configuration.GetValue<bool>("MIGRATION_ONLY");
if (migrationOnly && !applyMigrations)
    throw new InvalidOperationException("MIGRATION_ONLY requires APPLY_DATABASE_MIGRATIONS=true.");
if (applyMigrations)
{
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Database migrations require ConnectionStrings:DefaultConnection.");
    await using var migrationScope = app.Services.CreateAsyncScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<DeviceDbContext>();
    await migrationDb.Database.MigrateAsync();
}
if (migrationOnly) return;
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGet("/health/ready", async (RedisAasChangePublisher redis, DeviceDbContext database, CancellationToken cancellationToken) =>
{
    if (!redis.IsReady)
        return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);

    try
    {
        return await database.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
    }
    catch
    {
        return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
    }
});
app.MapControllers();
app.Run();

static string ValidatePostgresTls(string value, bool production)
{
    if (!production) return value;
    var parsed = new NpgsqlConnectionStringBuilder(value);
    if (parsed.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(parsed.RootCertificate))
        throw new InvalidOperationException("Production PostgreSQL requires SSL Mode=VerifyFull and a mounted provider Root Certificate.");
    return value;
}

public partial class Program { }
