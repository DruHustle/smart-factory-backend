namespace SmartFactory.Services.TelemetryService.Infrastructure.Services;

/// <summary>Coalesces committed acquisitions without retaining one notification per sample.</summary>
public sealed class TelemetryDeliveryWakeup : IDisposable
{
    private readonly SemaphoreSlim _pending = new(0, 1);

    public void Notify()
    {
        try { _pending.Release(); }
        catch (SemaphoreFullException) { /* A delivery check is already requested. */ }
    }

    public Task<bool> WaitAsync(TimeSpan delay, CancellationToken cancellation) =>
        _pending.WaitAsync(delay, cancellation);

    public void Dispose() => _pending.Dispose();
}
