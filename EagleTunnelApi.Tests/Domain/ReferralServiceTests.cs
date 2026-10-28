using System.Text.RegularExpressions;
using EagleTunnelApi.Domain.Referrals;
using Xunit;

namespace EagleTunnelApi.Tests.Domain;

public class ReferralServiceTests
{
    [Fact]
    public void RefPayload_RoundTrips()
    {
        var payload = ReferralService.BuildRefPayload(12345);
        Assert.Equal("ref_12345", payload);
        Assert.True(ReferralService.TryParseRefPayload(payload, out var tgId));
        Assert.Equal(12345, tgId);
    }

    [Theory]
    [InlineData("12345", 12345)]
    [InlineData("ref_999", 999)]
    [InlineData("REF_999", 999)]
    public void TryParseRefPayload_AcceptsVariants(string payload, long expected)
    {
        Assert.True(ReferralService.TryParseRefPayload(payload, out var tgId));
        Assert.Equal(expected, tgId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ref_")]
    [InlineData("ref_abc")]
    [InlineData("ref_-5")]
    [InlineData("ref_0")]
    [InlineData("hello")]
    public void TryParseRefPayload_RejectsInvalid(string? payload)
    {
        Assert.False(ReferralService.TryParseRefPayload(payload, out _));
    }

    [Fact]
    public void BuildReferralLink_Formats()
    {
        Assert.Equal("https://t.me/EagleTunnelBot?start=ref_42",
            ReferralService.BuildReferralLink("EagleTunnelBot", 42));
    }

    [Fact]
    public void ReferredBy_Confirmed_RoundTrips()
    {
        var comment = ReferralService.WithReferredBy("base comment", "friend@example.com", false);

        Assert.True(ReferralService.TryParseReferredBy(comment, out var email, out var pending));
        Assert.Equal("friend@example.com", email);
        Assert.False(pending);
        Assert.Contains("base comment", comment);
    }

    [Fact]
    public void ReferredBy_Pending_RoundTrips()
    {
        var comment = ReferralService.WithReferredBy(null, "friend@example.com", true);

        Assert.True(ReferralService.TryParseReferredBy(comment, out var email, out var pending));
        Assert.Equal("friend@example.com", email);
        Assert.True(pending);
    }

    [Fact]
    public void TryParseReferredBy_NoTag_ReturnsFalse()
    {
        Assert.False(ReferralService.TryParseReferredBy("plain comment", out _, out _));
        Assert.False(ReferralService.TryParseReferredBy(null, out _, out _));
    }

    [Fact]
    public void ReferrerTgId_RoundTrips()
    {
        var comment = ReferralService.AppendTag("base", "Referrer tgId: 777");

        Assert.True(ReferralService.TryParseReferrerTgId(comment, out var tgId));
        Assert.Equal(777, tgId);
    }

    [Fact]
    public void BonusPaidMarker_Detected()
    {
        Assert.False(ReferralService.HasBonusPaidMarker("Referred by: a@b.co"));
        Assert.True(ReferralService.HasBonusPaidMarker(
            ReferralService.AppendTag("Referred by: a@b.co", "Referral bonus paid")));
    }

    [Fact]
    public void CreditDays_Accumulate()
    {
        string? comment = null;
        comment = ReferralService.AddCreditDays(comment, 30);
        Assert.Equal(30, ReferralService.GetCreditDays(comment));

        comment = ReferralService.AddCreditDays(comment, 30);
        Assert.Equal(60, ReferralService.GetCreditDays(comment));
        Assert.Single(Regex.Matches(comment, "Referral credit:"));
    }

    [Fact]
    public void ClearCredit_RemovesLedgerKeepsRest()
    {
        const string comment = "Telegram ID: 5 · Referral credit: 30d · Referred by: friend@example.com";

        var cleared = ReferralService.ClearCredit(comment);

        Assert.Equal(0, ReferralService.GetCreditDays(cleared));
        Assert.Contains("Telegram ID: 5", cleared);
        Assert.Contains("Referred by: friend@example.com", cleared);
        Assert.DoesNotContain("Referral credit", cleared);
        Assert.DoesNotContain("·  ·", cleared);

        Assert.Equal("base", ReferralService.ClearCredit("Referral credit: 30d · base"));
        Assert.Equal("", ReferralService.ClearCredit("Referral credit: 30d"));
        Assert.Equal("", ReferralService.ClearCredit(null));
    }

    [Fact]
    public void Cancelled_MarkClear()
    {
        var comment = ReferralService.MarkCancelled("base", new DateOnly(2026, 9, 9));

        Assert.True(ReferralService.IsCancelled(comment));
        Assert.False(ReferralService.IsCancelled("base"));
        Assert.True(ReferralService.TryGetCancelledDate(comment, out var cancelledDate));
        Assert.Equal(new DateOnly(2026, 9, 9), cancelledDate);
        Assert.False(ReferralService.TryGetCancelledDate("Cancelled not-a-date", out _));

        var cleared = ReferralService.ClearCancelled(comment);
        Assert.False(ReferralService.IsCancelled(cleared));
        Assert.Contains("base", cleared);
    }

    [Fact]
    public void ReminderMarker_ScopedToExpiryDate()
    {
        var expiry = new DateOnly(2026, 10, 1);
        var comment = ReferralService.AppendTag("base", ReferralService.BuildReminderMarker(3, expiry));

        Assert.True(ReferralService.HasReminderMarker(comment, 3, expiry));
        Assert.False(ReferralService.HasReminderMarker(comment, 2, expiry));
        Assert.False(ReferralService.HasReminderMarker(comment, 3, expiry.AddDays(30)));

        var markers = ReferralService.GetReminderMarkers(
            "Reminder 3d sent for 2026-10-01 · Reminder 1d sent for 2026-10-01 · malformed");
        Assert.Equal(new[] { new ReminderMarker(1, expiry), new ReminderMarker(3, expiry) }, markers);
    }

    [Fact]
    public void ClearRenewalMarkers_RemovesCancelledAndRemindersKeepsRest()
    {
        var comment =
            "Telegram ID: 5 · Referred by: friend@example.com · Referral credit: 30d · " +
            "Referral bonus paid · Cancelled 2026-09-01 · " +
            "Reminder 3d sent for 2026-10-01 · Reminder 2d sent for 2026-10-01";

        var cleared = ReferralService.ClearRenewalMarkers(comment);

        Assert.DoesNotContain("Cancelled", cleared);
        Assert.DoesNotContain("Reminder", cleared);
        Assert.Contains("Telegram ID: 5", cleared);
        Assert.Contains("Referred by: friend@example.com", cleared);
        Assert.Contains("Referral credit: 30d", cleared);
        Assert.Contains("Referral bonus paid", cleared);
        Assert.DoesNotContain("· ·", cleared);
    }

    [Fact]
    public void ClearRenewalMarkers_CleanComment_Unchanged()
    {
        Assert.Equal("base", ReferralService.ClearRenewalMarkers("base"));
        Assert.Equal("", ReferralService.ClearRenewalMarkers(""));
        Assert.Equal("", ReferralService.ClearRenewalMarkers(null));
    }

    [Fact]
    public void ClearReferralAttribution_RemovesTagsKeepsRest()
    {
        var comment = "John Doe · Telegram ID: 5 · Referrer tgId: 777 · Referred by: friend@example.com";

        var cleared = ReferralService.ClearReferralAttribution(comment);

        Assert.DoesNotContain("Referrer tgId", cleared);
        Assert.DoesNotContain("Referred by", cleared);
        Assert.Contains("John Doe", cleared);
        Assert.Contains("Telegram ID: 5", cleared);
    }

    [Fact]
    public void ClearReferralAttribution_RemovesPending()
    {
        var cleared = ReferralService.ClearReferralAttribution("base · Referred by (pending): ghost@example.com");

        Assert.DoesNotContain("pending", cleared);
        Assert.DoesNotContain("ghost@example.com", cleared);
        Assert.Contains("base", cleared);
    }

    [Fact]
    public void TrialTag_RoundTripsAndClears()
    {
        var comment = ReferralService.WithTrialTag("base",
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));

        Assert.True(ReferralService.HasTrialTag(comment));
        Assert.Contains("Trial until 2026-09-09 12:00", comment);
        Assert.False(ReferralService.HasTrialTag("base"));

        var cleared = ReferralService.ClearTrialTag(comment);
        Assert.False(ReferralService.HasTrialTag(cleared));
        Assert.Contains("base", cleared);
    }

    [Fact]
    public void HasEverBeenProvisioned_TrialAccount_IsFalse()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(ReferralService.HasEverBeenProvisioned(true,
            now.AddHours(2).ToUnixTimeMilliseconds(), now, "Trial until 2026-09-09 12:00 UTC"));
    }

