namespace EagleTunnelApi.Application.Panel;

public sealed record PanelClient(
    string Uuid,
    string Email,
    bool Enable,
    long ExpiryTime,
    long TgId,
    long TotalGB,
    string? Comment,
    int LimitIp,
    int LimitHwid,
    string TrafficReset,
    int TrafficResetDay,
    int Reset,
    string? Security,
    string? SubId,
    string? Flow,
    long Id,
    IReadOnlyList<int>? InboundIds
);

public static class PanelClientExtensions
{
    public static PanelClientUpdate ToUpdateRequest(this PanelClient client)
    {
        return new PanelClientUpdate(client.Email, client.Enable, client.ExpiryTime, client.TotalGB, client.TgId,
            client.Comment,
            client.LimitIp, client.LimitHwid, client.TrafficReset, client.TrafficResetDay,
            client.Reset, client.Security, client.SubId, client.Flow, client.InboundIds);
    }

    public static PanelClientCreate ToCreateRequest(this PanelClientUpdate update, IReadOnlyList<int> inboundIds)
    {
        return new PanelClientCreate(
            update.Email,
            update.Enable,
            update.ExpiryTime,
            update.TotalGB,
            update.TgId,
            update.Comment,
            update.LimitHwid,
            update.TrafficReset,
            update.TrafficResetDay,
            update.SubId ?? RandomString.LowerAndNum(PanelClientDefaults.CredentialsLength),
            RandomString.LowerAndNum(PanelClientDefaults.CredentialsLength),
            RandomString.LowerAndNum(PanelClientDefaults.CredentialsLength),
            update.Flow ?? PanelClientDefaults.VisionFlow,
            inboundIds);
    }
}
