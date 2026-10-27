namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// A mirrored panel inbound and its default-for-new-clients setting.
/// </summary>
public sealed record PanelInboundSetting(
    int InboundId,
    string? Remark,
    string? Tag,
    string? Protocol,
    int? Port,
    bool IsDefaultForNewClients,
    int SortOrder,
    DateTimeOffset LastSyncedAt
);
