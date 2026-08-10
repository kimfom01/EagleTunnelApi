using EagleTunnelApi.PanelApi;

namespace EagleTunnelApi.Telegram;

public sealed record UserDetails(
    string Uuid,
    int Id,
    string SubId,
    string Username,
    SubscriptionStatus Status,
    long TrafficLimitBytes,
    string TrafficLimitStrategy,
    string ExpireAt,
    long TelegramId,
    int HwidDeviceLimit,
    string SubscriptionUrl,
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

        return new UserDetails(
            Uuid: client.Uuid,
            Id: client.Id,
            SubId: client.SubId,
            Username: client.Email,
            Status: status,
            TrafficLimitBytes: client.TotalGB,
            TrafficLimitStrategy: SubscriptionFormatter.GetTrafficLimitStrategy(client.Reset),
            ExpireAt: client.ExpiryTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(client.ExpiryTime).ToUniversalTime().ToString("o")
                : DateTimeOffset.UtcNow.AddYears(100).ToString("o"),
            TelegramId: client.TgId,
            HwidDeviceLimit: client.LimitIp,
            SubscriptionUrl: SubscriptionFormatter.BuildSubscriptionUrl(panelBaseUri, client.SubId),
            UsedTrafficBytes: response.UsedTraffic
        );
    }
}
