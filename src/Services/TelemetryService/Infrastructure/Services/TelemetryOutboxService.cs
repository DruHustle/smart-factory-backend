using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SmartFactory.Services.TelemetryService.Infrastructure.Services;

/// <summary>Single-consumer persistent delivery queue; unfinished records survive process restart.</summary>
public sealed class TelemetryOutboxService(IServiceScopeFactory scopes, ILogger<TelemetryOutboxService> logger, TelemetryDeliveryWakeup wakeup) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delaySeconds = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var attempted = await scope.ServiceProvider.GetRequiredService<TelemetryFunction>().DrainPendingAsync(stoppingToken);
                delaySeconds = attempted > 0 ? 2 : Math.Min(30, delaySeconds * 2);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) {
                logger.LogWarning("Telemetry outbox is unavailable; pending deliveries remain in PostgreSQL.");
                delaySeconds = Math.Min(30, delaySeconds * 2);
            }
            // New committed acquisitions wake delivery immediately, even after
            // the empty queue has backed off. Missed wakes still retry within 30s.
            try { await wakeup.WaitAsync(TimeSpan.FromSeconds(delaySeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
