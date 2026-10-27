namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Configured default inbound ids used only until the database has an
/// authoritative default set. Production composition sources this from
/// <c>Telegram:DefaultInboundIds</c>.
/// </summary>
public sealed record PanelDirectoryFallback(IReadOnlyList<int> DefaultInboundIds)
{
    public static PanelDirectoryFallback Empty { get; } = new([]);
}
