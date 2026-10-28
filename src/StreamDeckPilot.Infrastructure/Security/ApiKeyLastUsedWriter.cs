using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StreamDeckPilot.Infrastructure.Security;

/// <summary>
/// Writes API keys' last-used times (#98) off the request path: <see cref="ApiKeyStore.Touch"/>
/// records them in memory and signals; this writes them, and once more at shutdown.
/// </summary>
public sealed class ApiKeyLastUsedWriter(ApiKeyStore store, ILogger<ApiKeyLastUsedWriter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in store.FlushRequests.ReadAllAsync(stoppingToken))
                await FlushAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        try { await store.FlushLastUsedAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not write API keys' last-used times; will retry with the next use");
        }
    }
}
