using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartFactory.BuildingBlocks.DashboardAccess;
using SmartFactory.Services.NotificationService;

namespace SmartFactory.Tests;

internal static class PasswordResetFixture
{
    // Identical deterministic Node crypto fixture; these values are synthetic.
    public const string ServiceToken = "synthetic-cross-language-reset-key-2026";
    public const string Origin = "https://factory.example.test";
    public const string Token = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    public const string TokenHash = "ea866a757e4c38babfa8127cbe9a409d3e1f93a00ff1488ff735fcf917afffd0";
    public const string Email = "person@example.test";
    public const string Link = Origin + "/#/reset-password?token=" + Token;
    public const string Envelope = """
        {"v":1,"iv":"AAECAwQFBgcICQoL","tag":"ei8soCWOX95UYM9y9+V57Q==","data":"oPLQL/+WtsDkJjHYLKAE6FvNYW47Ark5U8KxXx+ZqPnorufUcIQIps8jas8YN6EFAFn3mS1Ue1EPJeMdpc+u6VXkCA7c3VfpLPCU6/ZsGtTZhpMF5iUwcLttTiZ58WhEUR1OVz7n/91TBabcFcSbOfXFqYHrOUI8muV4Q2FDp2GZ2lURyOne7waynHe4f8BNejanbvx2Bbf/X1700Qa5GBAo7Fk="}
        """;
    public static PasswordResetDelivery Row() => new(42, 7, Email, TokenHash, 3, Envelope,
        DateTimeOffset.UtcNow.AddMinutes(30), null, 1, DateTimeOffset.UtcNow.AddMinutes(2));
    public static PasswordResetDeliveryOptions Options() => new(ServiceToken, Origin);

    public static string EncryptLink(string link)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("smart-factory:password-reset:key:v1:" + ServiceToken));
        var iv = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token = Token, resetUrl = link }));
        var data = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(iv, plaintext, data, tag, Encoding.UTF8.GetBytes("smart-factory:password-reset:v1:42:7:3:" + Email));
        return JsonSerializer.Serialize(new { v = 1, iv = Convert.ToBase64String(iv), tag = Convert.ToBase64String(tag), data = Convert.ToBase64String(data) });
    }
}

public class PasswordResetCryptoTests
{
    [Fact]
    public void DecryptsTheExactNodeGeneratedEnvelopeAndTokenHash()
    {
        Assert.True(PasswordResetFixture.Options().TryDecryptLink(PasswordResetFixture.Row(), out var link));
        Assert.Equal(PasswordResetFixture.Link, link);
    }

    [Fact]
    public void RejectsTamperingWrongRowIdentityAndWrongTokenHash()
    {
        var row = PasswordResetFixture.Row();
        foreach (var invalid in new[] {
            row with { Payload = row.Payload.Replace("ei8soCWO", "fi8soCWO", StringComparison.Ordinal) },
            row with { Id = 43 }, row with { UserId = 8 }, row with { AuthVersion = 4 },
            row with { Email = "other@example.test" }, row with { TokenHash = new string('0', 64) },
            row with { Payload = "{}" }, row with { Payload = "not-json" }
        })
        {
            Assert.False(PasswordResetFixture.Options().TryDecryptLink(invalid, out var link));
            Assert.Null(link);
        }
    }

    [Theory]
    [InlineData("https://attacker.example/#/reset-password?token=")]
    [InlineData("http://factory.example.test/#/reset-password?token=")]
    [InlineData("https://factory.example.test/other/#/reset-password?token=")]
    [InlineData("https://factory.example.test/#/reset-password?extra=1&token=")]
    public void RejectsAuthenticatedPayloadsWithAnUnexpectedResetUrl(string prefix)
    {
        var row = PasswordResetFixture.Row() with { Payload = PasswordResetFixture.EncryptLink(prefix + PasswordResetFixture.Token) };
        Assert.False(PasswordResetFixture.Options().TryDecryptLink(row, out _));
    }

