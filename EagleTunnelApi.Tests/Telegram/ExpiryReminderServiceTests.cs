using System.Net;
using System.Text;
using System.Text.Json;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class ExpiryReminderServiceTests
{
    private const string PanelBaseUri = "https://panel.test";

    private static PanelClientSummary Summary(string email, DateTimeOffset expiry, bool enable = true)
    {
        return new PanelClientSummary(
            1, email, "sub1", "uuid-1", 100,
            expiry.ToUnixTimeMilliseconds(), enable, null, null);
    }

    private static PanelClient FullClient(string email, long tgId, string? comment)
    {
        return new PanelClient(
            "uuid-1", email, true,
            DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds(),
            tgId, 100, comment, 0, 2,
            "monthly", 1, 0, "auto",
            "sub1", "xtls-rprx-vision", 1, null);
    }

    private static string ListJson(params PanelClientSummary[] summaries)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientSummary>>(true, "ok", summaries.ToList()));
    }

    private static string ClientByEmailJson(PanelClient client)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<PanelClientResponse>(true, "ok",
            new PanelClientResponse(client, null, new List<int> { 1 }, 0)));
    }

    private static string SuccessJson()
    {
        return JsonSerializer.Serialize(new PanelApiResponse<object>(true, "ok", null));
    }

    private static HttpResponseMessage Json(string rawJson)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
    }

    private static (ExpiryReminderService Service, FakeTelegramBotClient Bot) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder, List<HttpRequestMessage>? requests = null)
    {
        var bot = new FakeTelegramBotClient();
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            TributeSubscriptionUrl = "https://tribute.test/sub",
            BotUsername = "TestBot",
            DefaultInboundIds = new[] { 1 }
        });

        var panelClient = new PanelApiClient(
            new HttpClient(new StubHttpMessageHandler(request =>
                {
                    requests?.Add(request);
                    return responder(request);
                }))
            { BaseAddress = new Uri(PanelBaseUri) },
            NullLogger<PanelApiClient>.Instance);

        return (new ExpiryReminderService(panelClient, bot, options,
            NullLogger<ExpiryReminderService>.Instance), bot);
    }

    [Fact]
    public async Task SendReminders_CancelledInWindow_SendsOnceWithMarker()
    {
        var requests = new List<HttpRequestMessage>();
        var expiry = DateTimeOffset.UtcNow.AddDays(2);
        var expiryDate = DateOnly.FromDateTime(expiry.UtcDateTime);
        var comment = "some comment · Cancelled 2026-01-01";

        var (service, bot) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list") return Json(ListJson(Summary("user@example.com", expiry)));

            if (path == "/admin/panel/api/clients/get/user@example.com")
                return Json(ClientByEmailJson(FullClient("user@example.com", 555, comment)
                    with
                {
                    ExpiryTime = expiry.ToUnixTimeMilliseconds()
                }));

            if (path == "/admin/panel/api/clients/update/user@example.com")
            {
                var body = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
                comment = body.GetProperty("comment").GetString()!;
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        var sent = await service.SendRemindersAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        var dm = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("555", dm.Body);
        Assert.Contains("2 days", dm.Body);
        Assert.Contains("https://tribute.test/sub", dm.Body);

        var update = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/clients/update/"));
        var body = JsonDocument.Parse(await update.Content!.ReadAsStringAsync()).RootElement;
        Assert.Contains($"Reminder 2d sent for {expiryDate:yyyy-MM-dd}",
            body.GetProperty("comment").GetString());

        var sentAgain = await service.SendRemindersAsync(CancellationToken.None);
        Assert.Equal(0, sentAgain);
    }

    [Fact]
    public async Task SendReminders_DayZero_SaysEndsToday()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(5);
        var full = FullClient("user@example.com", 555, "Cancelled 2026-01-01")
            with
        {
            ExpiryTime = expiry.ToUnixTimeMilliseconds()
        };

        var (service, bot) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list") return Json(ListJson(Summary("user@example.com", expiry)));

            if (path == "/admin/panel/api/clients/get/user@example.com") return Json(ClientByEmailJson(full));

            if (path.Contains("/clients/update/")) return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var sent = await service.SendRemindersAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        var dm = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("ends today", dm.Body);
    }

    [Fact]
    public async Task SendReminders_ActiveNotCancelled_Skips()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        var full = FullClient("user@example.com", 555, "plain comment");

        var (service, bot) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list") return Json(ListJson(Summary("user@example.com", expiry)));

            if (path == "/admin/panel/api/clients/get/user@example.com") return Json(ClientByEmailJson(full));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        Assert.Equal(0, await service.SendRemindersAsync(CancellationToken.None));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task SendReminders_LegacyDisabledOrOutsideWindow_SkipsWithoutDetailFetch()
    {
        var (service, bot) = CreateService(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/admin/panel/api/clients/list")
                return Json(ListJson(
                    Summary("tg12345", DateTimeOffset.UtcNow.AddDays(1)),
                    Summary("off@example.com", DateTimeOffset.UtcNow.AddDays(1), false),
                    Summary("far@example.com", DateTimeOffset.UtcNow.AddDays(30))));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        Assert.Equal(0, await service.SendRemindersAsync(CancellationToken.None));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task SendReminders_ClientWithoutTelegram_Skips()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        var full = FullClient("user@example.com", 0, "Cancelled 2026-01-01");

        var (service, bot) = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list") return Json(ListJson(Summary("user@example.com", expiry)));

            if (path == "/admin/panel/api/clients/get/user@example.com") return Json(ClientByEmailJson(full));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        Assert.Equal(0, await service.SendRemindersAsync(CancellationToken.None));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public void TimeUntilNextRun_BeforeHour_WaitsSameDay()
    {
        var now = new DateTime(2026, 9, 9, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.FromHours(1), ExpiryReminderService.TimeUntilNextRun(now, 9));
    }

    [Fact]
    public void BuildReminderText_Overdue_SaysExpired()
    {
        var text = ExpiryReminderService.BuildReminderText(-5, new DateOnly(2026, 9, 1),
            "https://tribute.test/sub", "https://t.me/support");

        Assert.Contains("expired on 2026-09-01", text);
        Assert.Contains("https://tribute.test/sub", text);
    }

    [Fact]
    public void TimeUntilNextRun_AfterHour_WaitsNextDay()
    {
        var now = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.FromHours(23), ExpiryReminderService.TimeUntilNextRun(now, 9));
    }
}