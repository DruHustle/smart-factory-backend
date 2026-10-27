using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFactory.Services.TelemetryService;
using SmartFactory.Services.TelemetryService.Infrastructure.Data;
using SmartFactory.Services.TelemetryService.Infrastructure.Services;

namespace SmartFactory.Tests;

public sealed class TelemetryDeliveryWakeupTests
{
    [Fact]
    public async Task BurstOfSamplesRetainsOnlyOnePendingWakeup()
    {
        using var wakeup = new TelemetryDeliveryWakeup();
        for (var i = 0; i < 10000; i++) wakeup.Notify();
        Assert.True(await wakeup.WaitAsync(TimeSpan.Zero, default));
        Assert.False(await wakeup.WaitAsync(TimeSpan.Zero, default));
    }

    [Fact]
    public async Task NewCommittedSampleInterruptsAnIdleWait()
    {
        using var wakeup = new TelemetryDeliveryWakeup();
        using var database = new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var waiting = wakeup.WaitAsync(TimeSpan.FromMinutes(5), default);
        Assert.False(waiting.IsCompleted);
        var function = new TelemetryFunction(new LoggerFactory(), database, deliveryWakeup: wakeup);
        var message = System.Text.Json.JsonSerializer.Serialize(new {
            deviceId = "wake-test", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), temperature = 25,
        });
        Assert.NotNull(await function.Run(message));
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Single(await database.TelemetryRecords.ToListAsync());
        Assert.False(await wakeup.WaitAsync(TimeSpan.Zero, default));
    }

    [Fact]
    public async Task RejectedMessageDoesNotWakeDelivery()
    {
        using var wakeup = new TelemetryDeliveryWakeup();
        using var database = new TelemetryDbContext(new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var function = new TelemetryFunction(new LoggerFactory(), database, deliveryWakeup: wakeup);
        Assert.Null(await function.Run("not valid JSON"));
        Assert.False(await wakeup.WaitAsync(TimeSpan.Zero, default));
        Assert.Empty(database.TelemetryRecords);
    }

    [Fact]
    public async Task CancellationStopsAnIdleWait()
    {
        using var wakeup = new TelemetryDeliveryWakeup();
        using var cancellation = new CancellationTokenSource();
        var waiting = wakeup.WaitAsync(TimeSpan.FromMinutes(5), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