    [Theory]
    [InlineData("https://factory.example.test", false, true)]
    [InlineData("https://factory.example.test/", false, true)]
    [InlineData("https://factory.example.test/path", false, false)]
    [InlineData("https://factory.example.test/?return=elsewhere", false, false)]
    [InlineData("https://factory.example.test/#anything", false, false)]
    [InlineData("https://user:password@factory.example.test", false, false)]
    [InlineData("http://127.0.0.1:3000", true, true)]
    [InlineData("http://localhost:3000/", true, true)]
    [InlineData("http://[::1]:3000", true, true)]
    [InlineData("http://127.0.0.1:3000", false, false)]
    [InlineData("http://factory.example.test", true, false)]
    [InlineData("", false, false)]
    public void RequiresACanonicalHttpsOriginWithOnlyExplicitDevelopmentLoopbackHttp(string input, bool development, bool allowed)
    {
        Assert.Equal(allowed, PasswordResetDeliveryOptions.CanonicalOrigin(input, development) is not null);
    }

    [Fact]
    public void RequiresACompleteKeyAndUsesTheExistingSingleOriginFallback()
    {
        Assert.False(new PasswordResetDeliveryOptions("short", PasswordResetFixture.Origin).Configured);
        Assert.False(new PasswordResetDeliveryOptions(PasswordResetFixture.ServiceToken, null).Configured);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["DASHBOARD_SERVICE_TOKEN"] = PasswordResetFixture.ServiceToken,
            ["ALLOWED_ORIGIN"] = PasswordResetFixture.Origin
        }).Build();
        Assert.True(PasswordResetDeliveryOptions.FromConfiguration(configuration, false).Configured);
    }

    [Fact]
    public void UsesADisjointProviderIdentityAndMakesNoMailboxDeliveryClaim()
    {
        Assert.Equal(-42, PasswordResetDeliveryPolicy.ProviderIdentity(42));
        Assert.Equal(-long.MaxValue, PasswordResetDeliveryPolicy.ProviderIdentity(long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordResetDeliveryPolicy.ProviderIdentity(0));
        var body = PasswordResetDeliveryPolicy.Body(PasswordResetFixture.Link, DateTimeOffset.Parse("2026-10-31T15:00:00Z"));
        Assert.Contains(PasswordResetFixture.Link, body);
        Assert.Contains("2026-10-31 15:00:00 UTC", body);
        Assert.Contains("If you did not request", body);
    }
}

