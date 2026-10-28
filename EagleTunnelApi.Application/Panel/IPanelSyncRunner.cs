namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Runs an on-demand panel sync from singleton UI code. Implementations create
/// a fresh scope for the scoped <see cref="IPanelSync"/>.
/// </summary>
public interface IPanelSyncRunner
{
    Task<PanelSyncResult> RunNowAsync(string owner, CancellationToken cancellationToken);
}
