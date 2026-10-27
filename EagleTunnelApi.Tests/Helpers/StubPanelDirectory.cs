using EagleTunnelApi.Application.Panel;

namespace EagleTunnelApi.Tests.Helpers;

/// <summary>
/// In-memory <see cref="IPanelDirectory"/> test double for code paths that need
/// effective defaults, sync status, conflicts, or inbound settings.
/// </summary>
public sealed class StubPanelDirectory : IPanelDirectory
{
    public IReadOnlyList<int> DefaultInboundIds { get; set; } = [1, 2];

    public Func<CancellationToken, Task<IReadOnlyList<int>>>? DefaultInboundIdsFactory { get; set; }

    public int DefaultInboundIdsCalls { get; private set; }

    public PanelSyncStatus SyncStatus { get; set; } = new();

    public List<PanelSyncConflictView> Conflicts { get; } = [];

    public List<PanelInboundSetting> InboundSettings { get; } = [];

    public List<(Guid ConflictId, string Status, string Actor)> ResolveCalls { get; } = [];

    public List<SetDefaultInboundsCall> SetDefaultInboundsCalls { get; } = [];

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
        Task.FromResult(SyncStatus);

    public Task<IReadOnlyList<PanelSyncConflictView>> GetSyncConflictsAsync(string? status,
        int limit, CancellationToken cancellationToken)
    {
        var effectiveStatus = status ?? PanelSyncConflictStates.Open;

        IReadOnlyList<PanelSyncConflictView> conflicts = Conflicts
            .Where(conflict => conflict.Status == effectiveStatus)
            .OrderByDescending(conflict => conflict.LastSeenAt)
            .Take(limit)
            .ToArray();

        return Task.FromResult(conflicts);
    }

    public Task ResolveSyncConflictAsync(Guid conflictId, string status, string actor,
        CancellationToken cancellationToken)
    {
        ResolveCalls.Add((conflictId, status, actor));

        var index = Conflicts.FindIndex(conflict => conflict.Id == conflictId);
        if (index >= 0)
            Conflicts[index] = Conflicts[index] with
            {
                Status = status,
                ResolvedAt = DateTimeOffset.UtcNow,
                ResolvedBy = actor
            };

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PanelInboundSetting>> GetInboundSettingsAsync(
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PanelInboundSetting>>(InboundSettings);

    public Task SetDefaultInboundsAsync(IReadOnlyList<int> inboundIds,
        IReadOnlyList<PanelInboundOption> availableInbounds, string actor,
        CancellationToken cancellationToken)
    {
        var selected = inboundIds.Distinct().ToArray();

        if (selected.Length == 0)
            throw new ArgumentException("At least one default inbound is required for new clients.");

        SetDefaultInboundsCalls.Add(new SetDefaultInboundsCall(selected, availableInbounds, actor));
        DefaultInboundIds = selected;

        for (var index = 0; index < InboundSettings.Count; index++)
        {
            var setting = InboundSettings[index];
            var selectedIndex = Array.IndexOf(selected, setting.InboundId);

            InboundSettings[index] = setting with
            {
                IsDefaultForNewClients = selectedIndex >= 0,
                SortOrder = selectedIndex >= 0 ? selectedIndex : 0
            };
        }

        return Task.CompletedTask;
    }

    public sealed record SetDefaultInboundsCall(IReadOnlyList<int> InboundIds,
        IReadOnlyList<PanelInboundOption> AvailableInbounds, string Actor);
}
