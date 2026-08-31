using EagleTunnelApi.PanelApi.Models;

namespace EagleTunnelApi.Telegram;

public enum SubscriptionStatus
{
    Active,
    Disabled,
    Limited,
    Expired
}

public static class SubscriptionFormatter
{
    public static SubscriptionStatus DeriveStatus(PanelClient client, long usedTraffic)
    {
        if (!client.Enable)
        {
            return SubscriptionStatus.Disabled;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (client.ExpiryTime > 0 && client.ExpiryTime < now)
        {
            return SubscriptionStatus.Expired;
        }

        if (client.TotalGB > 0 && usedTraffic >= client.TotalGB)
        {
            return SubscriptionStatus.Limited;
        }

        return SubscriptionStatus.Active;
    }

    public static string GetStatusText(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Active => "🔥 Subscription: Active",
        SubscriptionStatus.Disabled => "📦 Subscription: Not Active",
        SubscriptionStatus.Limited => "🦥 Subscription: Limited",
        SubscriptionStatus.Expired => "🧟 Subscription: Expired",
        _ => "❓ Subscription: Unknown"
    };

    public static string FormatGigabytes(double bytes) =>
        (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00");

    public static string GetTrafficLimitStrategy(int reset) =>
        reset > 0 ? $"RESET_EVERY_{reset}_DAYS" : "UNLIMITED";

    public static string BuildSubscriptionUrl(string panelBaseUri, string subId)
    {
        var uri = new Uri(panelBaseUri);
        var host = $"{uri.Scheme}://{uri.Host}";
        return $"{host}:2096/add/{subId}";
    }
}
