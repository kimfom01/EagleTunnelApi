using EagleTunnelApi.PanelApi.Models;

namespace EagleTunnelApi.PanelApi;

public static class PanelClientDefaults
{
    public const long TotalGigabytes = 300L * 1024 * 1024 * 1024;

    public const string VisionFlow = "xtls-rprx-vision";

    public const int HwidLimit = 2;

    public const string TrafficReset = "monthly";

    public const int TrafficResetDay = 1;

    public const int CredentialsLength = 16;

    public static CreateClientRequest CreateClient(string email, bool enable, long expiryTimeMs, long tgId,
        string? comment) => new(
        Email: email,
        Enable: enable,
        ExpiryTime: expiryTimeMs,
        TotalGB: TotalGigabytes,
        TgId: tgId,
        Comment: comment,
        LimitHwid: HwidLimit,
        TrafficReset: TrafficReset,
        TrafficResetDay: TrafficResetDay,
        SubId: RandomString.LowerAndNum(CredentialsLength),
        Password: RandomString.LowerAndNum(CredentialsLength),
        Auth: RandomString.LowerAndNum(CredentialsLength),
        Flow: VisionFlow
    );
}
