namespace EagleTunnelApi.Application.Panel;

public static class PanelClientDefaults
{
    public const long TotalGigabytes = 300L * 1024 * 1024 * 1024;

    public const string VisionFlow = "xtls-rprx-vision";

    public const int HwidLimit = 2;

    public const string TrafficReset = "monthly";

    public const int TrafficResetDay = 1;

    public const int CredentialsLength = 16;

    public static PanelClientCreate CreateClient(string email, bool enable, long expiryTimeMs, long tgId,
        string? comment, IReadOnlyList<int> inboundIds)
    {
        return new PanelClientCreate(
            email,
            enable,
            expiryTimeMs,
            TotalGigabytes,
            tgId,
            comment,
            HwidLimit,
            TrafficReset,
            TrafficResetDay,
            RandomString.LowerAndNum(CredentialsLength),
            RandomString.LowerAndNum(CredentialsLength),
            RandomString.LowerAndNum(CredentialsLength),
            VisionFlow,
            inboundIds
        );
    }
}
