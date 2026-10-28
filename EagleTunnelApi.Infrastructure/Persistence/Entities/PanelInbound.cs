namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class PanelInbound
{
    public int InboundId { get; set; }
    public string? Remark { get; set; }
    public string? Tag { get; set; }
    public string? Protocol { get; set; }
    public int? Port { get; set; }
    public bool IsDefaultForNewClients { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}
