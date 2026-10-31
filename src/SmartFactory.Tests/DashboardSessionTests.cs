using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartFactory.BuildingBlocks.DashboardAccess;
using System.Security.Cryptography;
using System.Text;

namespace SmartFactory.Tests;
public class DashboardSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Postgres")]
    public async Task PersistedIdentityChecksPermitApprovedIndividualsOnSharedOrigins(bool publicDeployment, bool demoAccounts)
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DASHBOARD_DATABASE_CONNECTION");
        if (string.IsNullOrEmpty(connection)) throw new InvalidOperationException("TEST_DASHBOARD_DATABASE_CONNECTION must name disposable local _e2e storage. Use Category!=Postgres for unit-only execution.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (!settings.Database!.EndsWith("_e2e", StringComparison.Ordinal) || !new[]{"localhost","127.0.0.1","host.docker.internal","database"}.Contains(settings.Host)) throw new InvalidOperationException("Disposable local _e2e database required");
        var schema = "session_" + Guid.NewGuid().ToString("N") + "_e2e";
        await using var admin = NpgsqlDataSource.Create(connection);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try {
            settings.SearchPath = schema;
            using var store = new DashboardStore(settings.ConnectionString, false);
            await using(var tables=store.Source.CreateCommand("""
            CREATE TABLE users(id integer primary key, "openId" text unique not null, email text, role text, auth_version integer, disabled_at timestamptz, password_change_required boolean default false);
            CREATE TABLE session_revocations(session_hash text primary key, expires_at timestamptz);
            INSERT INTO users VALUES
              (1,'individual-operator','individual@example.test','operator',3,null,false),
              (2,'demo-engineer','tech@dev.local','engineer',0,null,false),
              (3,'individual-reserved','ADMIN@DEV.LOCAL','admin',0,null,false),
              (4,'demo-renamed','renamed@example.test','admin',0,null,false),
              (5,'legacy-viewer','viewer@example.test','user',0,null,false);
            """)) await tables.ExecuteNonQueryAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["PUBLIC_DEMO_DEPLOYMENT"] = publicDeployment ? "true" : "false",
                ["ENABLE_DEMO_ACCOUNTS"] = demoAccounts ? "true" : "false"
            }).Build();
            using var services = new ServiceCollection()
                .AddSingleton<IConfiguration>(config)
                .AddSingleton(store)
                .AddSingleton<IDashboardSessionValidator, DashboardSessionValidator>()
                .BuildServiceProvider();
            var validator = services.GetRequiredService<IDashboardSessionValidator>();
            var sid = Guid.NewGuid().ToString();
            Assert.True(await validator.Current("individual-operator",sid,3,"operator",default));
            Assert.True(await validator.Current("legacy-viewer",sid,0,"viewer",default));
            Assert.False(await validator.Current("individual-operator",sid,2,"operator",default));
            Assert.False(await validator.Current("individual-operator",sid,3,"admin",default));
            Assert.False(await validator.Current("demo-engineer",sid,0,"engineer",default));
            Assert.False(await validator.Current("individual-reserved",sid,0,"admin",default));
            Assert.False(await validator.Current("demo-renamed",sid,0,"admin",default));
            Assert.False(await validator.Current("individual-operator","invalid",3,"operator",default));
            Assert.False(await validator.Current("unknown-individual",sid,0,"admin",default));
            await using(var revoke=store.Source.CreateCommand("INSERT INTO session_revocations VALUES($1,now()+interval '1 hour')")) { revoke.Parameters.AddWithValue(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant());await revoke.ExecuteNonQueryAsync(); }
            Assert.False(await validator.Current("individual-operator",sid,3,"operator",default));
            var second=Guid.NewGuid().ToString();
            await using(var disable=store.Source.CreateCommand("UPDATE users SET disabled_at=now() WHERE id=1")) await disable.ExecuteNonQueryAsync();
            Assert.False(await validator.Current("individual-operator",second,3,"operator",default));
            await using(var change=store.Source.CreateCommand("UPDATE users SET disabled_at=null,auth_version=4 WHERE id=1")) await change.ExecuteNonQueryAsync();
            Assert.False(await validator.Current("individual-operator",second,3,"operator",default));
            Assert.True(await validator.Current("individual-operator",second,4,"operator",default));
            await using(var temporary=store.Source.CreateCommand("UPDATE users SET password_change_required=true WHERE id=1")) await temporary.ExecuteNonQueryAsync();
            Assert.False(await validator.Current("individual-operator",second,4,"operator",default));
            Assert.False(await validator.Current("individual-operator",second,4,null,default));
        } finally { await using var drop=admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");await drop.ExecuteNonQueryAsync(); }
    }
}
