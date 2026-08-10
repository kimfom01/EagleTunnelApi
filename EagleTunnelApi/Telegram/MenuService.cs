using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace EagleTunnelApi.Telegram;

public static class MenuService
{
    public const string StartRegistration = "start_registration";
    public const string Setup = "setup";
    public const string SetupInstall = "setup_install";
    public const string SetupImport = "setup_import";
    public const string SetupConnect = "setup_connect";
    public const string PreSupport = "pre_support";
    public const string PreSupportYes = "pre_support_yes";
    public const string PreSupportNo = "pre_support_no";
    public const string Support = "support";
    public const string Back = "back";

    public static InlineKeyboardMarkup StartRegistrationMenu() => new(
    [
        [InlineKeyboardButton.WithCallbackData("📝 Start Registration", StartRegistration)]
    ]);

    public static InlineKeyboardMarkup MainMenu(SubscriptionStatus? status, string? subscriptionUrl,
        string tributeSubscriptionUrl)
    {
        var isActive = status == SubscriptionStatus.Active && !string.IsNullOrEmpty(subscriptionUrl);
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        if (isActive)
        {
            rows.Add([InlineKeyboardButton.WithUrl("⚙️ Manage Subscription", tributeSubscriptionUrl)]);
            rows.Add([InlineKeyboardButton.WithCallbackData("📱 Setup VPN", Setup)]);
            rows.Add([InlineKeyboardButton.WithCopyText("📋 Copy VPN Link", new CopyTextButton { Text = subscriptionUrl! })]);
        }
        else
        {
            rows.Add([InlineKeyboardButton.WithUrl("💳 Subscribe", tributeSubscriptionUrl)]);
        }

        rows.Add([InlineKeyboardButton.WithCallbackData("💬 Support", PreSupport)]);

        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup SetupMenu() => new(
    [
        [InlineKeyboardButton.WithCallbackData("📲 Install INCY", SetupInstall)],
        [InlineKeyboardButton.WithCallbackData("🔗 Import Subscription", SetupImport)],
        [InlineKeyboardButton.WithCallbackData("🚀 Connect to VPN", SetupConnect)],
        [InlineKeyboardButton.WithCallbackData("🔙 Back", Back)]
    ]);

    public static InlineKeyboardMarkup PreSupportMenu() => new(
    [
        [InlineKeyboardButton.WithCallbackData("✅ Yes, I rebooted my phone", PreSupportYes)],
        [InlineKeyboardButton.WithCallbackData("❌ No, I will reboot my phone", PreSupportNo)],
        [InlineKeyboardButton.WithCallbackData("🔙 Back", Back)]
    ]);

    public static InlineKeyboardMarkup SupportMenu(string supportUrl) => new(
    [
        [InlineKeyboardButton.WithUrl("📩 Message Support", supportUrl)],
        [InlineKeyboardButton.WithCallbackData("🔙 Back", Back)]
    ]);
}
