namespace EagleTunnelApi.Application.Panel;

public sealed record PanelClientDetails(
    PanelClient Client,
    IReadOnlyList<string>? ExternalLinks,
    IReadOnlyList<int>? InboundIds,
    long UsedTraffic
)
{
    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}
