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
using BotChat = Telegram.Bot.Types.Chat;
using BotMessage = Telegram.Bot.Types.Message;
using BotUpdate = Telegram.Bot.Types.Update;
using BotUser = Telegram.Bot.Types.User;
using BotCallbackQuery = Telegram.Bot.Types.CallbackQuery;
using ChatType = Telegram.Bot.Types.Enums.ChatType;

namespace EagleTunnelApi.Tests.Telegram;

public class TelegramHandlersTests
{
    private const string PanelBaseUri = "https://panel.test";
    private const long TelegramId = 12345;

    private static PanelClient ActiveClient()
    {
        return new PanelClient(
            "uuid-1",
            "user@example.com",
            true,
            DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
            TelegramId,
            100L * 1024 * 1024 * 1024,
            "comment",
            2,
            2,
            "monthly",
            1,
            0,
            "auto",
            "sub123",
            "xtls-rprx-vision",
            1,
            null
        );
    }

    private static PanelClient TestClient(bool enable)
    {
        return ActiveClient() with { Enable = enable };
    }

    private static string ClientListJson(PanelClient client)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            new List<PanelClientResponse> { new(client, null, new List<int> { 1 }, 10L * 1024 * 1024 * 1024) }));
    }

    private static string ClientByEmailJson(PanelClient client)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<PanelClientResponse>(true, "ok",
            new PanelClientResponse(client, null, new List<int> { 1 }, 10L * 1024 * 1024 * 1024)));
    }

    private static string EmptyListJson()
    {
        return JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            new List<PanelClientResponse>()));
    }

    private static string FailureJson(string message)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<object>(false, message, null));
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

    private static (TelegramHandlers Handler, FakeTelegramBotClient Bot) CreateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> panelResponder,
        long[]? adminIds = null)
    {
        var bot = new FakeTelegramBotClient();

        var telegramOptions = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            TributeSubscriptionUrl = "https://tribute.test",
            BotUsername = "TestBot",
            DefaultInboundIds = new[] { 1, 2 },
            WebhookPath = "/webhooks/telegram",
            AdminIds = adminIds ?? Array.Empty<long>()
        });

        var panelClient = new PanelApiClient(
            new HttpClient(new StubHttpMessageHandler(panelResponder)) { BaseAddress = new Uri(PanelBaseUri) },
            NullLogger<PanelApiClient>.Instance);

        var adminPanelService = new AdminPanelService(NullLogger<AdminPanelService>.Instance, panelClient);

        var handler = new TelegramHandlers(new SessionStore(), panelClient, adminPanelService,
            telegramOptions, Options.Create(new PanelOptions { BaseUri = PanelBaseUri, ApiKey = "key" }),
            NullLogger<TelegramHandlers>.Instance);

        return (handler, bot);
    }

    private static BotUpdate TextUpdate(string text, BotUser? from = null)
    {
        return new BotUpdate
        {
            Message = new BotMessage
            {
                Id = 1,
                Text = text,
                Chat = new BotChat { Id = TelegramId, Type = ChatType.Private },
                From = from ?? new BotUser { Id = TelegramId }
            }
        };
    }

    private static BotUpdate CallbackUpdate(string data)
    {
        return new BotUpdate
        {
            CallbackQuery = new BotCallbackQuery
            {
                Id = "cb-1",
                Data = data,
                From = new BotUser { Id = TelegramId },
                Message = new BotMessage
                {
                    Id = 10,
                    Text = "menu",
                    Chat = new BotChat { Id = TelegramId, Type = ChatType.Private }
                }
            }
        };
    }

    [Fact]
    public async Task Start_NewUser_PromptsForEmail_WithoutCreating()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);

            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(EmptyListJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var from = new BotUser
        {
            Id = TelegramId,
            FirstName = "John 😀 Doe",
            LastName = "Smith",
            Username = "john_smith"
        };
        await handler.HandleUpdateAsync(bot, TextUpdate("/start", from), CancellationToken.None);

        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");

        var prompt = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("email address", prompt.Body);
    }

    private static PanelClient ReferrerAccount(long tgId = 777, string email = "friend@example.com")
    {
        return ActiveClient() with
        {
            Email = email,
            TgId = tgId,
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds()
        };
    }

    private static (TelegramHandlers Handler, FakeTelegramBotClient Bot) CreateWizardHandler(
        List<HttpRequestMessage> requests, PanelClient referrer, string newEmail = "newbie@example.com")
    {
        var getCount = 0;

        return CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                if (getCount <= 2) return Json(EmptyListJson());

                var created = ActiveClient() with
                {
                    Email = newEmail,
                    Enable = false,
                    ExpiryTime = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds(),
                    Comment = "Telegram ID: 12345"
                };
                return Json(ClientListJson(created));
            }

            if (path == "/admin/panel/api/clients/get/tgId/777") return Json(ClientListJson(referrer));

            if (path == $"/admin/panel/api/clients/get/{newEmail}") return Json(FailureJson("not found"));

            if (path == $"/admin/panel/api/clients/get/{referrer.Email}") return Json(ClientByEmailJson(referrer));

            if (path == "/admin/panel/api/clients/add" ||
                path == "/admin/panel/api/clients/bulkDisable")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });
    }

    [Fact]
    public async Task Wizard_WithDeepLink_Confirm_CreatesWithReferralTags()
    {
        var requests = new List<HttpRequestMessage>();
        var referrer = ReferrerAccount();
        var (handler, bot) = CreateWizardHandler(requests, referrer);

        await handler.HandleUpdateAsync(bot, TextUpdate("/start ref_777"), CancellationToken.None);

        var emailPrompt = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("email address", emailPrompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var confirm = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("friend@example.com", confirm.Body);
        Assert.Contains("\"ref:confirm\"", confirm.Body);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.RefConfirm), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var payload = await ReadJson(addRequest);
        var client = payload.GetProperty("client");

        Assert.Equal("newbie@example.com", client.GetProperty("email").GetString());
        Assert.False(client.GetProperty("enable").GetBoolean());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());

        var comment = client.GetProperty("comment").GetString()!;
        Assert.Contains("Referred by: friend@example.com", comment);
        Assert.Contains("Referrer tgId: 777", comment);
        Assert.Contains($"Telegram ID: {TelegramId}", comment);

        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");

        var menu = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", menu.Body);
    }

    [Fact]
    public async Task Wizard_Skip_CreatesWithoutReferralTags()
    {
        var requests = new List<HttpRequestMessage>();
        var (handler, bot) = CreateWizardHandler(requests, ReferrerAccount());

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var referrerPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("\"ref:skip\"", referrerPrompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var client = (await ReadJson(addRequest)).GetProperty("client");

        Assert.Equal("newbie@example.com", client.GetProperty("email").GetString());
        Assert.DoesNotContain("Referred by", client.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Wizard_ManualReferrer_UnknownEmail_SavesPending()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return getCount <= 2 ? Json(EmptyListJson()) : Json(ClientListJson(TestClient(false)));
            }

            if (path == "/admin/panel/api/clients/get/newbie@example.com" ||
                path == "/admin/panel/api/clients/get/ghost@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/add" ||
                path == "/admin/panel/api/clients/bulkDisable")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("ghost@example.com"), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var comment = (await ReadJson(addRequest)).GetProperty("client").GetProperty("comment").GetString()!;

        Assert.Contains("Referred by (pending): ghost@example.com", comment);
    }

    [Fact]
    public async Task Wizard_SelfEmail_AsReferrer_Rejected()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(EmptyListJson());

            if (path == "/admin/panel/api/clients/get/newbie@example.com") return Json(FailureJson("not found"));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var rejection = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("yourself", rejection.Body);
        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
    }

    [Fact]
    public async Task Wizard_SelfLink_Ignored_FallsBackToManual()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(EmptyListJson());

            if (path == "/admin/panel/api/clients/get/newbie@example.com") return Json(FailureJson("not found"));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate($"/start ref_{TelegramId}"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var referrerPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.DoesNotContain("ref:confirm", referrerPrompt.Body);
        Assert.Contains("\"ref:skip\"", referrerPrompt.Body);
    }

    [Fact]
    public async Task Wizard_InvalidAndTakenEmail_Reprompt()
    {
        var taken = ActiveClient() with { Email = "taken@example.com", TgId = 999 };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(EmptyListJson());

            if (path == "/admin/panel/api/clients/get/taken@example.com") return Json(ClientByEmailJson(taken));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("not-an-email"), CancellationToken.None);

        var invalid = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("valid email", invalid.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("taken@example.com"), CancellationToken.None);

        var takenMsg = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("already registered", takenMsg.Body);
    }

    [Fact]
    public async Task Wizard_LegacyProvisioned_MigratesEmailOnly()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;
        var legacy = ActiveClient() with
        {
            Email = $"tg{TelegramId}",
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds()
        };
        var migrated = legacy with { Email = "newbie@example.com" };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return Json(ClientListJson(getCount <= 2 ? legacy : migrated));
            }

            if (path == "/admin/panel/api/clients/get/newbie@example.com") return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/update/tg12345") return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var prompt = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("email address", prompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        var body = await ReadJson(updateRequest);

        Assert.Equal("newbie@example.com", body.GetProperty("email").GetString());
        Assert.DoesNotContain("Referred by", body.GetProperty("comment").GetString());
        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
    }

    [Fact]
    public async Task Wizard_LegacyUnprovisioned_GetsReferrerStep()
    {
        var legacy = ActiveClient() with
        {
            Email = $"tg{TelegramId}",
            Enable = false,
            ExpiryTime = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds()
        };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(legacy));

            if (path == "/admin/panel/api/clients/get/newbie@example.com") return Json(FailureJson("not found"));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var referrerPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("\"ref:skip\"", referrerPrompt.Body);
    }

    [Fact]
    public async Task ReferrerCommand_RefusedWhenProvisioned()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/referrer"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("new subscribers", send.Body);
    }

    [Fact]
    public async Task ReferrerCommand_UpdatesAttributionWhenUnprovisioned()
    {
        var requests = new List<HttpRequestMessage>();
        var account = ActiveClient() with
        {
            Enable = false,
            ExpiryTime = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds(),
            Comment = "comment"
        };
        var friend = ReferrerAccount();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(account));

            if (path == $"/admin/panel/api/clients/get/{friend.Email}") return Json(ClientByEmailJson(friend));

            if (path == $"/admin/panel/api/clients/update/{account.Email}") return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/referrer"), CancellationToken.None);

        var prompt = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("referrer", prompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("friend@example.com"), CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        var comment = (await ReadJson(updateRequest)).GetProperty("comment").GetString()!;

        Assert.Contains("Referred by: friend@example.com", comment);
        Assert.Contains("Referrer tgId: 777", comment);
    }

    [Fact]
    public async Task ReferralScreen_RendersInviteLink()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Referral), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("Invite Friends", edit.Body);
        Assert.Contains($"t.me/TestBot?start=ref_{TelegramId}", edit.Body);
        Assert.Contains("Copy Invite Link", edit.Body);
    }

    [Fact]
    public async Task MainMenu_ContainsInviteButton()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("\"referral\"", send.Body);
        Assert.Contains("Invite Friends", send.Body);
    }

    [Fact]
    public async Task AdminLookup_ShowsReferralCreditAndCancelState()
    {
        var tagged = ActiveClient() with
        {
            Comment = "John · Telegram ID: 12345 · Referrer tgId: 777 " +
                      "· Referred by: friend@example.com · Referral credit: 30d " +
                      "· Referral bonus paid · Cancelled 2026-09-01"
        };

        var (handler, bot) = CreateHandler(_ => Json(ClientByEmailJson(tagged)), adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLookup), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user@example.com"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Referred by", send.Body);
        Assert.Contains("friend@example.com", send.Body);
        Assert.Contains("777", send.Body);
        Assert.Contains("30d", send.Body);
        Assert.Contains("bonus paid", send.Body);
        Assert.Contains("Cancelled", send.Body);
    }

    [Fact]
    public async Task AdminMenu_ContainsNudgeButton()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()), adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/admin"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("\"admin:nudge\"", send.Body);
    }

    [Fact]
    public async Task AdminNudge_ConfirmsAndSendsReminderWithMarker()
    {
        var requests = new List<HttpRequestMessage>();
        var expiry = DateTimeOffset.UtcNow.AddDays(2);
        var expiryDate = DateOnly.FromDateTime(expiry.UtcDateTime);
        var client = ActiveClient() with
        {
            TgId = 99999,
            ExpiryTime = expiry.ToUnixTimeMilliseconds(),
            Comment = "some comment · Cancelled 2026-01-01"
        };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/user@example.com")
            {
                return Json(ClientByEmailJson(client));
            }

            if (path == "/admin/panel/api/clients/update/user@example.com")
            {
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminNudge), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user@example.com"), CancellationToken.None);

        var confirm = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("admin:confirm:nudge:user@example.com", confirm.Body);

        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmNudge}:user@example.com"), CancellationToken.None);

        var dm = Assert.Single(bot.Requests,
            r => r.MethodName == "sendMessage" && r.Body.Contains("99999"));
        Assert.Contains("2 days", dm.Body);
        Assert.Contains("https://tribute.test", dm.Body);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/clients/update/"));
        Assert.Contains($"Reminder 2d sent for {expiryDate:yyyy-MM-dd}",
            (await ReadJson(updateRequest)).GetProperty("comment").GetString());

        var done = bot.Requests.Last(r => r.MethodName == "editMessageText");
        Assert.Contains("Reminder sent", done.Body);
    }

    [Fact]
    public async Task AdminNudge_WithoutTelegram_Refuses()
    {
        var client = ActiveClient() with { TgId = 0 };

        var (handler, bot) = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/admin/panel/api/clients/get/user@example.com")
            {
                return Json(ClientByEmailJson(client));
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminNudge), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmNudge}:user@example.com"), CancellationToken.None);

        var done = bot.Requests.Last(r => r.MethodName == "editMessageText");
        Assert.Contains("no Telegram", done.Body);
        Assert.DoesNotContain(bot.Requests, r => r.MethodName == "sendMessage" && r.Body.Contains("2 days"));
    }

    [Fact]
    public async Task Start_ExistingActiveUser_RendersStatusAndMainMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Subscription: Active", send.Body);
        Assert.Contains("Copy VPN Link", send.Body);
        Assert.Contains("How to Connect", send.Body);
    }

    [Fact]
    public async Task Start_ExistingDisabledUser_ShowsSupportWithoutConnect()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(TestClient(false))));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Subscription: Not Active", send.Body);
        Assert.Contains("Support", send.Body);
        Assert.DoesNotContain("Copy VPN Link", send.Body);
    }

    [Fact]
    public async Task Support_IncludesPrefillTemplate_WithUsernameAndTelegramId()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Support), CancellationToken.None);

        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Message Support"));
        Assert.Contains("My%20username%3A%20user%40example.com", edit.Body);
        Assert.Contains($"My%20Telegram%20ID%3A%20{TelegramId}", edit.Body);
        Assert.Contains("text=", edit.Body);
    }

    [Fact]
    public async Task Connect_ShowsGuide()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Connect), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("How to Connect", edit.Body);
        Assert.Contains("INCY", edit.Body);
        Assert.Contains("Copy VPN Link", edit.Body);

        Assert.Contains(bot.Requests, r => r.MethodName == "answerCallbackQuery");
    }

    [Fact]
    public async Task Back_RestoresMainMenuText_NotJustKeyboard()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Connect), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Back), CancellationToken.None);

        var mainMenuEdits = bot.Requests.Where(r => r.MethodName == "editMessageText").ToList();

        var backEdit = mainMenuEdits.Last(r => r.Body.Contains("Subscription: Active"));
        Assert.Contains("Welcome to Eagle Tunnel Network", backEdit.Body);
        Assert.DoesNotContain("Choose a subscription plan", backEdit.Body);
    }

    [Fact]
    public async Task Back_AfterConnectGuide_RestoresMainMenuText()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Connect), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Back), CancellationToken.None);

        var edits = bot.Requests.Where(r => r.MethodName == "editMessageText").ToList();

        Assert.Contains(edits, r => r.Body.Contains("INCY"));

        var backEdit = edits.Last(r => r.Body.Contains("Subscription: Active"));
        Assert.Contains("Welcome to Eagle Tunnel Network", backEdit.Body);
        Assert.DoesNotContain("INCY", backEdit.Body);
    }

    [Theory]
    [InlineData(MenuService.Connect, "INCY")]
    [InlineData(MenuService.Support, "Message Support")]
    public async Task Back_FromAnySubMenu_RestoresMainMenuText(string menuCallback, string subMenuMarker)
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(menuCallback), CancellationToken.None);

        var subMenuEdit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains(subMenuMarker));

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Back), CancellationToken.None);

        var backEdits = bot.Requests.Where(r => r.MethodName == "editMessageText")
            .Where(r => r != subMenuEdit).ToList();

        var backEdit = Assert.Single(backEdits);
        Assert.Contains("Welcome to Eagle Tunnel Network", backEdit.Body);
        Assert.Contains("Subscription: Active", backEdit.Body);
        Assert.DoesNotContain(subMenuMarker, backEdit.Body);
    }

    private static async Task<JsonElement> ReadJson(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task AdminCommand_NonAdmin_Denied()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()));

        await handler.HandleUpdateAsync(bot, TextUpdate("/admin"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("not authorized", send.Body);
    }

    [Fact]
    public async Task AdminCommand_Admin_ShowsAdminMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()), [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/admin"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Admin Panel", send.Body);
        Assert.Contains("\"admin:lookup\"", send.Body);
        Assert.Contains("\"admin:grant\"", send.Body);
    }

    [Fact]
    public async Task Start_AdminAccount_ShowsAdminButtonInMainMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())), [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("\"admin\"", send.Body);
        Assert.Contains("Admin", send.Body);
    }

    [Fact]
    public async Task Start_NonAdmin_DoesNotShowAdminButtonInMainMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.DoesNotContain("\"admin\"", send.Body);
        Assert.DoesNotContain("Admin Panel", send.Body);
    }

    [Fact]
    public async Task Start_AdminAccount_AdminButtonOpensAdminMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())), [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Admin), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("Admin Panel", edit.Body);
        Assert.DoesNotContain("Copy VPN Link", edit.Body);
    }

    [Fact]
    public async Task AdminLookup_WithId_ShowsClientCard()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())), [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLookup), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(TelegramId.ToString()), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("User lookup", send.Body);
        Assert.Contains("user@example.com", send.Body);
        Assert.Contains("Active", send.Body);
        Assert.Contains("\"admin:list\"", send.Body);
    }

    [Fact]
    public async Task AdminGrant_ConfirmsAndUpdatesExpiry()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(ActiveClient()));

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
                return Json(ClientByEmailJson(ActiveClient()));

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminGrant), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(TelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("30"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmGrant}:user@example.com:30"), CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));

        var payload = await ReadJson(updateRequest);
        Assert.True(payload.GetProperty("enable").GetBoolean());
        Assert.InRange(payload.GetProperty("expiryTime").GetInt64(),
            DateTimeOffset.UtcNow.AddDays(40).AddMinutes(-1).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddDays(40).AddMinutes(1).ToUnixTimeMilliseconds());

        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Granted"));
        Assert.Contains("30", edit.Body);
    }

    [Fact]
    public async Task AdminBan_ConfirmsAndDisablesClient()
    {
        var disableCalls = 0;

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(ActiveClient()));

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
                return Json(ClientByEmailJson(ActiveClient()));

            if (path == "/admin/panel/api/clients/bulkDisable")
            {
                disableCalls++;
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminBan), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(TelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmBan}:user@example.com"), CancellationToken.None);

        Assert.Equal(1, disableCalls);
        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Banned"));
        Assert.Contains("user@example.com", edit.Body);
    }

    [Fact]
    public async Task AdminUnban_ConfirmsAndEnablesClient()
    {
        var enableCalls = 0;

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(ActiveClient()));

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
                return Json(ClientByEmailJson(ActiveClient()));

            if (path == "/admin/panel/api/clients/bulkEnable")
            {
                enableCalls++;
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminUnban), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(TelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmUnban}:user@example.com"), CancellationToken.None);

        Assert.Equal(1, enableCalls);
        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Unbanned"));
        Assert.Contains("user@example.com", edit.Body);
    }

    [Fact]
    public async Task AdminDeviceLimit_ConfirmsAndUpdatesLimitHwid()
    {
        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(ActiveClient()));

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
                return Json(ClientByEmailJson(ActiveClient()));

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLimit), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(TelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("5"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmLimit}:user@example.com:5"), CancellationToken.None);

        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Device limit set"));
        Assert.Contains("5", edit.Body);
    }

    [Fact]
    public async Task AdminResetTraffic_ConfirmsAndCallsResetEndpoint()
    {
        var resetCalls = 0;

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(ActiveClient()));

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
                return Json(ClientByEmailJson(ActiveClient()));

            if (path == "/admin/panel/api/clients/resetTraffic/user@example.com")
            {
                resetCalls++;
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminReset), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(TelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmReset}:user@example.com"), CancellationToken.None);

        Assert.Equal(1, resetCalls);
        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Traffic reset"));
        Assert.Contains("user@example.com", edit.Body);
    }

    [Fact]
    public async Task AdminLookup_ByEmail_ShowsClientCard()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientByEmailJson(ActiveClient())), [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLookup), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user@example.com"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("User lookup", send.Body);
        Assert.Contains("user@example.com", send.Body);
        Assert.Contains("Active", send.Body);
    }

    [Fact]
    public async Task AdminLink_ConfirmsAndUpdatesTelegramId()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(ActiveClient()));

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
                return Json(ClientByEmailJson(ActiveClient()));

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        const long newTelegramId = 99999;

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLink), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(newTelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmLink}:user@example.com:{newTelegramId}"), CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));

        var payload = await ReadJson(updateRequest);
        Assert.Equal(newTelegramId, payload.GetProperty("tgId").GetInt64());

        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Linked"));
        Assert.Contains("99999", edit.Body);
        Assert.Contains("user@example.com", edit.Body);
    }

    [Fact]
    public async Task AdminLink_LookupByTelegramId_ThenLinkTelegramId()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}") return Json(ClientListJson(ActiveClient()));

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
                return Json(ClientByEmailJson(ActiveClient()));

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        const long newTelegramId = 88888;

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLink), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(TelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(newTelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmLink}:user@example.com:{newTelegramId}"), CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));

        var payload = await ReadJson(updateRequest);
        Assert.Equal(newTelegramId, payload.GetProperty("tgId").GetInt64());
    }

    [Fact]
    public async Task AdminList_RendersPaginatedClients()
    {
        var summaries = Enumerable.Range(1, 25)
            .Select(i => new PanelClientSummary(
                i, $"user{i:00}@example.com", $"sub{i:00}", null, 100,
                0, i % 2 == 0, null, null))
            .ToList();

        var listJson = JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientSummary>>(true, "ok", summaries));

        var (handler, bot) = CreateHandler(_ => Json(listJson), [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminList), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("25 total", edit.Body);
        Assert.Contains("user01@example.com", edit.Body);
        Assert.Contains("user20@example.com", edit.Body);
        Assert.DoesNotContain("user21@example.com", edit.Body);
        Assert.Contains("1/2", edit.Body);
        Assert.Contains("\"admin:list:next:1\"", edit.Body);
    }

    [Fact]
    public async Task AdminList_NextPage_ShowsSecondPage()
    {
        var summaries = Enumerable.Range(1, 25)
            .Select(i => new PanelClientSummary(
                i, $"user{i:00}@example.com", $"sub{i:00}", null, 100,
                0, i % 2 == 0, null, null))
            .ToList();

        var listJson = JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientSummary>>(true, "ok", summaries));

        var (handler, bot) = CreateHandler(_ => Json(listJson), [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminList), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate($"{MenuService.AdminListNext}:1"), CancellationToken.None);

        var edits = bot.Requests.Where(r => r.MethodName == "editMessageText").ToList();
        var secondPage = edits[^1];
        Assert.Contains("user21@example.com", secondPage.Body);
        Assert.Contains("user25@example.com", secondPage.Body);
        Assert.DoesNotContain("user01@example.com", secondPage.Body);
        Assert.Contains("2/2", secondPage.Body);
    }

    [Fact]
    public async Task AdminExit_ReturnsToMainMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())), [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/admin"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminExit), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("Welcome to Eagle Tunnel Network", edit.Body);
        Assert.DoesNotContain("Admin Panel", edit.Body);
    }
}