using Microsoft.Extensions.Hosting;

namespace EagleTunnelApi.Tests.Helpers;

public sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
        if (!_stopping.IsCancellationRequested) _stopping.Cancel();
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }
}
