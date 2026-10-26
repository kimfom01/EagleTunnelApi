using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Domain.Referrals;
using EagleTunnelApi.Domain.Subscriptions;

namespace EagleTunnelApi.Telegram;

public sealed record UserDetails(
    string Uuid,
    int Id,
    string SubId,
    string Username,
    SubscriptionStatus Status,
    long TrafficLimitBytes,
    string TrafficLimitStrategy,
    DateTimeOffset ExpireAt,
    long TelegramId,
    int HwidDeviceLimit,
    string SubscriptionUrl,
    string TrafficReset,
    int TrafficResetDay,
    IReadOnlyList<int>? InboundIds,
    long UsedTrafficBytes,
    bool Enable,
    long ExpiryTimeMs,
    string? Comment)
{
    public static UserDetails? From(PanelClientDetails? response, string panelBaseUri)
    {
        if (response is null) return null;

        var client = response.Client;
        var status = SubscriptionFormatter.DeriveStatus(client.Enable, client.ExpiryTime, client.TotalGB,
            response.UsedTraffic, DateTimeOffset.UtcNow);
        var subId = client.SubId ?? "";

        return new UserDetails(
            client.Uuid,
            client.Id,
            subId,
            client.Email,
            status,
            client.TotalGB,
            SubscriptionFormatter.GetTrafficLimitStrategy(client.Reset),
            client.ExpiryTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(client.ExpiryTime).ToUniversalTime()
                : DateTimeOffset.UtcNow.AddYears(100),
            client.TgId,
            client.LimitHwid,
            SubscriptionFormatter.BuildSubscriptionUrl(panelBaseUri, subId),
            client.TrafficReset,
            client.TrafficResetDay,
            response.InboundIds,
            response.UsedTraffic,
            client.Enable,
            client.ExpiryTime,
            client.Comment
        );
    }

    public bool HasConvertedToPaid()
    {
        return ReferralService.HasConvertedToPaid(Enable, ExpiryTimeMs, DateTimeOffset.UtcNow, Comment);
    }
}