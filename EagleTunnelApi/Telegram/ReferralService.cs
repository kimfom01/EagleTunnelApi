using System.Net.Mail;
using System.Text.RegularExpressions;
using EagleTunnelApi.PanelApi.Models;

namespace EagleTunnelApi.Telegram;

public static partial class ReferralService
{
    public const string RefPayloadPrefix = "ref_";

    public const string ReferredByTag = "Referred by: ";

    public const string PendingReferredByTag = "Referred by (pending): ";

    public const string ReferrerTgIdTag = "Referrer tgId: ";

    public const string BonusPaidMarker = "Referral bonus paid";

    public const string CreditTagPrefix = "Referral credit: ";

    public const string CancelledTagPrefix = "Cancelled ";

    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        return email.Trim().ToLowerInvariant();
    }

    public static bool IsValidEmail(string? email)
    {
        var normalized = NormalizeEmail(email);

        if (normalized is null || normalized.Length > 254) return false;

        try
        {
            var address = new MailAddress(normalized);
            return address.Address == normalized && normalized.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string BuildRefPayload(long telegramId)
    {
        return $"{RefPayloadPrefix}{telegramId}";
    }

    public static bool TryParseRefPayload(string? payload, out long telegramId)
    {
        telegramId = 0;

        if (string.IsNullOrWhiteSpace(payload)) return false;

        var raw = payload.Trim();

        if (raw.StartsWith(RefPayloadPrefix, StringComparison.OrdinalIgnoreCase)) raw = raw[RefPayloadPrefix.Length..];

        return long.TryParse(raw, out telegramId) && telegramId > 0;
    }

    public static string BuildReferralLink(string botUsername, long telegramId)
    {
        return $"https://t.me/{botUsername}?start={BuildRefPayload(telegramId)}";
    }

    public static string AppendTag(string? comment, string tag)
    {
        return string.IsNullOrEmpty(comment) ? tag : $"{comment} · {tag}";
    }

    public static string WithReferredBy(string? comment, string email, bool pending)
    {
        return AppendTag(comment, pending ? $"{PendingReferredByTag}{email}" : $"{ReferredByTag}{email}");
    }

    public static bool TryParseReferredBy(string? comment, out string email, out bool pending)
    {
        email = "";
        pending = false;

        if (string.IsNullOrEmpty(comment)) return false;

        var pendingMatch = PendingReferredByRegex().Match(comment);
        if (pendingMatch.Success)
        {
            email = pendingMatch.Groups[1].Value.Trim();
            pending = true;
            return true;
        }

        var match = ReferredByRegex().Match(comment);
        if (match.Success)
        {
            email = match.Groups[1].Value.Trim();
            return true;
        }

        return false;
    }

    public static bool TryParseReferrerTgId(string? comment, out long telegramId)
    {
        telegramId = 0;

        if (string.IsNullOrEmpty(comment)) return false;

        var match = ReferrerTgIdRegex().Match(comment);
        return match.Success && long.TryParse(match.Groups[1].Value, out telegramId) && telegramId > 0;
    }

    public static bool HasBonusPaidMarker(string? comment)
    {
        return comment?.Contains(BonusPaidMarker, StringComparison.OrdinalIgnoreCase) is true;
    }

    public static string ClearReferralAttribution(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";

        var updated = ReferralAttributionRegex().Replace(comment, " ");
        updated = Regex.Replace(updated, @"\s*·\s*·\s*", " · ");
        return updated.Trim().Trim('·').Trim();
    }

    public static int GetCreditDays(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return 0;

        return CreditRegex().Matches(comment).Sum(m =>
            int.TryParse(m.Groups[1].Value, out var days) && days > 0 ? days : 0);
    }

    public static string AddCreditDays(string? comment, int days)
    {
        var total = GetCreditDays(comment) + days;
        var updated = CreditRegex().Replace(comment ?? "", "", 1);

        if (string.IsNullOrEmpty(updated)) return $"{CreditTagPrefix}{total}d";

        return AppendTag(updated.TrimEnd(' ', '·'), $"{CreditTagPrefix}{total}d");
    }

    public static string MarkCancelled(string? comment, DateOnly date)
    {
        var updated = CancelledRegex().Replace(comment ?? "", "", 1).TrimEnd(' ', '·');
        return AppendTag(string.IsNullOrEmpty(updated) ? null : updated, $"{CancelledTagPrefix}{date:yyyy-MM-dd}");
    }

    public static bool IsCancelled(string? comment)
    {
        return !string.IsNullOrEmpty(comment) &&
               CancelledRegex().IsMatch(comment);
    }

    public static string ClearCancelled(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";

        return CancelledRegex().Replace(comment, "").TrimEnd(' ', '·');
    }

    public static string BuildReminderMarker(int days, DateOnly expiryDate)
    {
        return $"Reminder {days}d sent for {expiryDate:yyyy-MM-dd}";
    }

    public static bool HasReminderMarker(string? comment, int days, DateOnly expiryDate)
    {
        return comment?.Contains(BuildReminderMarker(days, expiryDate), StringComparison.Ordinal) is true;
    }

    public static bool HasEverBeenProvisioned(PanelClient client)
    {
        return HasEverBeenProvisioned(client.Enable, client.ExpiryTime);
    }

    public static bool HasEverBeenProvisioned(bool enable, long expiryTimeMs)
    {
        return enable || expiryTimeMs <= DateTimeOffset.UtcNow.AddYears(50).ToUnixTimeMilliseconds();
    }

    public static bool IsLegacyEmail(string? email)
    {
        return !string.IsNullOrEmpty(email) && LegacyEmailRegex().IsMatch(email);
    }

    [GeneratedRegex(@"Referred by \(pending\):\s*([^\s·]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PendingReferredByRegex();

    [GeneratedRegex(
        @"\s*·?\s*(?:Referred by \(pending\):\s*[^\s·]+|Referred by:\s*[^\s·]+|Referrer tgId:\s*\d+)\s*·?\s*",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReferralAttributionRegex();

    [GeneratedRegex(@"Referred by:\s*([^\s·]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReferredByRegex();

    [GeneratedRegex(@"Referrer tgId:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReferrerTgIdRegex();

    [GeneratedRegex(@"Referral credit:\s*(\d+)d", RegexOptions.IgnoreCase)]
    private static partial Regex CreditRegex();

    [GeneratedRegex(@"\s*·?\s*Cancelled \d{4}-\d{2}-\d{2}\s*·?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CancelledRegex();

    [GeneratedRegex(@"^tg\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyEmailRegex();
}