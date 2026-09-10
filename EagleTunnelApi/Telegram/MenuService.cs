using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace EagleTunnelApi.Telegram;

public static class MenuService
{
    public const string Connect = "connect";
    public const string Support = "support";
    public const string Back = "back";
    public const string Referral = "referral";
    public const string RefConfirm = "ref:confirm";
    public const string RefEdit = "ref:edit";
    public const string RefSkip = "ref:skip";
    public const string FriendRegister = "friend:register";

    public const string Admin = "admin";
    public const string AdminLookup = "admin:lookup";
    public const string AdminList = "admin:list";
    public const string AdminGrant = "admin:grant";
    public const string AdminBan = "admin:ban";
    public const string AdminUnban = "admin:unban";
    public const string AdminLimit = "admin:limit";
    public const string AdminReset = "admin:reset";
    public const string AdminLink = "admin:link";
    public const string AdminNudge = "admin:nudge";
    public const string AdminRegister = "admin:register";
    public const string AdminChangeEmail = "admin:change-email";
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
    public const string AdminConfirmNudge = "admin:confirm:nudge";
    public const string AdminConfirmChangeEmail = "admin:confirm:change-email";
    public const string AdminPick = "admin:pick";

    public static InlineKeyboardMarkup MainMenu(SubscriptionStatus? status, string? subscriptionUrl,
        string tributeSubscriptionUrl, bool isAdmin = false)
    {
        var isActive = status == SubscriptionStatus.Active && !string.IsNullOrEmpty(subscriptionUrl);
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        if (isActive)
        {
            rows.Add([InlineKeyboardButton.WithUrl("⚙️ Manage Subscription", tributeSubscriptionUrl)]);
            rows.Add([InlineKeyboardButton.WithCallbackData("🚀 How to Connect", Connect)]);
            rows.Add([
                InlineKeyboardButton.WithCopyText("📋 Copy VPN Link", new CopyTextButton { Text = subscriptionUrl! })
            ]);
        }
        else
        {
            rows.Add([InlineKeyboardButton.WithUrl("💳 Subscribe", tributeSubscriptionUrl)]);
        }

        rows.Add([InlineKeyboardButton.WithCallbackData("💬 Support", Support)]);
        rows.Add([InlineKeyboardButton.WithCallbackData("🎁 Invite Friends — Get 1 Month Free", Referral)]);

        if (isAdmin) rows.Add([InlineKeyboardButton.WithCallbackData("🛠 Admin", Admin)]);

        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup ConnectMenu(string subscriptionUrl)
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCopyText("📋 Copy VPN Link", new CopyTextButton { Text = subscriptionUrl })],
            [InlineKeyboardButton.WithCallbackData("🔙 Back", Back)]
        ]);
    }

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

    public static InlineKeyboardMarkup AdminMenu()
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("🔍 Look Up User", AdminLookup)],
            [InlineKeyboardButton.WithCallbackData("📜 List All Clients", AdminList)],
            [InlineKeyboardButton.WithCallbackData("➕ Grant / Extend", AdminGrant)],
            [
                InlineKeyboardButton.WithCallbackData("🚫 Ban", AdminBan),
                InlineKeyboardButton.WithCallbackData("✅ Unban", AdminUnban)
            ],
            [InlineKeyboardButton.WithCallbackData("📱 Device Limit", AdminLimit)],
            [InlineKeyboardButton.WithCallbackData("♻️ Reset Traffic", AdminReset)],
            [InlineKeyboardButton.WithCallbackData("🔗 Link Account", AdminLink)],
            [InlineKeyboardButton.WithCallbackData("➕ Register Account", AdminRegister)],
            [InlineKeyboardButton.WithCallbackData("✏️ Change Email", AdminChangeEmail)],
            [InlineKeyboardButton.WithCallbackData("📨 Send Reminder", AdminNudge)],
            [InlineKeyboardButton.WithCallbackData("❌ Exit Admin", AdminExit)]
        ]);
    }

    public static InlineKeyboardMarkup AdminInputMenu()
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("❌ Cancel", AdminCancel)]
        ]);
    }

    public static InlineKeyboardMarkup AdminConfirmMenu(string action, string target)
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("✅ Confirm", $"{action}:{target}")],
            [InlineKeyboardButton.WithCallbackData("❌ Cancel", AdminCancel)]
        ]);
    }

    public static InlineKeyboardMarkup AdminListMenu(int currentPage, int totalPages)
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("⬅️", $"{AdminListPrev}:{currentPage - 1}"),
                InlineKeyboardButton.WithCallbackData($"{currentPage + 1}/{totalPages}", Admin),
                InlineKeyboardButton.WithCallbackData("➡️", $"{AdminListNext}:{currentPage + 1}")
            ],
            [InlineKeyboardButton.WithCallbackData("❌ Exit Admin", AdminExit)]
        ]);
    }

    public static InlineKeyboardMarkup ReferralMenu(string referralLink, string supportUrl)
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCopyText("📋 Copy Invite Link", new CopyTextButton { Text = referralLink })],
            [InlineKeyboardButton.WithCallbackData("➕ Register a Friend", FriendRegister)],
            [InlineKeyboardButton.WithUrl("📹 Cancel Guide? Message Support", supportUrl)],
            [InlineKeyboardButton.WithCallbackData("🔙 Back", Back)]
        ]);
    }

    public static InlineKeyboardMarkup RefConfirmMenu()
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Confirm", RefConfirm),
                InlineKeyboardButton.WithCallbackData("✏️ Edit", RefEdit)
            ],
            [InlineKeyboardButton.WithCallbackData("⏭️ Skip", RefSkip)]
        ]);
    }

    public static InlineKeyboardMarkup ReferralInputMenu()
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("⏭️ Skip", RefSkip)]
        ]);
    }
}