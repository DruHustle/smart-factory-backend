using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
namespace SmartFactory.BuildingBlocks.DashboardAccess;

/// <summary>Observes actual service decisions into the existing application's durable outbox.
/// Does not accept producer/asset/user authority from a request, and never records credentials.</summary>
public static class SecurityObservation
{
    private static long lastWarning;
    public static string? RejectionType(int status) => status switch {
        401 => "auth.authentication_failed", 403 => "auth.authorization_denied", _ => null };
    public static string Envelope(string service, int status, Guid id, DateTimeOffset occurredAt, string mode="live", string? runId=null)
    {
        var type=RejectionType(status) ?? throw new ArgumentException("Only rejected decisions are observations.");
        if(service.Length is <1 or >64 || service.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'&&c!='_'))throw new ArgumentException("Unsupported service identity.");
        if(mode is not ("live" or "simulated" or "replay"))throw new ArgumentException("Unsupported source mode.");
        if(mode!="live" && (runId is null || runId.Length is <1 or >128 || runId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_' or '.' or ':'))))throw new ArgumentException("A bounded run identity is required.");
        var value=new Dictionary<string,object?> {
            ["schemaVersion"]="ot-security-evidence/v1",["producerNamespace"]="smart-factory-dotnet",["sourceEventId"]=id.ToString(),
            ["occurredAt"]=occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),["mode"]=mode,["eventType"]=type,
            ["assetRef"]=null,["summary"]="A .NET service rejected an access request.",["quality"]="good",["producerVersion"]="smart-factory-dotnet-1",
            ["attributes"]=new {service,outcome="denied",reasonCode=status==401?"INVALID_SERVICE_AUTHENTICATION":"FORBIDDEN"}
        };
        if(mode=="simulated")value["scenarioRunId"]=runId;
        if(mode=="replay")value["replayRunId"]=runId;
        return JsonSerializer.Serialize(value);
    }
    public static async Task Record(DashboardStore store,string service,int status,ILogger logger,CancellationToken cancellationToken=default)
    {
        if(Environment.GetEnvironmentVariable("SECURITY_ENABLED")=="false" || RejectionType(status) is null)return;
        try {
            var id=Guid.NewGuid();var occurredAt=DateTimeOffset.UtcNow;
            var mode=Environment.GetEnvironmentVariable("SECURITY_NATIVE_MODE")??"live";
            var payload=Envelope(service,status,id,occurredAt,mode,Environment.GetEnvironmentVariable(mode=="replay"?"SECURITY_REPLAY_RUN_ID":"SECURITY_SCENARIO_RUN_ID"));
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var connection=await store.Source.OpenConnectionAsync(timeout.Token);
            await using var transaction=await connection.BeginTransactionAsync(timeout.Token);
            await using(var policy=new NpgsqlCommand("SET LOCAL statement_timeout='1500ms'; SELECT pg_advisory_xact_lock(2026102704)",connection,transaction))await policy.ExecuteNonQueryAsync(timeout.Token);
            await using(var prune=new NpgsqlCommand("DELETE FROM security_outbox WHERE id IN (SELECT id FROM security_outbox WHERE delivered_at IS NOT NULL ORDER BY delivered_at LIMIT 1000) AND (SELECT count(*) FROM security_outbox)>=10000",connection,transaction))await prune.ExecuteNonQueryAsync(timeout.Token);
            await using var command=new NpgsqlCommand("""
            INSERT INTO security_outbox(id,producer,event_type,attributes,occurred_at)
            SELECT $1,'dotnet-services',$2,$3::jsonb,$4
            WHERE (SELECT count(*) FROM security_outbox WHERE delivered_at IS NULL)<10000
            """,connection,transaction);
            command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(RejectionType(status)!);
            command.Parameters.AddWithValue(payload);command.Parameters.AddWithValue(occurredAt.UtcDateTime);
            if(await command.ExecuteNonQueryAsync(timeout.Token)!=1)throw new InvalidOperationException("Security capture capacity reached.");
            await transaction.CommitAsync(timeout.Token);
        } catch {
            // The real access outcome remains unchanged. Surface an explicit capture gap,
            // with a bounded warning rather than logging SQL, request bodies or secrets.
            var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var previous=Interlocked.Read(ref lastWarning);
            if(now-previous>60 && Interlocked.CompareExchange(ref lastWarning,now,previous)==previous)
                logger.LogWarning("Security observation could not be queued; investigate collection coverage. Access decision preserved.");
        }
    }
}
