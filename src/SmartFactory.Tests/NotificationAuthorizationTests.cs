using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartFactory.BuildingBlocks.DashboardAccess;
using SmartFactory.Services.NotificationService;

namespace SmartFactory.Tests;

public class NotificationAuthorizationTests
{
    [Theory]
    [Trait("Category", "Postgres")]
    [InlineData("admin", true)]
    [InlineData("engineer", false)]
    [InlineData("viewer", false)]
    public async Task QueuedInfrastructureEmailRechecksAdministratorRoleAtDelivery(string role, bool allowed)
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DASHBOARD_DATABASE_CONNECTION")
            ?? throw new InvalidOperationException("Disposable local _e2e storage is required.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Database?.EndsWith("_e2e", StringComparison.Ordinal) != true ||
            !new[] { "localhost", "127.0.0.1", "host.docker.internal", "database" }.Contains(settings.Host))
            throw new InvalidOperationException("Disposable local _e2e storage is required.");
        var schema = "notification_role_" + Guid.NewGuid().ToString("N") + "_e2e";
        await using var admin = NpgsqlDataSource.Create(connection);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            settings.SearchPath = schema;
            using var store = new DashboardStore(settings.ConnectionString, false);
            await using (var setup = store.Source.CreateCommand("""
                CREATE TABLE users(id integer primary key, "openId" text, name text, email text, role text, disabled_at timestamptz, password_change_required boolean default false);
                CREATE TABLE notification_inbox(id bigint primary key, "userId" integer, kind text, title text, body text, attempts integer default 0, "emailStatus" text, "nextAttemptAt" timestamptz default now(), "lastError" text, "acceptedAt" timestamptz);
                INSERT INTO users VALUES(1,'individual-admin','Administrator','admin@example.test','admin',null,false);
                INSERT INTO notification_inbox(id,"userId",kind,title,body,"emailStatus") VALUES(1,1,'infrastructure','Installation incident','Private installation evidence','pending');
                """)) await setup.ExecuteNonQueryAsync();
            // The account was an administrator when this notification was queued.
            await using (var change = store.Source.CreateCommand("UPDATE users SET role=$1 WHERE id=1"))
            {
                change.Parameters.AddWithValue(role);
                await change.ExecuteNonQueryAsync();
            }
            using var services = new ServiceCollection().BuildServiceProvider();
            var sender = new RecordingSender();
            var worker = new NotificationDeliveryWorker(store, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<NotificationDeliveryWorker>.Instance);
            Assert.True(await worker.DeliverOne(sender, default));
            Assert.Equal(allowed ? 1 : 0, sender.Calls);
            await using var retained = store.Source.CreateCommand("SELECT \"emailStatus\" FROM notification_inbox WHERE id=1");
            Assert.Equal(allowed ? "accepted" : "no_recipient", await retained.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class RecordingSender : IEmailSender
    {
        public int Calls;
        public bool Configured => true;
        public string Provider => "Disposable test sender";
        public bool Allows(string? email) => email == "admin@example.test";
        public Task Send(string recipient, string title, string body, long notificationId, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
