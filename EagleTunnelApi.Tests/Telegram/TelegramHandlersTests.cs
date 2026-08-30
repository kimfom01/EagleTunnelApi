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

    private static PanelClient ActiveClient() => new(
        Uuid: "uuid-1",
        Email: "user@example.com",
        Enable: true,
        ExpiryTime: DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
        TgId: TelegramId,
        TotalGB: 100L * 1024 * 1024 * 1024,
        Comment: "comment",
        LimitIp: 2,
        LimitHwid: 2,
        TrafficReset: "monthly",
        TrafficResetDay: 1,
        Reset: 0,
        Security: "auto",
        SubId: "sub123",
        Flow: "xtls-rprx-vision",
        Id: 1,
        InboundIds: null
    );

    private static PanelClient TestClient(bool enable) => ActiveClient() with { Enable = enable };

    private static string ClientListJson(PanelClient client) =>
        JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            new List<PanelClientResponse> { new(client, null, new List<int> { 1 }, 10L * 1024 * 1024 * 1024) }));

    private static string ClientByEmailJson(PanelClient client) =>
        JsonSerializer.Serialize(new PanelApiResponse<PanelClientResponse>(true, "ok",
            new PanelClientResponse(client, null, new List<int> { 1 }, 10L * 1024 * 1024 * 1024)));

    private static string EmptyListJson() =>
        JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            new List<PanelClientResponse>()));

    private static string SuccessJson() =>
        JsonSerializer.Serialize(new PanelApiResponse<object>(true, "ok", null));

    private static HttpResponseMessage Json(string rawJson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
    };

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
            DefaultInboundIds = new[] { 1, 2 },
            WebhookPath = "/webhook/telegram",
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

    private static BotUpdate TextUpdate(string text, BotUser? from = null) => new()
    {
        Message = new BotMessage
        {
            Id = 1,
            Text = text,
            Chat = new BotChat { Id = TelegramId, Type = ChatType.Private },
            From = from ?? new BotUser { Id = TelegramId }
        }
    };

    private static BotUpdate CallbackUpdate(string data) => new()
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

    [Fact]
    public async Task Start_NewUser_AutoRegisters_WithSanitizedUsernameAndComment()
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
                return getCount == 1 ? Json(EmptyListJson()) : Json(ClientListJson(TestClient(enable: false)));
            }

            if (path == "/admin/panel/api/clients/add")
            {
                return Json(SuccessJson());
            }

            if (path == "/admin/panel/api/clients/bulkDisable")
            {
                return Json(SuccessJson());
            }

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

        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");

        var addRequest = requests.Single(r => r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var payload = await ReadJson(addRequest);

        var client = payload.GetProperty("client");
        Assert.Equal($"tg{TelegramId}", client.GetProperty("email").GetString());
        Assert.False(client.GetProperty("enable").GetBoolean());
        Assert.Equal(300L * 1024 * 1024 * 1024, client.GetProperty("totalGB").GetInt64());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());
        Assert.Equal(2, client.GetProperty("limitHwid").GetInt32());
        Assert.Equal("monthly", client.GetProperty("trafficReset").GetString());
        Assert.Equal(1, client.GetProperty("trafficResetDay").GetInt32());
        Assert.Equal("xtls-rprx-vision", client.GetProperty("flow").GetString());
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("password").GetString()!);
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("subId").GetString()!);
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("auth").GetString()!);

        var comment = client.GetProperty("comment").GetString()!;
        Assert.Contains("John 😀 Doe Smith", comment);
        Assert.Contains("@john_smith", comment);
        Assert.Contains($"Telegram ID: {TelegramId}", comment);

        Assert.Equal(new[] { 1, 2 }, payload.GetProperty("inboundIds").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray());

        var menu = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", menu.Body);
        Assert.Contains("Support", menu.Body);
        Assert.DoesNotContain("start_registration", menu.Body);
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
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(TestClient(enable: false))));

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
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()), adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/admin"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Admin Panel", send.Body);
        Assert.Contains("\"admin:lookup\"", send.Body);
        Assert.Contains("\"admin:grant\"", send.Body);
    }

    [Fact]
    public async Task Start_AdminAccount_ShowsAdminButtonInMainMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())), adminIds: [TelegramId]);

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
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())), adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Admin), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("Admin Panel", edit.Body);
        Assert.DoesNotContain("Copy VPN Link", edit.Body);
    }

    [Fact]
    public async Task AdminLookup_WithId_ShowsClientCard()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())), adminIds: [TelegramId]);

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

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return Json(ClientListJson(ActiveClient()));
            }

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
            {
                return Json(ClientByEmailJson(ActiveClient()));
            }

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
            {
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

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

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return Json(ClientListJson(ActiveClient()));
            }

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
            {
                return Json(ClientByEmailJson(ActiveClient()));
            }

            if (path == "/admin/panel/api/clients/bulkDisable")
            {
                disableCalls++;
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

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

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return Json(ClientListJson(ActiveClient()));
            }

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
            {
                return Json(ClientByEmailJson(ActiveClient()));
            }

            if (path == "/admin/panel/api/clients/bulkEnable")
            {
                enableCalls++;
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

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
    public async Task AdminDeviceLimit_ConfirmsAndUpdatesLimitIp()
    {
        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return Json(ClientListJson(ActiveClient()));
            }

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
            {
                return Json(ClientByEmailJson(ActiveClient()));
            }

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
            {
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLimit), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(TelegramId.ToString()), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("5"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmLimit}:user@example.com:5"), CancellationToken.None);

        var updateRequest = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Device limit set"));
        Assert.Contains("5", updateRequest.Body);
    }

    [Fact]
    public async Task AdminResetTraffic_ConfirmsAndCallsResetEndpoint()
    {
        var resetCalls = 0;

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return Json(ClientListJson(ActiveClient()));
            }

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
            {
                return Json(ClientByEmailJson(ActiveClient()));
            }

            if (path == "/admin/panel/api/clients/resetTraffic/user@example.com")
            {
                resetCalls++;
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

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
        var (handler, bot) = CreateHandler(_ => Json(ClientByEmailJson(ActiveClient())), adminIds: [TelegramId]);

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

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return Json(ClientListJson(ActiveClient()));
            }

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
            {
                return Json(ClientByEmailJson(ActiveClient()));
            }

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
            {
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

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

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return Json(ClientListJson(ActiveClient()));
            }

            if (path == $"/admin/panel/api/clients/get/{"user@example.com"}")
            {
                return Json(ClientByEmailJson(ActiveClient()));
            }

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
            {
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

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
                Id: i, Email: $"user{i:00}@example.com", SubId: $"sub{i:00}", Uuid: null, TotalGB: 100,
                ExpiryTime: 0, Enable: i % 2 == 0, InboundIds: null, Traffic: null))
            .ToList();

        var listJson = JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientSummary>>(true, "ok", summaries));

        var (handler, bot) = CreateHandler(_ => Json(listJson), adminIds: [TelegramId]);

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
                Id: i, Email: $"user{i:00}@example.com", SubId: $"sub{i:00}", Uuid: null, TotalGB: 100,
                ExpiryTime: 0, Enable: i % 2 == 0, InboundIds: null, Traffic: null))
            .ToList();

        var listJson = JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientSummary>>(true, "ok", summaries));

        var (handler, bot) = CreateHandler(_ => Json(listJson), adminIds: [TelegramId]);

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
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())), adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/admin"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminExit), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("Welcome to Eagle Tunnel Network", edit.Body);
        Assert.DoesNotContain("Admin Panel", edit.Body);
    }
}