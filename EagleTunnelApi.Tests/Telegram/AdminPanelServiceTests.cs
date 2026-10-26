using System.Text.Json;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.Domain.Referrals;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class AdminPanelServiceTests
{
    private const string BaseUri = "https://panel.test";
    private const int BonusDays = 30;
    private const long DayMs = 24L * 60 * 60 * 1000;

    private static PanelClient Client(string email, long tgId, bool enable, long expiryMs, string? comment)
    {
        return new PanelClient("uuid-1", email, enable, expiryMs, tgId, 100L * 1024 * 1024 * 1024, comment,
            0, 2, "monthly", 1, 0, "auto", "sub1", "xtls-rprx-vision", 1, null);
    }

    private static PanelClient ActiveClient(string email = "user@example.com", long tgId = 12345)
    {
        return Client(email, tgId, true, DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
            $"Telegram ID: {tgId}");
    }

    private static PanelClient TrialReferee()
    {
        return Client("newbie@example.com", 111, true,
            DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
            "Telegram ID: 111 · Referrer tgId: 777 · Referred by: friend@example.com · " +
            "Trial until 2099-01-01 00:00 UTC");
    }

    private static string ClientListJson(PanelClient client)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            new List<PanelClientResponse> { new(client, null, new List<int> { 1 }, 0) }));
    }

    private static string ClientByEmailJson(PanelClient client)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<PanelClientResponse>(true, "ok",
            new PanelClientResponse(client, null, new List<int> { 1 }, 0)));
    }

    private static string SummaryListJson(params PanelClientSummary[] summaries)
    {
        return JsonSerializer.Serialize(
            new PanelApiResponse<List<PanelClientSummary>>(true, "ok", summaries.ToList()));
    }

    private static string SuccessJson()
    {
        return JsonSerializer.Serialize(new PanelApiResponse<object>(true, "ok", null));
    }

    private static (AdminPanelService Service, FakeTelegramBotClient Bot, List<HttpRequestMessage> Requests)
        CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var bot = new FakeTelegramBotClient();
        var requests = new List<HttpRequestMessage>();

        var options = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            TributeSubscriptionUrl = "https://tribute.test",
            BotUsername = "TestBot",
            ReferralBonusDays = BonusDays,
            DefaultInboundIds = new[] { 1, 2 },
            WebhookPath = "/webhooks/telegram"
        });

        var panelClient = new PanelApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return responder(request);
        }))
        {
            BaseAddress = new Uri(BaseUri)
        }, NullLogger<PanelApiClient>.Instance);

        var service = new AdminPanelService(NullLogger<AdminPanelService>.Instance, panelClient, bot, options);

        return (service, bot, requests);
    }

    private static async Task<JsonElement> ReadBody(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static List<HttpRequestMessage> UpdatesTo(List<HttpRequestMessage> requests, string email)
    {
        return requests.Where(r => r.Method == HttpMethod.Post &&
            r.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/update/{email}").ToList();
    }

    [Fact]
    public async Task Grant_ManualGrantToReferredTrialAccount_SettlesBonus()
    {
        var referee = TrialReferee();
        var referrer = ActiveClient("friend@example.com", 777);
        var referrerOldExpiry = referrer.ExpiryTime;

        var (service, bot, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            // The grant clears the trial tag, so a re-fetch sees the updated comment.
            if (path == "/admin/panel/api/clients/get/tgId/111")
                return StubHttpMessageHandler.Json(ClientListJson(referee with
                {
                    Comment = ReferralService.ClearTrialTag(referee.Comment)
                }));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await service.GrantAsync("newbie@example.com", 30, CancellationToken.None);

        // Referrer got the bonus grant on top of their existing expiry (no chaining check here).
        var referrerUpdates = UpdatesTo(requests, "friend@example.com");
        Assert.Equal(2, referrerUpdates.Count); // grant + credit ledger

        var grantBody = await ReadBody(referrerUpdates[0]);
        Assert.Equal(referrerOldExpiry + 30 * DayMs, grantBody.GetProperty("expiryTime").GetInt64());
        Assert.True(grantBody.GetProperty("enable").GetBoolean());

        var creditBody = await ReadBody(referrerUpdates[1]);
        Assert.Contains("Referral credit: 30d", creditBody.GetProperty("comment").GetString());

        // Referee marked paid and trial cleared (grant update + bonus-paid mark).
        var refereeUpdates = UpdatesTo(requests, "newbie@example.com");
        Assert.Equal(2, refereeUpdates.Count);

        var paidBody = await ReadBody(refereeUpdates[1]);
        Assert.Contains("Referral bonus paid", paidBody.GetProperty("comment").GetString());

        foreach (var update in refereeUpdates)
            Assert.DoesNotContain("Trial until", (await ReadBody(update)).GetProperty("comment").GetString());

        // Referrer notified.
        Assert.Contains(bot.Requests, r => r.MethodName == "sendMessage" && r.Body.Contains("30 days"));
    }

    [Fact]
    public async Task Grant_AlreadyPaidMarker_NoDoublePayout()
    {
        var referee = ActiveClient("newbie@example.com", 111) with
        {
            Comment = "Telegram ID: 111 · Referrer tgId: 777 · Referred by: friend@example.com · " +
                      "Referral bonus paid"
        };

        var (service, bot, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/update/newbie@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await service.GrantAsync("newbie@example.com", 30, CancellationToken.None);

        Assert.Single(UpdatesTo(requests, "newbie@example.com"));
        Assert.Empty(UpdatesTo(requests, "friend@example.com"));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task Grant_ReferrerGrantDoesNotChain()
    {
        var referee = TrialReferee();
        var referrer = ActiveClient("friend@example.com", 777) with
        {
            Comment = "Telegram ID: 777 · Referrer tgId: 999 · Referred by: grand@example.com"
        };

        var (service, _, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/111")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await service.GrantAsync("newbie@example.com", 30, CancellationToken.None);

        // The referrer was paid, but their own referrer got nothing.
        Assert.NotEmpty(UpdatesTo(requests, "friend@example.com"));
        Assert.Empty(UpdatesTo(requests, "grand@example.com"));
    }

    [Fact]
    public async Task Grant_UnclaimedPreRegisteredReferee_SettlesBonusByEmail()
    {
        // Referrer pre-registered a friend from the bot: the row has TgId = 0 until
        // the friend claims it. The friend pays by bank transfer, so the admin grants
        // days by email — the payout must not require a claimed Telegram ID.
        var referee = Client("friend-of@example.com", 0, true,
            DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
            "Referrer tgId: 777 · Referred by: friend@example.com · " +
            "Trial until 2099-01-01 00:00 UTC");
        var referrer = ActiveClient("friend@example.com", 777);
        var referrerOldExpiry = referrer.ExpiryTime;
        var granted = false;

        var (service, bot, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/friend-of@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(granted
                    ? referee with { Comment = ReferralService.ClearTrialTag(referee.Comment) }
                    : referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/friend-of@example.com")
            {
                granted = true;
                return StubHttpMessageHandler.Json(SuccessJson());
            }

            if (path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await service.GrantAsync("friend-of@example.com", 30, CancellationToken.None);

        var referrerUpdates = UpdatesTo(requests, "friend@example.com");
        Assert.Equal(2, referrerUpdates.Count); // bonus grant + credit ledger

        var grantBody = await ReadBody(referrerUpdates[0]);
        Assert.Equal(referrerOldExpiry + BonusDays * DayMs, grantBody.GetProperty("expiryTime").GetInt64());
        Assert.True(grantBody.GetProperty("enable").GetBoolean());

        var creditBody = await ReadBody(referrerUpdates[1]);
        Assert.Contains("Referral credit: 30d", creditBody.GetProperty("comment").GetString());

        var refereeUpdates = UpdatesTo(requests, "friend-of@example.com");
        Assert.Equal(2, refereeUpdates.Count); // admin grant + bonus-paid mark
        Assert.Contains("Referral bonus paid",
            (await ReadBody(refereeUpdates[1])).GetProperty("comment").GetString());

        Assert.Contains(bot.Requests, r => r.MethodName == "sendMessage" && r.Body.Contains("30 days"));
    }

    [Fact]
    public async Task Sweep_SettlesUnclaimedDashboardGrantedAccount()
    {
        // Same unclaimed pre-registration, but the admin extended it straight in the
        // 3X-UI dashboard: the daily sweep must still pay the referrer.
        var referee = Client("friend-of@example.com", 0, true,
            DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
            "Referrer tgId: 777 · Referred by: friend@example.com");
        var referrer = ActiveClient("friend@example.com", 777);

        var (service, bot, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list")
                return StubHttpMessageHandler.Json(SummaryListJson(
                    new PanelClientSummary(1, "friend-of@example.com", "sub1", "uuid-1", 100,
                        referee.ExpiryTime, true, new List<int> { 1 }, null)));

            if (path == "/admin/panel/api/clients/get/friend-of@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/friend-of@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var settled = await service.SettlePendingReferralBonusesAsync(CancellationToken.None);

        Assert.Equal(1, settled);
        Assert.NotEmpty(UpdatesTo(requests, "friend@example.com"));
        Assert.Contains(bot.Requests, r => r.MethodName == "sendMessage" && r.Body.Contains("30 days"));
    }

    [Fact]
    public async Task Payout_PreservesReferrerCancelledStateForReminders()
    {
        // The payout is not a resubscription: the referrer keeps the Cancelled tag
        // so the 3/2/1/0-day win-back reminders still fire when the free month ends.
        var referee = TrialReferee();
        var referrer = ActiveClient("friend@example.com", 777) with
        {
            Comment = "Telegram ID: 777 · Cancelled 2026-09-01 · Trial until 2026-09-01 10:00 UTC"
        };

        var (service, _, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/111")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await service.GrantAsync("newbie@example.com", 30, CancellationToken.None);

        var referrerUpdates = UpdatesTo(requests, "friend@example.com");
        Assert.Equal(2, referrerUpdates.Count);

        var grantComment = (await ReadBody(referrerUpdates[0])).GetProperty("comment").GetString();
        Assert.Contains("Cancelled 2026-09-01", grantComment);
        Assert.DoesNotContain("Trial until", grantComment);
    }

    [Fact]
    public async Task Grant_LapsedClient_ClearsConsumedCredit()
    {
        // The bonus days were already used up (expiry in the past). A new direct
        // payment must not re-add the spent credit, and the stale tag should go.
        var lapsed = Client("user@example.com", 12345, false,
            DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds(),
            "Telegram ID: 12345 · Referral credit: 30d");

        var (service, _, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/user@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(lapsed));

            if (path == "/admin/panel/api/clients/update/user@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await service.GrantAsync("user@example.com", 30, CancellationToken.None);

        var update = Assert.Single(UpdatesTo(requests, "user@example.com"));
        var body = await ReadBody(update);

        Assert.DoesNotContain("Referral credit", body.GetProperty("comment").GetString()!);
        Assert.InRange(body.GetProperty("expiryTime").GetInt64(),
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-2).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(2).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Payout_LapsedReferrer_DoesNotCarryConsumedCredit()
    {
        // The referrer's earlier bonus was used up. A new payout must tag only the
        // new 30 days — not sum the spent ledger into 60.
        var referee = TrialReferee();
        var referrer = Client("friend@example.com", 777, false,
            DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds(),
            "Telegram ID: 777 · Referral credit: 30d");

        string? referrerComment = null;

        var (service, _, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/111")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(
                    referrerComment is null ? referrer : referrer with { Comment = referrerComment }));

            if (path == "/admin/panel/api/clients/update/friend@example.com")
            {
                // Mirror the panel state so the credit-ledger read sees the grant's
                // cleared ledger rather than the stale fixture comment.
                var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(payload);
                referrerComment = doc.RootElement.GetProperty("comment").GetString();
                return StubHttpMessageHandler.Json(SuccessJson());
            }

            if (path == "/admin/panel/api/clients/update/newbie@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await service.GrantAsync("newbie@example.com", 30, CancellationToken.None);

        var referrerUpdates = UpdatesTo(requests, "friend@example.com");
        Assert.Equal(2, referrerUpdates.Count);

        var grantComment = (await ReadBody(referrerUpdates[0])).GetProperty("comment").GetString()!;
        Assert.DoesNotContain("Referral credit", grantComment);

        var creditComment = (await ReadBody(referrerUpdates[1])).GetProperty("comment").GetString()!;
        Assert.Contains("Referral credit: 30d", creditComment);
        Assert.DoesNotContain("Referral credit: 60d", creditComment);
    }

    [Fact]
    public async Task Sweep_SettlesTrialTaggedDashboardExtendedAccount()
    {
        // Direct payment extended the expiry in the 3X-UI dashboard, so the trial
        // tag is still present but the account has clearly paid.
        var trialEnds = DateTimeOffset.UtcNow.AddDays(-2);
        var referee = Client("newbie@example.com", 111, true,
            DateTimeOffset.UtcNow.AddDays(28).ToUnixTimeMilliseconds(),
            "Telegram ID: 111 · Referrer tgId: 777 · Referred by: friend@example.com · " +
            ReferralService.WithTrialTag(null, trialEnds));
        var referrer = ActiveClient("friend@example.com", 777);

        var (service, bot, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list")
                return StubHttpMessageHandler.Json(SummaryListJson(
                    new PanelClientSummary(1, "newbie@example.com", "sub1", "uuid-1", 100, referee.ExpiryTime,
                        true, new List<int> { 1 }, null)));

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/111")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var settled = await service.SettlePendingReferralBonusesAsync(CancellationToken.None);

        Assert.Equal(1, settled);
        Assert.NotEmpty(UpdatesTo(requests, "friend@example.com"));
        Assert.Contains(bot.Requests, r => r.MethodName == "sendMessage" && r.Body.Contains("30 days"));
    }

    [Fact]
    public async Task SettleForEmail_TrialExtendedInDashboard_PaysReferrer()
    {
        var trialEnds = DateTimeOffset.UtcNow.AddDays(-1);
        var referee = Client("newbie@example.com", 0, true,
            DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeMilliseconds(),
            "Referrer tgId: 777 · Referred by: friend@example.com · " +
            ReferralService.WithTrialTag(null, trialEnds));
        var referrer = ActiveClient("friend@example.com", 777);

        var (service, _, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var paid = await service.TrySettleReferralBonusForEmailAsync("newbie@example.com",
            CancellationToken.None);

        Assert.True(paid);
        Assert.NotEmpty(UpdatesTo(requests, "friend@example.com"));
    }

    [Fact]
    public async Task Grant_DirectPaymentClearsWinBackStateAndReportsBonus()
    {
        var referee = Client("newbie@example.com", 111, true,
            DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(),
            "Telegram ID: 111 · Referrer tgId: 777 · Referred by: friend@example.com · " +
            "Cancelled 2026-09-01 · Reminder 1d sent for 2026-09-02 · Trial until 2026-09-01 10:00 UTC");
        var referrer = ActiveClient("friend@example.com", 777);

        var (service, _, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/111")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var paid = await service.GrantAsync("newbie@example.com", 30, CancellationToken.None);

        Assert.True(paid);

        // The admin grant is a paid activation: win-back state goes away exactly as
        // it does on the Tribute path, while referral state is preserved.
        var refereeUpdates = UpdatesTo(requests, "newbie@example.com");
        var grantComment = (await ReadBody(refereeUpdates[0])).GetProperty("comment").GetString();

        Assert.DoesNotContain("Cancelled", grantComment);
        Assert.DoesNotContain("Reminder", grantComment);
        Assert.DoesNotContain("Trial until", grantComment);
        Assert.Contains("Referred by: friend@example.com", grantComment);
    }

    [Fact]
    public async Task Sweep_SettlesDashboardGrantedAccount()
    {
        var referee = ActiveClient("newbie@example.com", 111) with
        {
            Comment = "Telegram ID: 111 · Referrer tgId: 777 · Referred by: friend@example.com"
        };
        var referrer = ActiveClient("friend@example.com", 777);

        var (service, bot, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list")
                return StubHttpMessageHandler.Json(SummaryListJson(
                    new PanelClientSummary(1, "newbie@example.com", "sub1", "uuid-1", 100, referee.ExpiryTime,
                        true, new List<int> { 1 }, null)));

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/111")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var settled = await service.SettlePendingReferralBonusesAsync(CancellationToken.None);

        Assert.Equal(1, settled);
        Assert.NotEmpty(UpdatesTo(requests, "friend@example.com"));
        Assert.Contains(bot.Requests, r => r.MethodName == "sendMessage" && r.Body.Contains("30 days"));
    }

    [Fact]
    public async Task Sweep_SkipsTrialPaidMarkerAndUntagged()
    {
        var trial = TrialReferee();
        var paid = ActiveClient("paid@example.com", 222) with
        {
            Comment = "Telegram ID: 222 · Referred by: friend@example.com · Referral bonus paid"
        };
        var plain = ActiveClient("plain@example.com", 333);

        var (service, _, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list")
                return StubHttpMessageHandler.Json(SummaryListJson(
                    new PanelClientSummary(1, "newbie@example.com", "s1", "u1", 100, trial.ExpiryTime, true,
                        new List<int> { 1 }, null),
                    new PanelClientSummary(2, "paid@example.com", "s2", "u2", 100, paid.ExpiryTime, true,
                        new List<int> { 1 }, null),
                    new PanelClientSummary(3, "plain@example.com", "s3", "u3", 100, plain.ExpiryTime, true,
                        new List<int> { 1 }, null)));

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(trial));

            if (path == "/admin/panel/api/clients/get/paid@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(paid));

            if (path == "/admin/panel/api/clients/get/plain@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(plain));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var settled = await service.SettlePendingReferralBonusesAsync(CancellationToken.None);

        Assert.Equal(0, settled);
        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task SettleForEmail_UnclaimedGrantedAccount_PaysReferrerImmediately()
    {
        var referee = Client("newbie@example.com", 0, true,
            DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
            "Registered by admin · Referrer tgId: 777 · Referred by: friend@example.com");
        var referrer = ActiveClient("friend@example.com", 777);
        var referrerOldExpiry = referrer.ExpiryTime;

        var (service, bot, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var paid = await service.TrySettleReferralBonusForEmailAsync("newbie@example.com",
            CancellationToken.None);

        Assert.True(paid);

        var referrerUpdates = UpdatesTo(requests, "friend@example.com");
        Assert.Equal(2, referrerUpdates.Count); // bonus grant + credit ledger

        var grantBody = await ReadBody(referrerUpdates[0]);
        Assert.Equal(referrerOldExpiry + BonusDays * DayMs, grantBody.GetProperty("expiryTime").GetInt64());

        var refereeUpdates = UpdatesTo(requests, "newbie@example.com");
        Assert.Single(refereeUpdates);
        Assert.Contains("Referral bonus paid",
            (await ReadBody(refereeUpdates[0])).GetProperty("comment").GetString());

        Assert.Contains(bot.Requests, r => r.MethodName == "sendMessage" && r.Body.Contains("30 days"));
    }

    [Fact]
    public async Task SettleForEmail_TrialAccount_StaysPending()
    {
        var trial = TrialReferee() with { TgId = 0, Email = "newbie@example.com" };

        var (service, _, requests) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(trial));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var paid = await service.TrySettleReferralBonusForEmailAsync("newbie@example.com",
            CancellationToken.None);

        Assert.False(paid);
        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Post);
    }
}
