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

    public const string Admin = "admin";
    public const string AdminLookup = "admin:lookup";
    public const string AdminList = "admin:list";
    public const string AdminGrant = "admin:grant";
    public const string AdminBan = "admin:ban";
    public const string AdminUnban = "admin:unban";
public const string AdminLimit = "admin:limit";
    public const string AdminReset = "admin:reset";
    public const string AdminLink = "admin:link";
    public const string AdminExit = "admin:exit";
    public const string AdminCancel = "admin:cancel";

    public const string AdminListPrev = "admin:list:prev";
    public const string AdminListNext = "admin:list:next";

    public const string AdminConfirmGrant = "admin:confirm:grant";
    public const string AdminConfirmBan = "admin:confirm:ban";
    public const string AdminConfirmUnban = "admin:confirm:unban";
    public const string AdminConfirmLimit = "admin:confirm:limit";
    public const string AdminConfirmReset = "admin:confirm:reset";
    public const string AdminConfirmLink = "admin:confirm:link";

    public static InlineKeyboardMarkup MainMenu(SubscriptionStatus? status, string? subscriptionUrl, bool isAdmin = false)
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

        if (isAdmin)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData("🛠 Admin", Admin)]);
        }

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

    public static InlineKeyboardMarkup AdminMenu() => new(
    [
        [InlineKeyboardButton.WithCallbackData("🔍 Look Up User", AdminLookup)],
        [InlineKeyboardButton.WithCallbackData("📜 List All Clients", AdminList)],
        [InlineKeyboardButton.WithCallbackData("➕ Grant / Extend", AdminGrant)],
        [InlineKeyboardButton.WithCallbackData("🚫 Ban", AdminBan), InlineKeyboardButton.WithCallbackData("✅ Unban", AdminUnban)],
        [InlineKeyboardButton.WithCallbackData("📱 Device Limit", AdminLimit)],
        [InlineKeyboardButton.WithCallbackData("♻️ Reset Traffic", AdminReset)],
        [InlineKeyboardButton.WithCallbackData("🔗 Link Account", AdminLink)],
        [InlineKeyboardButton.WithCallbackData("❌ Exit Admin", AdminExit)]
    ]);

    public static InlineKeyboardMarkup AdminInputMenu() => new(
    [
        [InlineKeyboardButton.WithCallbackData("❌ Cancel", AdminCancel)]
    ]);

    public static InlineKeyboardMarkup AdminConfirmMenu(string action, string target) => new(
    [
        [InlineKeyboardButton.WithCallbackData("✅ Confirm", $"{action}:{target}")],
        [InlineKeyboardButton.WithCallbackData("❌ Cancel", AdminCancel)]
    ]);

    public static InlineKeyboardMarkup AdminListMenu(int currentPage, int totalPages) => new(
    [
        [
            InlineKeyboardButton.WithCallbackData("⬅️", $"{AdminListPrev}:{currentPage - 1}"),
            InlineKeyboardButton.WithCallbackData($"{currentPage + 1}/{totalPages}", Admin),
            InlineKeyboardButton.WithCallbackData("➡️", $"{AdminListNext}:{currentPage + 1}")
        ],
        [InlineKeyboardButton.WithCallbackData("❌ Exit Admin", AdminExit)]
    ]);
}
