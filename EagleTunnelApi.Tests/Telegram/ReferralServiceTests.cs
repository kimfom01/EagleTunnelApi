using System.Text.RegularExpressions;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class ReferralServiceTests
{
    [Theory]
    [InlineData("  USER@Example.COM ", "user@example.com")]
    [InlineData("a@b.co", "a@b.co")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void NormalizeEmail_NormalizesOrNull(string? input, string? expected)
    {
        Assert.Equal(expected, ReferralService.NormalizeEmail(input));
    }

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("USER@Example.COM", true)]
    [InlineData("  spaced@example.com  ", true)]
    [InlineData("not-an-email", false)]
    [InlineData("user@localhost", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidEmail_Validates(string? input, bool expected)
    {
        Assert.Equal(expected, ReferralService.IsValidEmail(input));
    }

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
    public void Cancelled_MarkClear()
    {
        var comment = ReferralService.MarkCancelled("base", new DateOnly(2026, 9, 9));

        Assert.True(ReferralService.IsCancelled(comment));
        Assert.False(ReferralService.IsCancelled("base"));

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
        var trial = Client(true,
            DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds()) with
        {
            Comment = "Trial until 2026-09-09 12:00 UTC"
        };

        Assert.False(ReferralService.HasEverBeenProvisioned(trial));
    }

    [Fact]
    public void HasEverBeenProvisioned_PaidAfterTrial_IsTrue()
    {
        var paid = Client(true,
            DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds()) with
        {
            Comment = "some comment"
        };

        Assert.True(ReferralService.HasEverBeenProvisioned(paid));
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

    private static PanelClient Client(bool enable, long expiryTime)
    {
        return new PanelClient(
            "uuid", "user@example.com", enable, expiryTime,
            1, 100, null, 0, 2,
            "monthly", 1, 0, "auto",
            "sub", "xtls-rprx-vision", 1, null);
    }

    [Fact]
    public void HasEverBeenProvisioned_Enabled_IsTrue()
    {
        Assert.True(ReferralService.HasEverBeenProvisioned(
            Client(true, DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds())));
    }

    [Fact]
    public void HasEverBeenProvisioned_PlaceholderExpiry_IsFalse()
    {
        Assert.False(ReferralService.HasEverBeenProvisioned(
            Client(false, DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds())));
    }

    [Fact]
    public void HasEverBeenProvisioned_RealExpiry_IsTrue()
    {
        Assert.True(ReferralService.HasEverBeenProvisioned(
            Client(false, DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeMilliseconds())));
    }
}