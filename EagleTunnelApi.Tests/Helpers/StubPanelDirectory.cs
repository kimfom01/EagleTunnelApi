using EagleTunnelApi.Application.Panel;

namespace EagleTunnelApi.Tests.Helpers;

/// <summary>
/// Minimal <see cref="IPanelDirectory"/> test double for code paths that only
/// need the effective default inbound ids.
/// </summary>
public sealed class StubPanelDirectory : IPanelDirectory
{
    public IReadOnlyList<int> DefaultInboundIds { get; set; } = [1, 2];

    public Func<CancellationToken, Task<IReadOnlyList<int>>>? DefaultInboundIdsFactory { get; set; }

    public int DefaultInboundIdsCalls { get; private set; }

    public Task<IReadOnlyList<int>> GetDefaultInboundIdsAsync(CancellationToken cancellationToken)
    {
        DefaultInboundIdsCalls++;

        return DefaultInboundIdsFactory is null
            ? Task.FromResult(DefaultInboundIds)
            : DefaultInboundIdsFactory(cancellationToken);
    }

    public Task<PanelMirrorClient?> GetClientByEmailAsync(string email,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PanelSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<PanelSyncConflictView>> GetSyncConflictsAsync(string? status,
        int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ResolveSyncConflictAsync(Guid conflictId, string status, string actor,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<PanelInboundSetting>> GetInboundSettingsAsync(
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetDefaultInboundsAsync(IReadOnlyList<int> inboundIds,
        IReadOnlyList<PanelInboundOption> availableInbounds, string actor,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
