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
        Reset: 0,
        Security: "auto",
        SubId: "sub123",
        Flow: "xtls-rprx-vision",
        Id: 1
    );

    private static PanelClient TestClient(bool enable) => ActiveClient() with { Enable = enable };

    private static string ClientListJson(PanelClient client) =>
        JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            new List<PanelClientResponse> { new(client, null, new List<int> { 1 }, 10L * 1024 * 1024 * 1024) }));

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
        Func<HttpRequestMessage, HttpResponseMessage> panelResponder)
    {
        var bot = new FakeTelegramBotClient();

        var telegramOptions = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            TributeSubscriptionUrl = "https://tribute.test",
            DefaultInboundIds = new[] { 1, 2 },
            WebhookPath = "/webhook/telegram"
        });

        var panelClient = new PanelApiClient(
            new HttpClient(new StubHttpMessageHandler(panelResponder)) { BaseAddress = new Uri(PanelBaseUri) },
            NullLogger<PanelApiClient>.Instance);

        var handler = new TelegramHandlers(new SessionStore(), panelClient, telegramOptions,
            Options.Create(new PanelOptions { BaseUri = PanelBaseUri, ApiKey = "key" }),
            NullLogger<TelegramHandlers>.Instance);

        return (handler, bot);
    }

    private static BotUpdate TextUpdate(string text) => new()
    {
        Message = new BotMessage
        {
            Id = 1,
            Text = text,
            Chat = new BotChat { Id = TelegramId, Type = ChatType.Private },
            From = new BotUser { Id = TelegramId }
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
    public async Task Start_NewUser_SendsRegistrationPrompt()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var welcome = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", welcome.Body);
        Assert.Contains("start_registration", welcome.Body);
    }

    [Fact]
    public async Task Start_ExistingActiveUser_RendersStatusAndMainMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Subscription: Active", send.Body);
        Assert.Contains("setup", send.Body);
    }

    [Fact]
    public async Task Start_ExistingDisabledUser_ShowsSubscribeWithoutSetup()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(TestClient(enable: false))));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Subscription: Not Active", send.Body);
        Assert.DoesNotContain("setup", send.Body);
    }

    [Fact]
    public async Task RegistrationFlow_Completes_WithAddAndBulkDisable()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return Json(EmptyListJson());
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

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.StartRegistration), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("John"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("skip"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("Doe"), CancellationToken.None);

        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");

        var addRequest = requests.Single(r => r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var payload = await ReadJson(addRequest);

        var client = payload.GetProperty("client");
        Assert.Equal("JohnDoe", client.GetProperty("email").GetString());
        Assert.False(client.GetProperty("enable").GetBoolean());
        Assert.Equal(300L * 1024 * 1024 * 1024, client.GetProperty("totalGB").GetInt64());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());
        Assert.Equal("xtls-rprx-vision", client.GetProperty("flow").GetString());
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("password").GetString()!);
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("subId").GetString()!);
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("auth").GetString()!);

        Assert.Equal(new[] { 1, 2 }, payload.GetProperty("inboundIds").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray());

        Assert.Contains(bot.Requests, r => r.MethodName == "sendMessage" && r.Body.Contains("Registration complete"));
    }

    [Fact]
    public async Task SetupNavigation_EditsReplyMarkup()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Setup), CancellationToken.None);

        var editMarkup = Assert.Single(bot.Requests, r => r.MethodName == "editMessageReplyMarkup");
        Assert.Contains("setup_install", editMarkup.Body);

        Assert.Contains(bot.Requests, r => r.MethodName == "answerCallbackQuery");
    }

    private static async Task<JsonElement> ReadJson(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}