public class PasswordResetQueueTests
{
    [Theory]
    [Trait("Category", "Postgres")]
    [InlineData("accepted", "accepted", 1, true)]
    [InlineData("expired", "expired", 0, true)]
    [InlineData("used", "blocked", 0, true)]
    [InlineData("revoked", "blocked", 0, true)]
    [InlineData("disabled", "blocked", 0, true)]
    [InlineData("changed-email", "blocked", 0, true)]
    [InlineData("shared-email", "blocked", 0, true)]
    [InlineData("shared-openid", "blocked", 0, true)]
    [InlineData("recipient-policy", "blocked", 0, true)]
    [InlineData("bad-payload", "blocked", 0, true)]
    [InlineData("unconfigured", "unconfigured", 0, false)]
    [InlineData("missing-key", "unconfigured", 0, false)]
    public async Task QueueChecksTheCurrentAccountAndNeverSendsDisallowedLinks(string scenario, string expected, int calls, bool cleared)
    {
        await using var fixture = await ResetDatabase.Create();
        var sender = new RecordingSender();
        var options = PasswordResetFixture.Options();
        switch (scenario)
        {
            case "expired": await fixture.Execute("UPDATE password_reset_tokens SET expires_at=now()-interval '1 second'"); break;
            case "used": await fixture.Execute("UPDATE password_reset_tokens SET used_at=now()"); break;
            case "revoked": await fixture.Execute("UPDATE users SET auth_version=4"); break;
            case "disabled": await fixture.Execute("UPDATE users SET disabled_at=now()"); break;
            case "changed-email": await fixture.Execute("UPDATE users SET email='changed@example.test'"); break;
            case "shared-email":
                await fixture.Execute("UPDATE users SET email='admin@dev.local'; UPDATE password_reset_tokens SET email='admin@dev.local'"); break;
            case "shared-openid": await fixture.Execute("UPDATE users SET \"openId\"='demo-renamed-account'"); break;
            case "recipient-policy": sender.Allowed = false; break;
            case "bad-payload": await fixture.Execute("UPDATE password_reset_tokens SET delivery_payload='{}'"); break;
            case "unconfigured": sender.Configured = false; break;
            case "missing-key": options = new PasswordResetDeliveryOptions("", PasswordResetFixture.Origin); break;
        }
        var worker = fixture.Worker(options);
        Assert.True(await worker.DeliverOne(sender, default));
        Assert.Equal(calls, sender.Messages.Count);
        var state = await fixture.State();
        Assert.Equal(expected, state.Status);
        Assert.Equal(cleared, state.Payload.Length == 0);
        Assert.Equal(PasswordResetFixture.TokenHash, state.Hash);
        if (expected == "accepted")
        {
            Assert.Null(state.UsedAt);
            Assert.Equal(-42, sender.Messages.Single().Id);
            Assert.Contains(PasswordResetFixture.Link, sender.Messages.Single().Body);
            Assert.False(await worker.DeliverOne(sender, default));
        }
        if (expected == "unconfigured") Assert.Equal(0, state.Attempts);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task RetryIsBoundedUsesTheSameProviderIdentityAndPersistsOnlySafeErrorCodes()
    {
        await using var fixture = await ResetDatabase.Create();
        var sender = new RecordingSender { Failure = new Exception("private SMTP error including a reset link must never persist") };
        var worker = fixture.Worker();
        for (var attempt = 1; attempt <= PasswordResetDeliveryPolicy.MaximumAttempts; attempt++)
        {
            await fixture.Execute("UPDATE password_reset_tokens SET next_attempt_at=now()");
            Assert.True(await worker.DeliverOne(sender, default));
            var state = await fixture.State();
            Assert.Equal(attempt, state.Attempts);
            Assert.Equal("failed", state.Status);
            Assert.Equal("delivery_failed", state.Error);
            Assert.Equal(attempt == PasswordResetDeliveryPolicy.MaximumAttempts, state.Payload.Length == 0);
        }
        Assert.False(await worker.DeliverOne(sender, default));
        Assert.Equal(4, sender.Messages.Count);
        Assert.All(sender.Messages, message => Assert.Equal(-42, message.Id));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ATransientFailureCanRecoverWithoutConsumingTheOneTimeToken()
    {
        await using var fixture = await ResetDatabase.Create();
        var sender = new RecordingSender { Failure = new HttpRequestException("synthetic timeout") };
        var worker = fixture.Worker();
        Assert.True(await worker.DeliverOne(sender, default));
        Assert.False(await worker.DeliverOne(sender, default)); // Retry is delayed.
        sender.Failure = null;
        await fixture.Execute("UPDATE password_reset_tokens SET next_attempt_at=now()");
        Assert.True(await worker.DeliverOne(sender, default));
        var state = await fixture.State();
        Assert.Equal("accepted", state.Status);
        Assert.Equal(2, state.Attempts);
        Assert.Equal("", state.Payload);
        Assert.Equal(PasswordResetFixture.TokenHash, state.Hash);
        Assert.Null(state.UsedAt);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task AnExpiredUnconfiguredPayloadIsErasedWithoutSending()
    {
        await using var fixture = await ResetDatabase.Create();
        var sender = new RecordingSender { Configured = false };
        var worker = fixture.Worker();
        Assert.True(await worker.DeliverOne(sender, default));
        Assert.False(await worker.DeliverOne(sender, default));
        await fixture.Execute("UPDATE password_reset_tokens SET expires_at=now()-interval '1 second'");
        Assert.True(await worker.DeliverOne(sender, default));
        var state = await fixture.State();
        Assert.Equal("expired", state.Status);
        Assert.Equal("", state.Payload);
        Assert.Empty(sender.Messages);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task AnotherWorkerCannotSendAnActivelyLeasedToken()
    {
        await using var fixture = await ResetDatabase.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new RecordingSender { DuringSend = async _ => { started.TrySetResult(); await release.Task; } };
        var first = fixture.Worker().DeliverOne(sender, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var other = new RecordingSender();
            Assert.False(await fixture.Worker().DeliverOne(other, default));
            Assert.Empty(other.Messages);
        }
        finally { release.TrySetResult(); }
        Assert.True(await first);
        Assert.Single(sender.Messages);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task AResetConsumedDuringMailDeliveryCannotBeResurrectedByCompletion()
    {
        await using var fixture = await ResetDatabase.Create();
        var sender = new RecordingSender {
            DuringSend = _ => fixture.Execute("UPDATE password_reset_tokens SET used_at=now(),delivery_payload='',delivery_status='blocked',last_error='consumed'")
        };
        Assert.True(await fixture.Worker().DeliverOne(sender, default));
        var state = await fixture.State();
        Assert.Equal("blocked", state.Status);
        Assert.Equal("consumed", state.Error);
        Assert.NotNull(state.UsedAt);
        Assert.Equal("", state.Payload);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task MissingMigrationKeepsTheWorkerAliveWithoutRepeatedLogsAndRecoversAfterUpgrade()
    {
        await using var fixture = await ResetDatabase.Create();
        await fixture.Execute("ALTER TABLE password_reset_tokens RENAME TO password_reset_tokens_waiting");
        var clock = new ManualDelayClock();
        var logger = new RecordingLogger();
        using var worker = fixture.Worker(logger: logger, clock: clock);
        await worker.StartAsync(default);
        try
        {
            var first = await clock.Next();
            Assert.Equal(TimeSpan.FromSeconds(30), first.Due);
            Assert.Single(logger.Messages);
            Assert.Equal("Password reset delivery is waiting for its database migration.", logger.Messages.Single());
            Assert.False(worker.ExecuteTask!.IsCompleted);
            first.Fire();
            var second = await clock.Next();
            Assert.Equal(TimeSpan.FromSeconds(30), second.Due);
            Assert.Single(logger.Messages);
            Assert.Empty(fixture.BackgroundSender.Messages);

            await fixture.Execute("ALTER TABLE password_reset_tokens_waiting RENAME TO password_reset_tokens");
            second.Fire();
            var recovered = await clock.Next();
            Assert.Equal(TimeSpan.FromSeconds(1), recovered.Due);
            Assert.Single(fixture.BackgroundSender.Messages);
            Assert.Equal("accepted", (await fixture.State()).Status);
            Assert.Single(logger.Messages);
        }
        finally { await worker.StopAsync(default); }
    }

    private sealed class RecordingLogger : ILogger<PasswordResetDeliveryWorker>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    private sealed class ManualDelayClock : TimeProvider
    {
        private readonly Channel<ManualTimer> timers = Channel.CreateUnbounded<ManualTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            timers.Writer.TryWrite(timer);
            return timer;
        }
        public Task<ManualTimer> Next() => timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private bool disposed;
            public TimeSpan Due { get; private set; } = due;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return !disposed; }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class RecordingSender : IEmailSender
    {
        public bool Configured { get; set; } = true;
        public string Provider => "Recording test sender";
        public bool Allowed { get; set; } = true;
        public Exception? Failure { get; set; }
        public Func<CancellationToken, Task>? DuringSend { get; set; }
        public List<(long Id, string Body)> Messages { get; } = [];
        public bool Allows(string? email) => Allowed && email == PasswordResetFixture.Email;
        public async Task Send(string recipient, string title, string body, long notificationId, CancellationToken ct)
        {
            Messages.Add((notificationId, body));
            if (DuringSend is not null) await DuringSend(ct);
            if (Failure is not null) throw Failure;
        }
    }

    private sealed record QueueState(string Status, string Payload, string Hash, int Attempts, string? Error, DateTimeOffset? UsedAt);

    private sealed class ResetDatabase : IAsyncDisposable
    {
        private readonly NpgsqlDataSource admin;
        private readonly string schema;
        private readonly ServiceProvider services;
        public RecordingSender BackgroundSender { get; } = new();
        public DashboardStore Store { get; }
        private ResetDatabase(NpgsqlDataSource admin, string schema, DashboardStore store)
        {
            (this.admin, this.schema, Store) = (admin, schema, store);
            services = new ServiceCollection().AddSingleton<IEmailSender>(BackgroundSender).BuildServiceProvider();
        }

        public static async Task<ResetDatabase> Create()
        {
            var connection = Environment.GetEnvironmentVariable("TEST_DASHBOARD_DATABASE_CONNECTION")
                ?? throw new InvalidOperationException("Disposable local _e2e storage is required.");
            var settings = new NpgsqlConnectionStringBuilder(connection);
            if (settings.Database?.EndsWith("_e2e", StringComparison.Ordinal) != true ||
                !new[] { "localhost", "127.0.0.1", "host.docker.internal", "database" }.Contains(settings.Host))
                throw new InvalidOperationException("Disposable local _e2e storage is required.");
            var schema = "password_reset_" + Guid.NewGuid().ToString("N") + "_e2e";
            var admin = NpgsqlDataSource.Create(connection);
            await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
            settings.SearchPath = schema;
            var fixture = new ResetDatabase(admin, schema, new DashboardStore(settings.ConnectionString, false));
            try
            {
                await fixture.Execute("""
                    CREATE TABLE users(id integer primary key,"openId" text,email text,auth_version integer,disabled_at timestamptz);
                    CREATE TABLE password_reset_tokens(
                      id bigint generated by default as identity primary key,user_id integer not null,
                      email varchar(320) not null,token_hash char(64) not null,auth_version integer not null,
                      delivery_payload text not null default '',expires_at timestamptz not null,used_at timestamptz,
                      created_at timestamptz not null default now(),
                      delivery_status text not null check(delivery_status in ('pending','accepted','failed','unconfigured','blocked','expired')),
                      attempts integer not null default 0,next_attempt_at timestamptz not null default now(),last_error text);
                    INSERT INTO users VALUES(7,'individual-reset-owner','person@example.test',3,null);
                    """);
                await using var insert = fixture.Store.Source.CreateCommand("""
                    INSERT INTO password_reset_tokens(id,user_id,email,token_hash,auth_version,delivery_payload,expires_at,delivery_status)
                    VALUES(42,7,$1,$2,3,$3,now()+interval '30 minutes','pending')
                    """);
                insert.Parameters.AddWithValue(PasswordResetFixture.Email);
                insert.Parameters.AddWithValue(PasswordResetFixture.TokenHash);
                insert.Parameters.AddWithValue(PasswordResetFixture.Envelope);
                await insert.ExecuteNonQueryAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public PasswordResetDeliveryWorker Worker(PasswordResetDeliveryOptions? options = null,
            ILogger<PasswordResetDeliveryWorker>? logger = null, TimeProvider? clock = null) => new(
            Store, services.GetRequiredService<IServiceScopeFactory>(), options ?? PasswordResetFixture.Options(),
            logger ?? NullLogger<PasswordResetDeliveryWorker>.Instance, clock);

        public async Task Execute(string sql)
        {
            await using var command = Store.Source.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<QueueState> State()
        {
            await using var command = Store.Source.CreateCommand("SELECT delivery_status,delivery_payload,token_hash,attempts,last_error,used_at FROM password_reset_tokens WHERE id=42");
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
        }

        public async ValueTask DisposeAsync()
        {
            Store.Dispose();
            services.Dispose();
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }
}
