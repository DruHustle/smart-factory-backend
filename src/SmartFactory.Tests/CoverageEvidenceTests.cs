using Microsoft.Extensions.Configuration;
using Npgsql;
using SmartFactory.BuildingBlocks.DashboardAccess;
using SmartFactory.Services.AnalyticsService;
namespace SmartFactory.Tests;
public class CoverageEvidenceTests
{
    [Fact, Trait("Category","Postgres")]
    public async Task AppliesEvidenceAndVisibilityBeforeAggregatesAndKeepsUnknownUnitsOut()
    {
        var connection=Environment.GetEnvironmentVariable("TEST_DASHBOARD_DATABASE_CONNECTION")??throw new InvalidOperationException("Explicit disposable database required");
        var settings=new NpgsqlConnectionStringBuilder(connection);
        if(!settings.Database!.EndsWith("_e2e")||!new[]{"127.0.0.1","localhost","database","host.docker.internal"}.Contains(settings.Host))throw new InvalidOperationException("Local _e2e storage required");
        var schema="coverage_"+Guid.NewGuid().ToString("N")+"_e2e";
        await using var admin=NpgsqlDataSource.Create(connection);
        await using(var create=admin.CreateCommand($"CREATE SCHEMA {schema}"))await create.ExecuteNonQueryAsync();
        try {
            settings.SearchPath=schema;
            using var store=new DashboardStore(settings.ConnectionString,false);
            await using(var fixture=store.Source.CreateCommand("""
            CREATE TABLE assets("assetId" text primary key,"isDemo" boolean);
            CREATE TABLE devices(id integer primary key,"isDemo" boolean);
            CREATE TABLE system_settings(key text primary key,value jsonb);
            CREATE TABLE sensor_readings(id integer generated always as identity,"assetId" text,"deviceId" integer,timestamp bigint,integration jsonb,measurements jsonb,temperature real,humidity real,vibration real,power real,pressure real,rpm real);
            INSERT INTO assets VALUES('urn:private:coverage',false),('urn:demo:coverage',true);
            INSERT INTO devices VALUES(1,false),(2,true);
            INSERT INTO system_settings VALUES('demo_data_enabled','false');
            INSERT INTO sensor_readings("assetId","deviceId",timestamp,integration,measurements,power) VALUES
            ('urn:private:coverage',1,1000,'{"mode":"live"}','{"power":{"canonicalUnit":"W","conversionVersion":"sf-units-v1","quality":"good"}}',10),
            ('urn:private:coverage',1,2000,'{"mode":"live"}','{"power":{"canonicalUnit":"W","conversionVersion":"sf-units-v1","quality":"good"}}',30),
            ('urn:private:coverage',1,3000,'{"mode":"simulated"}','{"power":{"canonicalUnit":"W","conversionVersion":"sf-units-v1","quality":"good"}}',1000),
            ('urn:private:coverage',1,4000,'{"mode":"replay"}','{"power":{"canonicalUnit":"W","conversionVersion":"sf-units-v1","quality":"good"}}',2000),
            ('urn:private:coverage',1,5000,null,null,999),
            ('urn:private:coverage',1,6000,'{"mode":"live"}',null,888),
            ('urn:private:coverage',1,7000,'{"mode":"live"}','{"power":{"canonicalUnit":"W","conversionVersion":"sf-units-v1","quality":"bad"}}',500),
            ('urn:private:coverage',1,8000,'{"mode":"unknown","evidence":{"legacyReviewed":true}}','{"power":{"canonicalUnit":"W","conversionVersion":"sf-units-v1","quality":"good"}}',50),
            ('urn:demo:coverage',2,1000,'{"mode":"simulated"}','{"power":{"canonicalUnit":"W","conversionVersion":"sf-units-v1","quality":"good"}}',42);
            """))await fixture.ExecuteNonQueryAsync();
            var analytics=new CoverageAnalytics(store);
            async Task<AssetCoverage> Query(string scope)=>(await analytics.Query(new(["urn:private:coverage"],1,10000,scope),default)).Single();
            var live=await Query("live");Assert.Equal(4,live.Samples);var power=live.Metrics.Single(m=>m.Metric=="power");Assert.Equal(2,power.Samples);Assert.Equal(20,power.Average);Assert.Equal("W",power.Unit);Assert.Equal(2,power.UnverifiedSamples);
            Assert.Equal(1000,(await Query("simulated")).Metrics.Single(m=>m.Metric=="power").Average);
            Assert.Equal(2000,(await Query("replay")).Metrics.Single(m=>m.Metric=="power").Average);
            Assert.Equal(30,(await Query("operational")).Metrics.Single(m=>m.Metric=="power").Average);
            Assert.Equal(2,(await Query("unknown")).Samples);
            Assert.Empty(await analytics.Query(new(["urn:demo:coverage"],1,10000,"simulated"),default));
            await using(var show=store.Source.CreateCommand("UPDATE system_settings SET value='true'::jsonb"))await show.ExecuteNonQueryAsync();
            Assert.Single(await analytics.Query(new(["urn:demo:coverage"],1,10000,"simulated"),default));
            Assert.False(CoverageAnalytics.Valid(new(["urn:private:coverage"],1,10000,"invalid")));
        }finally{await using var drop=admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");await drop.ExecuteNonQueryAsync();}
    }
}