    [Fact]
    public void HasEverBeenProvisioned_PaidAfterTrial_IsTrue()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(ReferralService.HasEverBeenProvisioned(true,
            now.AddDays(30).ToUnixTimeMilliseconds(), now, "some comment"));
    }

    [Theory]
    [InlineData("tg12345", true)]
    [InlineData("TG99", true)]
    [InlineData("user@example.com", false)]
    [InlineData("tgabc", false)]
    [InlineData(null, false)]
    public void IsLegacyEmail_Detects(string? email, bool expected)
    {
        Assert.Equal(expected, ReferralService.IsLegacyEmail(email));
    }

    [Fact]
    public void HasEverBeenProvisioned_Enabled_IsTrue()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(ReferralService.HasEverBeenProvisioned(true,
            now.AddDays(30).ToUnixTimeMilliseconds(), now));
    }

    [Fact]
    public void HasEverBeenProvisioned_PlaceholderExpiry_IsFalse()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(ReferralService.HasEverBeenProvisioned(false,
            now.AddYears(100).ToUnixTimeMilliseconds(), now));
    }

    [Fact]
    public void HasEverBeenProvisioned_RealExpiry_IsTrue()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(ReferralService.HasEverBeenProvisioned(false,
            now.AddDays(-5).ToUnixTimeMilliseconds(), now));
    }

    [Fact]
    public void HasConvertedToPaid_GenuineTrial_IsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        var ends = now.AddHours(2);

        Assert.False(ReferralService.HasConvertedToPaid(true, ends.ToUnixTimeMilliseconds(), now,
            ReferralService.WithTrialTag(null, ends)));
    }

    [Fact]
    public void HasConvertedToPaid_TrialExtendedOutsideBot_IsTrue()
    {
        // Direct payment extended the expiry in the 3X-UI dashboard; the tag remains.
        var now = DateTimeOffset.UtcNow;
        var ends = now.AddHours(2);

        Assert.True(ReferralService.HasConvertedToPaid(true, ends.AddDays(30).ToUnixTimeMilliseconds(), now,
            ReferralService.WithTrialTag(null, ends)));
    }

    [Fact]
    public void HasConvertedToPaid_ExpiredUnpaidTrial_IsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        var ends = now.AddDays(-2);

        Assert.False(ReferralService.HasConvertedToPaid(true, ends.ToUnixTimeMilliseconds(), now,
            ReferralService.WithTrialTag(null, ends)));
    }

    [Fact]
    public void HasConvertedToPaid_TrialTagMinuteRounding_IsFalse()
    {
        // The tag stores minute precision, so a trial ending seconds later is still a trial.
        var ends = new DateTimeOffset(2026, 9, 9, 12, 0, 30, TimeSpan.Zero);

        Assert.False(ReferralService.HasConvertedToPaid(true, ends.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow, "Trial until 2026-09-09 12:00 UTC"));
    }

    [Fact]
    public void HasConvertedToPaid_NoTrialTag_FallsBackToProvisioned()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(ReferralService.HasConvertedToPaid(true,
            now.AddDays(30).ToUnixTimeMilliseconds(), now));
        Assert.False(ReferralService.HasConvertedToPaid(false,
            now.AddYears(100).ToUnixTimeMilliseconds(), now));
    }

    [Fact]
    public void TryGetTrialEnd_ParsesUtcMinute()
    {
        Assert.True(ReferralService.TryGetTrialEnd(
            "base · Trial until 2026-09-09 12:30 UTC", out var ends));
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 12, 30, 0, TimeSpan.Zero), ends);

        Assert.False(ReferralService.TryGetTrialEnd("no tag here", out _));
        Assert.False(ReferralService.TryGetTrialEnd(null, out _));
    }

    [Fact]
    public void ClearActivationComment_ClearsWinBackAndTrialKeepsReferralState()
    {
        const string comment = "base · Referred by: a@b.com · Referral credit: 30d · " +
                               "Cancelled 2026-09-01 · Reminder 3d sent for 2026-09-09 · " +
                               "Trial until 2026-09-09 12:00 UTC";

        var cleared = ReferralService.ClearActivationComment(comment);

        Assert.NotNull(cleared);
        Assert.DoesNotContain("Cancelled", cleared);
        Assert.DoesNotContain("Reminder", cleared);
        Assert.DoesNotContain("Trial until", cleared);
        Assert.Contains("Referred by: a@b.com", cleared);
        Assert.Contains("Referral credit: 30d", cleared);

        Assert.Null(ReferralService.ClearActivationComment(null));
    }
}