using EagleTunnelApi.PanelApi.Models;

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
    long UsedTrafficBytes)
{
    public static UserDetails? From(PanelClientResponse? response, string panelBaseUri)
    {
        if (response is null)
        {
            return null;
        }

        var client = response.Client;
        var status = SubscriptionFormatter.DeriveStatus(client, response.UsedTraffic);
        var subId = client.SubId ?? "";

        return new UserDetails(
            Uuid: client.Uuid,
            Id: client.Id,
            SubId: subId,
            Username: client.Email,
            Status: status,
            TrafficLimitBytes: client.TotalGB,
            TrafficLimitStrategy: SubscriptionFormatter.GetTrafficLimitStrategy(client.Reset),
            ExpireAt: client.ExpiryTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(client.ExpiryTime).ToUniversalTime()
                : DateTimeOffset.UtcNow.AddYears(100),
            TelegramId: client.TgId,
            HwidDeviceLimit: client.LimitHwid,
            SubscriptionUrl: SubscriptionFormatter.BuildSubscriptionUrl(panelBaseUri, subId),
            TrafficReset: client.TrafficReset,
            TrafficResetDay: client.TrafficResetDay,
            UsedTrafficBytes: response.UsedTraffic
        );
    }
}
