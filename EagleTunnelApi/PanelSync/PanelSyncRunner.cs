using EagleTunnelApi.Application.Panel;

namespace EagleTunnelApi.PanelSync;

/// <summary>
/// Creates a fresh scope for an on-demand sync so singleton Telegram handlers
/// never capture the scoped sync service.
/// </summary>
public sealed class PanelSyncRunner(IServiceScopeFactory scopeFactory) : IPanelSyncRunner
{
    public async Task<PanelSyncResult> RunNowAsync(string owner, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<IPanelSync>();

        return await sync.RunAsync(PanelSyncMode.Cursor, owner, cancellationToken);
    }
}
