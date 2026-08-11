using EagleTunnelApi.TributeShop;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace EagleTunnelApi.Telegram;

public static class MenuService
{
    public const string Subscribe = "subscribe";
    public const string Connect = "connect";
    public const string Support = "support";
    public const string Back = "back";

    public static InlineKeyboardMarkup MainMenu(SubscriptionStatus? status, string? subscriptionUrl)
    {
        var isActive = status == SubscriptionStatus.Active && !string.IsNullOrEmpty(subscriptionUrl);
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        if (isActive)
        {
            rows.Add([InlineKeyboardButton.WithCopyText("📋 Copy VPN Link", new CopyTextButton { Text = subscriptionUrl! })]);
            rows.Add([InlineKeyboardButton.WithCallbackData("🚀 How to Connect", Connect)]);
            rows.Add([InlineKeyboardButton.WithCallbackData("💳 Manage Subscription", Subscribe)]);
        }
        else
        {
            rows.Add([InlineKeyboardButton.WithCallbackData("💳 Get VPN", Subscribe)]);
        }

        rows.Add([InlineKeyboardButton.WithCallbackData("💬 Support", Support)]);

        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup SubscriptionMenu()
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        foreach (var plan in SubscriptionPlans.All)
        {
            var recurring = plan.IsRecurring ? "🔄" : "⚡";
            rows.Add([
                InlineKeyboardButton.WithCallbackData(
                    $"{recurring} {plan.Title} — {plan.Description}",
                    $"{SubscriptionPlans.PlanPrefix}{plan.TributePeriod}")
            ]);
        }

        rows.Add([InlineKeyboardButton.WithCallbackData("🔙 Back", Back)]);

        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup PaymentMenu(string? paymentUrl, string? webappPaymentUrl = null)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        if (!string.IsNullOrEmpty(webappPaymentUrl))
        {
            rows.Add([InlineKeyboardButton.WithUrl("📱 Pay in Telegram", webappPaymentUrl)]);
        }

        if (!string.IsNullOrEmpty(paymentUrl))
        {
            rows.Add([InlineKeyboardButton.WithUrl("💳 Pay Now", paymentUrl)]);
        }

        rows.Add([InlineKeyboardButton.WithCallbackData("🔙 Back", Subscribe)]);

        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup ConnectMenu(string subscriptionUrl) => new(
    [
        [InlineKeyboardButton.WithCopyText("📋 Copy VPN Link", new CopyTextButton { Text = subscriptionUrl })],
        [InlineKeyboardButton.WithCallbackData("🔙 Back", Back)]
    ]);

    public static InlineKeyboardMarkup SupportMenu(string supportUrl, string prefillText)
    {
        var separator = supportUrl.Contains('?') ? "&" : "?";
        var link = $"{supportUrl}{separator}text={Uri.EscapeDataString(prefillText)}";

        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithUrl("📩 Message Support", link)],
            [InlineKeyboardButton.WithCallbackData("🔙 Back", Back)]
        ]);
    }
}
