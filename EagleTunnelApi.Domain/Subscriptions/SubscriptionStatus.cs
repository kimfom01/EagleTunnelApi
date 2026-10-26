namespace EagleTunnelApi.Domain.Subscriptions;

public enum SubscriptionStatus
{
    Active,
    Disabled,
    Limited,
    Expired
}

public static class SubscriptionFormatter
{
    public static SubscriptionStatus DeriveStatus(bool enable, long expiryTimeMs, long totalBytes, long usedTraffic,
        DateTimeOffset now)
    {
        if (!enable) return SubscriptionStatus.Disabled;

        var nowMs = now.ToUnixTimeMilliseconds();

        if (expiryTimeMs > 0 && expiryTimeMs < nowMs) return SubscriptionStatus.Expired;

        if (totalBytes > 0 && usedTraffic >= totalBytes) return SubscriptionStatus.Limited;

        return SubscriptionStatus.Active;
    }

    public static string GetStatusText(SubscriptionStatus status)
    {
        return status switch
        {
            SubscriptionStatus.Active => "🔥 Subscription: Active",
            SubscriptionStatus.Disabled => "📦 Subscription: Not Active",
            SubscriptionStatus.Limited => "🦥 Subscription: Limited",
            SubscriptionStatus.Expired => "🧟 Subscription: Expired",
            _ => "❓ Subscription: Unknown"
        };
    }

    public static string FormatGigabytes(double bytes)
    {
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00");
    }

    public static string GetTrafficLimitStrategy(int reset)
    {
        return reset > 0 ? $"RESET_EVERY_{reset}_DAYS" : "UNLIMITED";
    }

    public static string BuildSubscriptionUrl(string panelBaseUri, string subId)
    {
        var uri = new Uri(panelBaseUri);
        var host = $"{uri.Scheme}://{uri.Host}";
        return $"{host}:2096/add/{subId}";
    }
}