using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;
namespace SmartFactory.Services.TelemetryService.Infrastructure.Services;

/// <summary>One bounded, completion-driven status receipt for the singleton worker.</summary>
public sealed class WorkerStatusService(IServiceScopeFactory scopes,IHttpClientFactory clients,MqttTelemetrySubscriberService subscriber,ILogger<WorkerStatusService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellation)
    {
        var sink=Environment.GetEnvironmentVariable("INTERNAL_TELEMETRY_SINK_URL");
        var token=Environment.GetEnvironmentVariable("INGESTION_API_TOKEN");
        if(string.IsNullOrEmpty(sink)||string.IsNullOrEmpty(token))return;
        var endpoint=new Uri(new Uri(sink),"/api/internal/worker-status");
        while(!cancellation.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();
                var db=scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
                var applied=await db.Database.SqlQueryRaw<string>("SELECT hash AS \"Value\" FROM telemetry_schema_migrations").ToArrayAsync(cancellation);
                foreach(var file in TelemetryMigrations.Files)
                {
                    var hash=await TelemetryMigrations.HashAsync(file);
                    if(Array.IndexOf(applied,hash)<0)throw new InvalidOperationException("Worker schema identity unavailable.");
                }
                var metrics=await db.TelemetryRecords.GroupBy(r=>1).Select(g=>new {
                    pending=g.Count(r=>r.DashboardForwardedAt==null&&r.DeliveryQuarantinedAt==null),
                    quarantined=g.Count(r=>r.DeliveryQuarantinedAt!=null),
                    failedAttempts=g.Count(r=>r.DashboardForwardedAt==null&&r.DeliveryAttempts>0),
                    oldestPendingAt=g.Where(r=>r.DashboardForwardedAt==null&&r.DeliveryQuarantinedAt==null).Min(r=>r.AcceptedAt),
                }).SingleOrDefaultAsync(cancellation);
                var source=Environment.GetEnvironmentVariable("BACKEND_SOURCE_REVISION");
                if(source is not null&&!System.Text.RegularExpressions.Regex.IsMatch(source,"^[a-f0-9]{40}$"))source=null;
                var report=new {worker="telemetry",source,runtime=Environment.Version.ToString(),contractVersion=2,
                    migrationsDigest=await TelemetryMigrations.DigestAsync(),mqttSubscribed=subscriber.IsSubscriptionReady,
                    observedAt=DateTime.UtcNow,pending=metrics?.pending??0,quarantined=metrics?.quarantined??0,
                    failedAttempts=metrics?.failedAttempts??0,oldestPendingAt=metrics?.oldestPendingAt};
                using var request=new HttpRequestMessage(HttpMethod.Post,endpoint){Content=JsonContent.Create(report)};
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                using var response=await clients.CreateClient("dashboard-ingestion").SendAsync(request,cancellation);
                if(!response.IsSuccessStatusCode)logger.LogWarning("Worker status receipt was not accepted; status is not current.");
            }
            catch(OperationCanceledException)when(cancellation.IsCancellationRequested){break;}
            catch(Exception){logger.LogWarning("Worker status receipt unavailable; no current receipt was asserted.");}
            try{await Task.Delay(TimeSpan.FromSeconds(30),cancellation);}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){break;}
        }
    }
}
