using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;
using SmartFactory.Services.TelemetryService;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;
using SmartFactory.Services.TelemetryService.Infrastructure.Services;

// The Render release job applies versioned SQL before starting any worker.
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    var rawConnection = Environment.GetEnvironmentVariable("PostgresConnectionString")
        ?? throw new InvalidOperationException("PostgresConnectionString is required.");
    var production = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase);
    var migrationConnection = new NpgsqlConnectionStringBuilder(PostgresSecurity.ValidatePostgresTls(rawConnection, production))
    {
        Pooling = false,
        Timeout = 10,
    };
    await using var database = new NpgsqlConnection(migrationConnection.ConnectionString);
    for (var attempt = 0; ; attempt++)
    {
        try { await database.OpenAsync(); break; }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.TooManyConnections && attempt < 8)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(30, 2 << attempt));
            Console.WriteLine($"PostgreSQL connection limit reached; retrying telemetry migration in {delay.TotalSeconds:0}s.");
            await Task.Delay(delay);
        }
    }
    await using var transaction = await database.BeginTransactionAsync();
    await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(20261005)", database, transaction))
        await migrationLock.ExecuteNonQueryAsync();
    var directory = Environment.GetEnvironmentVariable("TELEMETRY_MIGRATIONS_DIR") ?? "deploy/migrations";
    foreach (var file in new[] { "telemetry-schema.sql", "20261005_telemetry_delivery.sql", "20261006_dashboard_outbox_index.sql", "20261006_sensor_metadata.sql", "20261025_onesa_provenance.sql" })
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(directory, file));
        await using var command = new NpgsqlCommand(sql, database, transaction);
        await command.ExecuteNonQueryAsync();
        Console.WriteLine("Applied telemetry migration: " + file);
    }
    await transaction.CommitAsync();
    return;
}

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        // The worker uses an independently migrated telemetry database.
        var connectionString = Environment.GetEnvironmentVariable("PostgresConnectionString")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        
        if (!string.IsNullOrEmpty(connectionString))
        {
            connectionString = PostgresSecurity.ValidatePostgresTls(connectionString,
                string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase));
            var pooledConnection = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 2, MinPoolSize = 0 };
            connectionString = pooledConnection.ConnectionString;
            services.AddDbContext<TelemetryDbContext>(options =>
                options.UseNpgsql(connectionString));
        }
        else
        {
            throw new InvalidOperationException("PostgresConnectionString is required for telemetry persistence.");
        }

        services.AddTransient<TelemetryFunction>();
        services.AddHostedService<TelemetryOutboxService>();
        services.AddHttpClient("dashboard-ingestion", client => client.Timeout = TimeSpan.FromSeconds(10));
        // Share one subscriber instance between the worker and its readiness endpoint.
        services.AddSingleton<MqttTelemetrySubscriberService>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MqttTelemetrySubscriberService>());
        services.AddHostedService<RenderHealthEndpointService>();
    })
    .Build();

host.Run();

public sealed class RenderHealthEndpointService : BackgroundService
{
    private readonly ILogger<RenderHealthEndpointService> _logger;
    private readonly MqttTelemetrySubscriberService _subscriber;
    private TcpListener? _listener;
    private readonly IServiceScopeFactory _scopes;
    private readonly SemaphoreSlim _connections = new(32);

    public RenderHealthEndpointService(
        MqttTelemetrySubscriberService subscriber,
        ILogger<RenderHealthEndpointService> logger,
        IServiceScopeFactory scopes)
    {
        _subscriber = subscriber;
        _logger = logger;
        _scopes = scopes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = ResolvePort();
        // In the split Render topology TelemetryService is the worker's public
        // health endpoint. In the legacy/all-in-one bundle it remains private
        // behind the Node API readiness aggregator.
        _listener = new TcpListener(ResolveBindAddress(
            Environment.GetEnvironmentVariable("RENDER_SERVICE_ROLE"),
            Environment.GetEnvironmentVariable("BACKEND_DEPLOYMENT_MODE")), port);
        _listener.Start();
        _logger.LogInformation("Health endpoint listening on port {Port}.", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stoppingToken);
            }
            catch (Exception error) when (IsExpectedListenerShutdown(error, stoppingToken))
            {
                break;
            }

            try
            {
                await _connections.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                client.Dispose();
                break;
            }
            _ = HandleHealthRequest(client, stoppingToken);

        }
    }

    private async Task HandleHealthRequest(TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var cancellation = timeout.Token;
                await using var stream = client.GetStream();
                var requestBuffer = new byte[1024];
                var bytesRead = await stream.ReadAsync(requestBuffer, cancellation);
                var request = Encoding.ASCII.GetString(requestBuffer, 0, bytesRead).Split('\n')[0]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var route = request.ElementAtOrDefault(1);
                var known = route is "/health" or "/health/live" or "/health/ready";
                var ready = true;
                if (route is "/health" or "/health/ready")
                {
                    using var scope = _scopes.CreateScope();
                    var database = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
                    ready = _subscriber.IsSubscriptionReady && await database.Database.CanConnectAsync(cancellation);
                }
                var status = !known ? "404 Not Found" : !ready ? "503 Service Unavailable" : "200 OK";
                var body = !known ? "Not found" : ready ? "OK" : "MQTT subscription or telemetry database unavailable";
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellation);
                await stream.WriteAsync(bodyBytes, cancellation);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or System.IO.IOException or SocketException)
        {
            client.Dispose();
            _logger.LogDebug("Health request timed out or disconnected.");
        }
        catch (Exception error)
        {
            client.Dispose();
            _logger.LogWarning(error, "Health request failed.");
        }
        finally { _connections.Release(); }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Stop();
        return base.StopAsync(cancellationToken);
    }

    public static bool IsExpectedListenerShutdown(Exception error, CancellationToken stoppingToken) =>
        stoppingToken.IsCancellationRequested &&
        error is OperationCanceledException or SocketException or ObjectDisposedException;

    private static int ResolvePort()
    {
        var fromPort = Environment.GetEnvironmentVariable("PORT");
        if (int.TryParse(fromPort, out var explicitPort))
        {
            return explicitPort;
        }

        var aspnetCoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(aspnetCoreUrls))
        {
            foreach (var candidate in aspnetCoreUrls.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Port > 0)
                {
                    return uri.Port;
                }
            }
        }

        return 10000;
    }

    public static IPAddress ResolveBindAddress(string? renderRole, string? deploymentMode) =>
        string.Equals(renderRole, "worker", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Any
            : deploymentMode == "render-bundle" ? IPAddress.Loopback : IPAddress.Any;
}


internal static class PostgresSecurity
{
    public static string ValidatePostgresTls(string value, bool production)
    {
        if (!production) return value;
        var parsed = new NpgsqlConnectionStringBuilder(value);
        if (parsed.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(parsed.RootCertificate))
            throw new InvalidOperationException("Production PostgreSQL requires SSL Mode=VerifyFull and a mounted provider Root Certificate.");
        return value;
    }
}
