using System.Globalization;
using System.Text.RegularExpressions;

namespace EagleTunnelApi.Domain.Referrals;

public static partial class ReferralService
{
    public const string RefPayloadPrefix = "ref_";

    public const string ReferredByTag = "Referred by: ";

    public const string PendingReferredByTag = "Referred by (pending): ";

    public const string ReferrerTgIdTag = "Referrer tgId: ";

    public const string BonusPaidMarker = "Referral bonus paid";

    public const string CreditTagPrefix = "Referral credit: ";

    public const string CancelledTagPrefix = "Cancelled ";

    public const string TrialTagPrefix = "Trial until ";

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

    /// <summary>
    /// Removes the credit ledger once its days have been consumed. The tag records
    /// bonus days that are already baked into the panel expiry; re-applying it
    /// after the account lapsed would grant the same bonus a second time.
    /// </summary>
    public static string ClearCredit(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";

        var updated = CreditRegex().Replace(comment, " ");
        updated = Regex.Replace(updated, @"\s*·\s*·\s*", " · ");
        return updated.Trim().Trim('·').Trim();
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

    /// <summary>
    /// Removes win-back state (the <c>Cancelled</c> tag and all
    /// <c>Reminder Nd sent</c> markers) when paid access resumes.
    /// Referral attribution, credit ledger, and bonus markers are preserved.
    /// </summary>
    public static string ClearRenewalMarkers(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";

        var updated = CancelledRegex().Replace(comment, " ");
        updated = ReminderMarkerRegex().Replace(updated, " ");
        updated = Regex.Replace(updated, @"\s*·\s*·\s*", " · ");
        return updated.Trim().Trim('·').Trim();
    }

    public static string BuildReminderMarker(int days, DateOnly expiryDate)
    {
        return $"Reminder {days}d sent for {expiryDate:yyyy-MM-dd}";
    }

    public static bool HasReminderMarker(string? comment, int days, DateOnly expiryDate)
    {
        return comment?.Contains(BuildReminderMarker(days, expiryDate), StringComparison.Ordinal) is true;
    }

    public static bool HasEverBeenProvisioned(bool enable, long expiryTimeMs, DateTimeOffset now,
        string? comment = null)
    {
        return (enable || expiryTimeMs <= now.AddYears(50).ToUnixTimeMilliseconds()) &&
            !HasTrialTag(comment);
    }

    /// <summary>
    /// Whether the account has ever had paid access, treating a trial row whose
    /// expiry was pushed past the trial window as paid. Direct payments (bank
    /// transfer to the admin, 3X-UI dashboard edits) leave the trial tag in place,
    /// so <see cref="HasEverBeenProvisioned"/> alone would keep calling them
    /// "never paid". Used for payouts and for locking referral changes after
    /// payment; the claim path intentionally stays on
    /// <see cref="HasEverBeenProvisioned"/> so a friend who pays before claiming
    /// keeps their attribution.
    /// </summary>
    public static bool HasConvertedToPaid(bool enable, long expiryTimeMs, DateTimeOffset now,
        string? comment = null)
    {
        // Genuine trial rows expire at the tag's own time. Anything meaningfully
        // later was paid for; the one-hour tolerance absorbs the tag's minute
        // precision. Unparsable tags stay unpaid (safe: the bonus stays pending).
        if (HasTrialTag(comment) && TryGetTrialEnd(comment, out var trialEnds))
            return enable && expiryTimeMs > trialEnds.AddHours(1).ToUnixTimeMilliseconds();

        return HasEverBeenProvisioned(enable, expiryTimeMs, now, comment);
    }

    public static bool TryGetTrialEnd(string? comment, out DateTimeOffset trialEnds)
    {
        trialEnds = default;

        if (string.IsNullOrEmpty(comment)) return false;

        var match = TrialEndRegex().Match(comment);

        return match.Success && DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out trialEnds);
    }

    /// <summary>
    /// Comment transition for any paid activation: drop win-back state (cancelled
    /// tag, reminder markers) and the trial tag, preserving referral attribution,
    /// the credit ledger, and the bonus-paid marker. Shared by the Tribute
    /// provisioner and manual admin grants so both routes leave identical state.
    /// </summary>
    public static string? ClearActivationComment(string? comment)
    {
        if (comment is null) return null;

        var cleared = ClearRenewalMarkers(comment);

        if (HasTrialTag(cleared)) cleared = ClearTrialTag(cleared);

        return cleared;
    }

    public static string WithTrialTag(string? comment, DateTimeOffset trialEnds) =>
        AppendTag(comment, $"{TrialTagPrefix}{trialEnds:yyyy-MM-dd HH:mm} UTC");

    public static bool HasTrialTag(string? comment)
    {
        return !string.IsNullOrEmpty(comment) && TrialTagRegex().IsMatch(comment);
    }

    public static string ClearTrialTag(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";

        var updated = TrialTagRegex().Replace(comment, " ");
        updated = System.Text.RegularExpressions.Regex.Replace(updated, @"\s*·\s*·\s*", " · ");
        return updated.Trim().Trim('·').Trim();
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

    [GeneratedRegex(@"\s*·?\s*Reminder \d+d sent for \d{4}-\d{2}-\d{2}\s*·?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ReminderMarkerRegex();

    [GeneratedRegex(@"\s*·?\s*Trial until [^·]*", RegexOptions.IgnoreCase)]
    private static partial Regex TrialTagRegex();

    [GeneratedRegex(@"Trial until (\d{4}-\d{2}-\d{2} \d{2}:\d{2}) UTC", RegexOptions.IgnoreCase)]
    private static partial Regex TrialEndRegex();

    [GeneratedRegex(@"^tg\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyEmailRegex();
}