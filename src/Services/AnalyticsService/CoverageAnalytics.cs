using Npgsql;
using SmartFactory.BuildingBlocks.DashboardAccess;
namespace SmartFactory.Services.AnalyticsService;

public sealed record CoverageRequest(string[] AssetIds, long StartTime, long EndTime, string Scope = "operational");
public sealed record MetricCoverage(string Metric, long Samples, double? Average, double? Minimum, double? Maximum, string? Unit = null, long UnverifiedSamples = 0);
public sealed record AssetCoverage(string AssetId, long Samples, long FirstAt, long LastAt, long? LongestGapMs, List<MetricCoverage> Metrics);
public sealed class CoverageAnalytics(DashboardStore store)
{
    public static bool Valid(CoverageRequest request) => request.AssetIds is { Length: > 0 and <= 200 }
        && request.AssetIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl))
        && request.Scope is "operational" or "live" or "simulated" or "replay" or "unknown" or "all"
        && request.StartTime >= 0 && request.EndTime > request.StartTime && request.EndTime - request.StartTime <= 93L * 86400000;
    public async Task<List<AssetCoverage>> Query(CoverageRequest request, CancellationToken ct)
    {
        if (!Valid(request)) throw new ArgumentException("Invalid coverage scope");
        var names = new[] { "temperature", "humidity", "vibration", "power", "pressure", "rpm" };
        var units = new[] { "°C", "%", "mm/s", "W", "Pa", "rpm" };
        // Identifiers and units come only from these fixed arrays; request values are parameters.
        var normalized = string.Join(",", names.Select((name,i) => $"CASE WHEN r.{name} IS NOT NULL OR jsonb_typeof(r.measurements->'{name}'->'sourceValue')='number' THEN 1 ELSE NULL END as raw_{name},CASE WHEN r.measurements->'{name}'->>'canonicalUnit'='{units[i]}' AND r.measurements->'{name}'->>'conversionVersion'='sf-units-v1' AND r.measurements->'{name}'->>'quality'='good' THEN COALESCE((r.measurements->'{name}'->>'canonicalValue')::double precision,r.{name}) ELSE NULL END AS {name}"));
        var aggregates = string.Join(",", names.Select(name=>$"count({name}),avg({name}),min({name}),max({name}),count(raw_{name})-count({name})"));
        var query = $$"""
        WITH scoped AS (
          SELECT r.id,r."assetId",r.timestamp,{{normalized}}
          FROM sensor_readings r JOIN assets a ON a."assetId"=r."assetId" JOIN devices d ON d.id=r."deviceId"
          WHERE r."assetId"=ANY($1) AND r.timestamp >= $2 AND r.timestamp <= $3
          AND (NOT a."isDemo" OR coalesce((SELECT value='true'::jsonb FROM system_settings WHERE key='demo_data_enabled'),$5))
          AND (NOT d."isDemo" OR coalesce((SELECT value='true'::jsonb FROM system_settings WHERE key='demo_data_enabled'),$5))
          AND ($4='all' OR ($4='unknown' AND coalesce(r.integration->>'mode','unknown') NOT IN ('live','simulated','replay'))
            OR ($4='operational' AND (r.integration->>'mode'='live' OR (coalesce(r.integration->>'mode','unknown') NOT IN ('live','simulated','replay') AND r.integration->'evidence'->>'legacyReviewed'='true')))
            OR ($4 IN ('live','simulated','replay') AND r.integration->>'mode'=$4))
        ), samples AS (
          SELECT *,timestamp-lag(timestamp) OVER(PARTITION BY "assetId" ORDER BY timestamp,id) AS gap FROM scoped
        )
        SELECT "assetId",count(*),min(timestamp),max(timestamp),max(gap),{{aggregates}}
        FROM samples GROUP BY "assetId" ORDER BY "assetId"
        """;
        await using var command = store.Source.CreateCommand(query);
        command.Parameters.AddWithValue(request.AssetIds.Distinct().ToArray());
        command.Parameters.AddWithValue(request.StartTime); command.Parameters.AddWithValue(request.EndTime);
        command.Parameters.AddWithValue(request.Scope); command.Parameters.AddWithValue(Environment.GetEnvironmentVariable("ENABLE_DEMO_DATA")=="true");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var output = new List<AssetCoverage>();
        while (await reader.ReadAsync(ct)) {
            var metrics = new List<MetricCoverage>();
            for (var i=0;i<names.Length;i++) {
                var column=5+i*5;
                double? Number(int index) => reader.IsDBNull(index)?null:Convert.ToDouble(reader.GetValue(index));
                var count=reader.GetInt64(column);
                metrics.Add(new(names[i],count,Number(column+1),Number(column+2),Number(column+3),count>0?units[i]:null,reader.GetInt64(column+4)));
            }
            output.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.IsDBNull(4)?null:reader.GetInt64(4),metrics));
        }
        return output;
    }
}
