using System.Net;
using System.Text;
using System.Text.Json;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.Domain.Referrals;
using EagleTunnelApi.Infrastructure.PanelApi;
using EagleTunnelApi.Infrastructure.PanelApi.Models;
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

    private static PanelClientDto ActiveClient()
    {
        return new PanelClientDto(
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

    private static PanelClientDto TestClient(bool enable)
    {
        return ActiveClient() with { Enable = enable };
    }

    private static string ClientListJson(PanelClientDto client)
    {
        return JsonSerializer.Serialize(new PanelApiResponseDto<List<PanelClientResponseDto>>(true, "ok",
            new List<PanelClientResponseDto> { new(client, null, new List<int> { 1 }, 10L * 1024 * 1024 * 1024) }));
    }

    private static string ClientByEmailJson(PanelClientDto client)
    {
        return JsonSerializer.Serialize(new PanelApiResponseDto<PanelClientResponseDto>(true, "ok",
            new PanelClientResponseDto(client, null, new List<int> { 1 }, 10L * 1024 * 1024 * 1024)));
    }

    private static string EmptyListJson()
    {
        return JsonSerializer.Serialize(new PanelApiResponseDto<List<PanelClientResponseDto>>(true, "ok",
            new List<PanelClientResponseDto>()));
    }

    private static string FailureJson(string message)
    {
        return JsonSerializer.Serialize(new PanelApiResponseDto<object>(false, message, null));
    }

    private static string SuccessJson()
    {
        return JsonSerializer.Serialize(new PanelApiResponseDto<object>(true, "ok", null));
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
        long[]? adminIds = null,
        int trialHours = 2)
    {
        var bot = new FakeTelegramBotClient();

        var telegramOptions = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            TributeSubscriptionUrl = "https://tribute.test",
            BotUsername = "TestBot",
            TrialDurationHours = trialHours,
            // The effective values come from PanelDirectory; this fallback must
            // not be used when the directory has an authoritative set.
            DefaultInboundIds = new[] { 99 },
            WebhookPath = "/webhooks/telegram",
            AdminIds = adminIds ?? Array.Empty<long>()
        });

        var panelClient = new PanelApiGateway(
            new HttpClient(new StubHttpMessageHandler(panelResponder)) { BaseAddress = new Uri(PanelBaseUri) },
            NullLogger<PanelApiGateway>.Instance);

        var adminPanelService = new AdminPanelService(NullLogger<AdminPanelService>.Instance, panelClient,
            bot, telegramOptions);

        var settlementRunner = new ReferralSettlementRunner(adminPanelService, new TestHostApplicationLifetime(),
            NullLogger<ReferralSettlementRunner>.Instance);

        var handler = new TelegramHandlers(new SessionStore(), panelClient,
            new StubPanelDirectory { DefaultInboundIds = [1, 2] }, adminPanelService, settlementRunner,
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

    private static PanelClientDto ReferrerAccount(long tgId = 777, string email = "friend@example.com")
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
        List<HttpRequestMessage> requests, PanelClientDto referrer, string newEmail = "newbie@example.com")
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
                    Enable = true,
                    ExpiryTime = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
                    Comment = "Telegram ID: 12345 · Trial until 2099-01-01 00:00 UTC"
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
        Assert.DoesNotContain("\"ref:skip\"", confirm.Body);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.RefConfirm), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var payload = await ReadJson(addRequest);
        var client = payload.GetProperty("client");

        Assert.Equal("newbie@example.com", client.GetProperty("email").GetString());
        Assert.True(client.GetProperty("enable").GetBoolean());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());
        Assert.Equal(new[] { 1, 2 }, payload.GetProperty("inboundIds").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray());
        Assert.InRange(client.GetProperty("expiryTime").GetInt64(),
            DateTimeOffset.UtcNow.AddHours(2).AddMinutes(-2).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddHours(2).AddMinutes(2).ToUnixTimeMilliseconds());

        var comment = client.GetProperty("comment").GetString()!;
        Assert.Contains("Referred by: friend@example.com", comment);
        Assert.Contains("Referrer tgId: 777", comment);
        Assert.Contains($"Telegram ID: {TelegramId}", comment);
        Assert.Contains("Trial until", comment);

        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");

        var deepLinkMenu = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", deepLinkMenu.Body);
    }

    [Fact]
    public async Task Wizard_WithDeepLink_SkipText_Rejected_ReferrerKept()
    {
        var requests = new List<HttpRequestMessage>();
        var referrer = ReferrerAccount();
        var (handler, bot) = CreateWizardHandler(requests, referrer);

        await handler.HandleUpdateAsync(bot, TextUpdate("/start ref_777"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var rejection = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("referrer is required", rejection.Body);
        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");

        // Confirming afterwards still completes with attribution.
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.RefConfirm), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var comment = (await ReadJson(addRequest)).GetProperty("client").GetProperty("comment").GetString()!;
        Assert.Contains("Referred by: friend@example.com", comment);
    }

    [Fact]
    public async Task Wizard_WithDeepLink_SkipCallback_Rejected_ReferrerKept()
    {
        var requests = new List<HttpRequestMessage>();
        var referrer = ReferrerAccount();
        var (handler, bot) = CreateWizardHandler(requests, referrer);

        await handler.HandleUpdateAsync(bot, TextUpdate("/start ref_777"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.RefSkip), CancellationToken.None);

        var rejection = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("referrer is required", rejection.Body);
        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
    }

    [Fact]
    public async Task Wizard_WithDeepLink_Edit_RequiresReferrer_NoSkipOffered()
    {
        var editRequests = new List<HttpRequestMessage>();
        var editReferrer = ReferrerAccount();
        var (editHandler, editBot) = CreateWizardHandler(editRequests, editReferrer);

        await editHandler.HandleUpdateAsync(editBot, TextUpdate("/start ref_777"), CancellationToken.None);
        await editHandler.HandleUpdateAsync(editBot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await editHandler.HandleUpdateAsync(editBot, CallbackUpdate(MenuService.RefEdit), CancellationToken.None);

        var editPrompt = editBot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("required", editPrompt.Body);
        Assert.DoesNotContain("\"ref:skip\"", editPrompt.Body);

        // Skipping after edit is still rejected.
        await editHandler.HandleUpdateAsync(editBot, TextUpdate("/skip"), CancellationToken.None);

        var editRejection = editBot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("referrer is required", editRejection.Body);
        Assert.DoesNotContain(editRequests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
    }

    [Fact]
    public async Task Claim_ProvisionedInvitedAccount_VoidsReferralAttribution()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;
        var invited = ActiveClient() with
        {
            Email = "newbie@example.com",
            TgId = 0,
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
            Comment = "Referrer tgId: 777 · Referred by: friend@example.com"
        };
        var claimed = invited with { TgId = TelegramId };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return Json(getCount == 1 ? EmptyListJson() : ClientListJson(claimed));
            }

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(ClientByEmailJson(invited));

            if (path == "/admin/panel/api/clients/get/tg12345")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/update/newbie@example.com")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var linkRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post &&
                r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/update/newbie@example.com");
        var body = await ReadJson(linkRequest);

        Assert.Equal(TelegramId, body.GetProperty("tgId").GetInt64());

        var comment = body.GetProperty("comment").GetString()!;
        Assert.Contains("Telegram ID", comment);
        Assert.DoesNotContain("Referred by", comment);
        Assert.DoesNotContain("Referrer tgId", comment);

        var menu = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", menu.Body);
    }

    [Fact]
    public async Task Wizard_TrialDisabled_CreatesDisabledAccount()
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

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/add" ||
                path == "/admin/panel/api/clients/bulkDisable")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, trialHours: 0);

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var client = (await ReadJson(addRequest)).GetProperty("client");

        Assert.False(client.GetProperty("enable").GetBoolean());
        Assert.DoesNotContain("Trial until", client.GetProperty("comment").GetString());
        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");
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
        Assert.True(client.GetProperty("enable").GetBoolean());
        Assert.Contains("Trial until", client.GetProperty("comment").GetString());
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
        Assert.Contains("Trial until", comment);
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
    public async Task Start_LegacyProvisioned_GoesStraightToMenu()
    {
        var requests = new List<HttpRequestMessage>();
        var legacy = ActiveClient() with
        {
            Email = $"tg{TelegramId}",
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds()
        };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(legacy));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var menu = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", menu.Body);
        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Wizard_LegacyUnprovisioned_MigratesWithTrial()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;
        var legacy = ActiveClient() with
        {
            Email = $"tg{TelegramId}",
            Enable = false,
            ExpiryTime = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds()
        };
        var migrated = legacy with
        {
            Email = "newbie@example.com",
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
            Comment = "Trial until 2099-01-01 00:00 UTC"
        };

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
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var referrerPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("\"ref:skip\"", referrerPrompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        var body = await ReadJson(updateRequest);

        Assert.Equal("newbie@example.com", body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("enable").GetBoolean());
        Assert.Contains("Trial until", body.GetProperty("comment").GetString());
        Assert.DoesNotContain("Referred by", body.GetProperty("comment").GetString());
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
    public async Task ReferrerCommand_RefusedWhenTrialTaggedButPaid()
    {
        // Paid directly to the admin (dashboard extension) leaves the trial tag, but
        // the account has paid — referrals must be locked like any other paid account.
        var trialEnds = DateTimeOffset.UtcNow.AddDays(-1);
        var paid = ActiveClient() with
        {
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
            Comment = ReferralService.WithTrialTag("comment", trialEnds)
        };

        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(paid)));

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
        Assert.Contains("Register a Friend", edit.Body);
    }

    [Fact]
    public async Task FriendInvite_CreatesUnclaimedAccountWithReferrer()
    {
        var requests = new List<HttpRequestMessage>();
        var inviter = ActiveClient();
        var createdInvite = ActiveClient() with
        {
            Email = "pal@example.com",
            TgId = 0,
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
            SubId = "palsub123",
            Comment = "Invited by user@example.com"
        };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(inviter));

            if (path == "/admin/panel/api/clients/get/pal@example.com")
            {
                var alreadyCreated = requests.Any(r => r.Method == HttpMethod.Post &&
                    r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
                requests.Add(request);
                return Json(alreadyCreated ? ClientByEmailJson(createdInvite) : FailureJson("not found"));
            }

            requests.Add(request);
            if (path == "/admin/panel/api/clients/add" ||
                path == "/admin/panel/api/clients/bulkDisable")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.FriendRegister), CancellationToken.None);

        var prompt = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("email address", prompt.Body);
        Assert.Contains("referrer", prompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("pal@example.com"), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var client = (await ReadJson(addRequest)).GetProperty("client");

        Assert.Equal("pal@example.com", client.GetProperty("email").GetString());
        Assert.Equal(0, client.GetProperty("tgId").GetInt64());

        // Offline-friend flow: the trial starts immediately so the friend can
        // connect before ever touching Telegram.
        Assert.True(client.GetProperty("enable").GetBoolean());

        var comment = client.GetProperty("comment").GetString()!;
        Assert.Contains($"Referrer tgId: {TelegramId}", comment);
        Assert.Contains("Referred by: user@example.com", comment);
        Assert.Contains("Trial until", comment);
        Assert.InRange(client.GetProperty("expiryTime").GetInt64(),
            DateTimeOffset.UtcNow.AddHours(2).AddMinutes(-2).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddHours(2).AddMinutes(2).ToUnixTimeMilliseconds());

        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");

        var done = bot.Requests.First(r => r.MethodName == "sendMessage" && r.Body.Contains("Invite created"));
        Assert.Contains("active now", done.Body);

        // The inviter gets the shareable subscription link plus a QR photo.
        var share = bot.Requests.First(r => r.MethodName == "sendMessage" && r.Body.Contains("palsub123"));
        Assert.Contains("Copy VPN Link", share.Body);
        Assert.Contains("qr:friend", share.Body);

        var qr = Assert.Single(bot.Requests, r => r.MethodName == "sendPhoto");
        Assert.Contains("qr.png", qr.Body);
    }

    [Fact]
    public async Task FriendInvite_TrialDisabled_CreatesDisabledAccountWithoutLink()
    {
        var requests = new List<HttpRequestMessage>();
        var inviter = ActiveClient();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(inviter));

            if (path == "/admin/panel/api/clients/get/pal@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/add" ||
                path == "/admin/panel/api/clients/bulkDisable")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, trialHours: 0);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.FriendRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("pal@example.com"), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var client = (await ReadJson(addRequest)).GetProperty("client");

        Assert.False(client.GetProperty("enable").GetBoolean());
        Assert.DoesNotContain("Trial until", client.GetProperty("comment").GetString());
        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");
        Assert.DoesNotContain(bot.Requests, r => r.MethodName == "sendPhoto");
    }

    [Fact]
    public async Task FriendInvite_AlreadyInvited_ResendsLinkAndQr()
    {
        var inviter = ActiveClient();
        var unclaimed = ActiveClient() with
        {
            Email = "pal@example.com",
            TgId = 0,
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
            SubId = "palsub123",
            Comment = "Invited by user@example.com"
        };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(inviter));

            if (path == "/admin/panel/api/clients/get/pal@example.com")
                return Json(ClientByEmailJson(unclaimed));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.FriendRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("pal@example.com"), CancellationToken.None);

        var info = bot.Requests.First(r => r.MethodName == "sendMessage" && r.Body.Contains("still unclaimed"));
        var share = bot.Requests.First(r => r.MethodName == "sendMessage" && r.Body.Contains("palsub123"));
        Assert.Contains("Copy VPN Link", share.Body);

        var qr = Assert.Single(bot.Requests, r => r.MethodName == "sendPhoto");
        Assert.Contains("qr.png", qr.Body);
    }

    [Fact]
    public async Task QrOwn_SendsQrPhotoForActiveUser()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.QrOwn), CancellationToken.None);

        var qr = Assert.Single(bot.Requests, r => r.MethodName == "sendPhoto");
        Assert.Contains("qr.png", qr.Body);
    }

    [Fact]
    public async Task QrFriend_WithoutInvite_ExplainsMissing()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.QrFriend), CancellationToken.None);

        var msg = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("No recent friend invite", msg.Body);
    }

    [Fact]
    public async Task Claim_ExpiredTrialInvite_RestartsTrialOnClaim()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;
        var expiredTrialEnds = DateTimeOffset.UtcNow.AddHours(-1);
        var expiredInvite = ActiveClient() with
        {
            Email = "newbie@example.com",
            TgId = 0,
            Enable = true,
            ExpiryTime = expiredTrialEnds.ToUnixTimeMilliseconds(),
            Comment = "Referrer tgId: 777 · Referred by: friend@example.com · " +
                      ReferralService.WithTrialTag(null, expiredTrialEnds)
        };
        var claimed = expiredInvite with { TgId = TelegramId };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return Json(getCount == 1 ? EmptyListJson() : ClientListJson(claimed));
            }

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(ClientByEmailJson(expiredInvite));

            if (path == "/admin/panel/api/clients/get/tg12345")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/update/newbie@example.com")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var linkRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post &&
                r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/update/newbie@example.com");
        var linkBody = await ReadJson(linkRequest);

        Assert.Equal(TelegramId, linkBody.GetProperty("tgId").GetInt64());
        Assert.True(linkBody.GetProperty("enable").GetBoolean());
        // Expired trial restarted: fresh ~2h expiry, no stale 2000 tag.
        Assert.InRange(linkBody.GetProperty("expiryTime").GetInt64(),
            DateTimeOffset.UtcNow.AddHours(2).AddMinutes(-2).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddHours(2).AddMinutes(2).ToUnixTimeMilliseconds());
        Assert.DoesNotContain("2000-01-01", linkBody.GetProperty("comment").GetString());
        Assert.Contains("Trial until", linkBody.GetProperty("comment").GetString());
        Assert.Contains("Referred by: friend@example.com", linkBody.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task FriendInvite_TakenEmail_Refuses()
    {
        var inviter = ActiveClient();
        var taken = ActiveClient() with { Email = "taken@example.com", TgId = 999 };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(inviter));

            if (path == "/admin/panel/api/clients/get/taken@example.com")
                return Json(ClientByEmailJson(taken));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.FriendRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("taken@example.com"), CancellationToken.None);

        var msg = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("already registered", msg.Body);
    }

    [Fact]
    public async Task ClaimLinksUnclaimedAccountKeepingReferrer()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;
        var invited = ActiveClient() with
        {
            Email = "newbie@example.com",
            TgId = 0,
            Enable = false,
            ExpiryTime = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds(),
            Comment = "Referrer tgId: 777 · Referred by: friend@example.com"
        };
        var claimed = invited with { TgId = TelegramId };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return Json(getCount == 1 ? EmptyListJson() : ClientListJson(claimed));
            }

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(ClientByEmailJson(invited));

            if (path == "/admin/panel/api/clients/update/newbie@example.com")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var linkRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        var linkBody = await ReadJson(linkRequest);

        // Claiming starts the deferred trial and links Telegram.
        Assert.Equal(TelegramId, linkBody.GetProperty("tgId").GetInt64());
        Assert.True(linkBody.GetProperty("enable").GetBoolean());
        Assert.Contains("Trial until", linkBody.GetProperty("comment").GetString());
        Assert.Contains("Referred by: friend@example.com", linkBody.GetProperty("comment").GetString());
        Assert.InRange(linkBody.GetProperty("expiryTime").GetInt64(),
            DateTimeOffset.UtcNow.AddHours(2).AddMinutes(-2).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddHours(2).AddMinutes(2).ToUnixTimeMilliseconds());

        var menu = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", menu.Body);
    }

    [Fact]
    public async Task ClaimLinksUnclaimedAccountWithTrial_KeepsExistingState()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;
        var invited = ActiveClient() with
        {
            Email = "newbie@example.com",
            TgId = 0,
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
            Comment = "Registered by admin · Referred by: friend@example.com · Trial until 2099-01-01 00:00 UTC"
        };
        var claimed = invited with { TgId = TelegramId };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return Json(getCount == 1 ? EmptyListJson() : ClientListJson(claimed));
            }

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(ClientByEmailJson(invited));

            if (path == "/admin/panel/api/clients/update/newbie@example.com")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var linkRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        var linkBody = await ReadJson(linkRequest);

        // Admin-registered rows already carry a trial: claim only links Telegram.
        Assert.Equal(TelegramId, linkBody.GetProperty("tgId").GetInt64());
        Assert.True(linkBody.GetProperty("enable").GetBoolean());
        Assert.Contains("Trial until 2099-01-01", linkBody.GetProperty("comment").GetString());

        var menu = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", menu.Body);
    }

    [Fact]
    public async Task AdminRegister_CreatesUnclaimedAccountWithReferrer()
    {
        var requests = new List<HttpRequestMessage>();
        var friend = ReferrerAccount();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == $"/admin/panel/api/clients/get/{friend.Email}")
                return Json(ClientByEmailJson(friend));

            if (path == "/admin/panel/api/clients/add") return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var referrerPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("referrer", referrerPrompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("friend@example.com"), CancellationToken.None);

        var grantPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("days", grantPrompt.Body.ToLowerInvariant());

        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var client = (await ReadJson(addRequest)).GetProperty("client");

        Assert.Equal("newbie@example.com", client.GetProperty("email").GetString());
        Assert.Equal(0, client.GetProperty("tgId").GetInt64());
        Assert.True(client.GetProperty("enable").GetBoolean());

        var comment = client.GetProperty("comment").GetString()!;
        Assert.Contains("Registered by admin", comment);
        Assert.Contains("Referred by: friend@example.com", comment);
        Assert.Contains("Trial until", comment);

        var done = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Account created", done.Body);
    }

    [Fact]
    public async Task AdminRegister_Skip_CreatesWithoutReferrer()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/add") return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var grantPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("days", grantPrompt.Body.ToLowerInvariant());

        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        Assert.DoesNotContain("Referred by",
            (await ReadJson(addRequest)).GetProperty("client").GetProperty("comment").GetString());
    }

    [Fact]
    public async Task AdminRegister_AddFails_ShowsPanelErrorAndConfigHint()
    {
        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/add")
                return Json(FailureJson("Something went wrong (inbound 1: record not found)"));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var failure = bot.Requests.Last(r => r.MethodName == "sendMessage");

        Assert.Contains("inbound 1: record not found", failure.Body);
        Assert.Contains("DefaultInboundIds", failure.Body);
    }

    [Fact]
    public async Task AdminRegister_GrantDays_CreatesWithExpiry()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/add") return Json(SuccessJson());

            if (path == "/admin/panel/api/clients/get/sub/newbie@example.com")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var grantPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("days", grantPrompt.Body.ToLowerInvariant());

        var before = DateTimeOffset.UtcNow;
        await handler.HandleUpdateAsync(bot, TextUpdate("30"), CancellationToken.None);

        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var client = (await ReadJson(addRequest)).GetProperty("client");

        Assert.Equal("newbie@example.com", client.GetProperty("email").GetString());
        Assert.True(client.GetProperty("enable").GetBoolean());

        var expiryMs = client.GetProperty("expiryTime").GetInt64();
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiryMs);
        Assert.InRange(expiry, before.AddDays(30).AddMinutes(-2), before.AddDays(30).AddMinutes(2));

        var comment = client.GetProperty("comment").GetString()!;
        Assert.DoesNotContain("Trial until", comment);

        var done = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("30 days", done.Body);
    }

    [Fact]
    public async Task AdminRegister_GrantDays_Invalid_Reprompts()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/add") return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("abc"), CancellationToken.None);

        var reprompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Invalid number of days", reprompt.Body);
        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");

        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
    }

    [Fact]
    public async Task AdminRegister_GrantDays_WithReferrer_PaysBonusImmediately()
    {
        var requests = new List<HttpRequestMessage>();
        var friend = ReferrerAccount();
        var getNewbieCount = 0;

        PanelClientDto GrantedReferee()
        {
            return ActiveClient() with
            {
                Email = "newbie@example.com",
                TgId = 0,
                Enable = true,
                ExpiryTime = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
                Comment = "Registered by admin · Referrer tgId: 777 · Referred by: friend@example.com"
            };
        }

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
            {
                getNewbieCount++;
                // First lookup: clash check (not found). Later: settlement + link lookups.
                if (getNewbieCount == 1) return Json(FailureJson("not found"));
                return Json(ClientByEmailJson(GrantedReferee()));
            }

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return Json(ClientByEmailJson(friend));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return Json(ClientListJson(friend));

            if (path == "/admin/panel/api/clients/add") return Json(SuccessJson());

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/friend@example.com")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("friend@example.com"), CancellationToken.None);

        var grantPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("days", grantPrompt.Body.ToLowerInvariant());

        await handler.HandleUpdateAsync(bot, TextUpdate("30"), CancellationToken.None);

        // New account created with grant.
        var addRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        Assert.Equal("newbie@example.com",
            (await ReadJson(addRequest)).GetProperty("client").GetProperty("email").GetString());

        // Referrer got bonus grant + credit ledger.
        var referrerUpdates = requests.Where(r => r.Method == HttpMethod.Post &&
            r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/update/friend@example.com").ToList();
        Assert.Equal(2, referrerUpdates.Count);

        // Referee marked bonus-paid.
        var refereeUpdates = requests.Where(r => r.Method == HttpMethod.Post &&
            r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/update/newbie@example.com").ToList();
        Assert.Single(refereeUpdates);
        Assert.Contains("Referral bonus paid",
            (await ReadJson(refereeUpdates[0])).GetProperty("comment").GetString());

        var done = bot.Requests.Last(r => r.MethodName == "sendMessage" && r.Body.Contains("Account created"));
        Assert.Contains("30 days", done.Body);
        Assert.Contains("Referrer", done.Body);
    }

    [Fact]
    public async Task AdminRegister_TakenEmail_Refuses()
    {
        var taken = ActiveClient() with { TgId = 999 };

        var (handler, bot) = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/admin/panel/api/clients/get/taken@example.com")
                return Json(ClientByEmailJson(taken));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminRegister), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("taken@example.com"), CancellationToken.None);

        var msg = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("already registered", msg.Body);
    }

    [Fact]
    public async Task AdminChangeEmail_ConfirmsAndRenames()
    {
        var requests = new List<HttpRequestMessage>();
        var account = ActiveClient();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/user@example.com")
                return Json(ClientByEmailJson(account));

            if (path == "/admin/panel/api/clients/get/renamed@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/update/user@example.com")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminChangeEmail), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user@example.com"), CancellationToken.None);

        var askNew = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("new email", askNew.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("renamed@example.com"), CancellationToken.None);

        var confirm = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("renamed@example.com", confirm.Body);
        Assert.Contains($"\"callback_data\":\"{MenuService.AdminConfirmChangeEmail}\"", confirm.Body);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminConfirmChangeEmail),
            CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        Assert.Equal("renamed@example.com", (await ReadJson(updateRequest)).GetProperty("email").GetString());

        var done = bot.Requests.Last(r => r.MethodName == "editMessageText");
        Assert.Contains("Changed", done.Body);
    }

    [Fact]
    public async Task AdminChangeEmail_LongAddresses_KeepsConfirmCallbackUnderTelegramLimit()
    {
        const string oldEmail = "alexandra.montgomery@example.com";
        const string newEmail = "alexandra.montgomery.junior@example.com";
        var requests = new List<HttpRequestMessage>();
        var account = ActiveClient() with { Email = oldEmail };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/{oldEmail}")
                return Json(ClientByEmailJson(account));

            if (path == $"/admin/panel/api/clients/get/{newEmail}")
                return Json(FailureJson("not found"));

            if (path == $"/admin/panel/api/clients/update/{oldEmail}")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminChangeEmail), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(oldEmail), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate(newEmail), CancellationToken.None);

        var confirm = bot.Requests.Last(r => r.MethodName == "sendMessage");
        using var doc = JsonDocument.Parse(confirm.Body);
        var callbackData = doc.RootElement
            .GetProperty("reply_markup")
            .GetProperty("inline_keyboard")[0][0]
            .GetProperty("callback_data")
            .GetString();

        Assert.Equal(MenuService.AdminConfirmChangeEmail, callbackData);
        Assert.True(Encoding.UTF8.GetByteCount(callbackData!) <= 64,
            $"callback_data must fit Telegram's 64-byte limit, was {Encoding.UTF8.GetByteCount(callbackData!)}");

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminConfirmChangeEmail),
            CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        Assert.Equal(newEmail, (await ReadJson(updateRequest)).GetProperty("email").GetString());

        var done = bot.Requests.Last(r => r.MethodName == "editMessageText");
        Assert.Contains("Changed", done.Body);
    }

    [Fact]
    public async Task AdminChangeEmail_TakenTarget_Refuses()
    {
        var taken = ActiveClient() with { Email = "taken@example.com", TgId = 999 };

        var (handler, bot) = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/admin/panel/api/clients/get/taken@example.com")
                return Json(ClientByEmailJson(taken));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminChangeEmail), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("taken@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("taken@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminConfirmChangeEmail),
            CancellationToken.None);

        var done = bot.Requests.Last(r => r.MethodName == "editMessageText");
        Assert.Contains("failed", done.Body);
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
    public async Task ReferralGate_ProvisionedLegacy_OneTimeEmailUpdateThenLink()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;
        var legacy = ActiveClient() with
        {
            Email = $"tg{TelegramId}",
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
            Comment = "Telegram ID: 12345"
        };
        var renamed = legacy with { Email = "newbie@example.com" };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return Json(ClientListJson(getCount <= 3 ? legacy : renamed));
            }

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/update/tg12345")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Referral), CancellationToken.None);

        var prompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("one-time", prompt.Body);
        Assert.DoesNotContain("t.me/TestBot", prompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        var body = await ReadJson(updateRequest);

        Assert.Equal("newbie@example.com", body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("enable").GetBoolean());
        Assert.DoesNotContain("Trial until", body.GetProperty("comment").GetString());
        Assert.DoesNotContain("Referred by", body.GetProperty("comment").GetString());

        var link = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains($"t.me/TestBot?start=ref_{TelegramId}", link.Body);
        Assert.Contains("Invite Friends", link.Body);
    }

    [Fact]
    public async Task ReferralGate_UnprovisionedLegacy_FullWizardThenLink()
    {
        var requests = new List<HttpRequestMessage>();
        var legacy = ActiveClient() with
        {
            Email = $"tg{TelegramId}",
            Enable = false,
            ExpiryTime = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds()
        };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(legacy));

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/update/tg12345")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, TextUpdate("/admin"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminExit), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Referral), CancellationToken.None);

        var prompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("one-time", prompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var referrerPrompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("\"ref:skip\"", referrerPrompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("/skip"), CancellationToken.None);

        var link = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains($"t.me/TestBot?start=ref_{TelegramId}", link.Body);
    }

    [Fact]
    public async Task ReferralGate_InvalidEmail_RequiresEmailUpdateThenLink()
    {
        var requests = new List<HttpRequestMessage>();
        var invalid = ActiveClient() with
        {
            Email = "not-an-email",
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
            Comment = "Telegram ID: 12345"
        };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(invalid));

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/update/not-an-email")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Referral), CancellationToken.None);

        var prompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("one-time", prompt.Body);
        Assert.DoesNotContain("t.me/TestBot", prompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var updateRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(
                "/admin/panel/api/clients/update/", StringComparison.Ordinal));
        var body = await ReadJson(updateRequest);

        Assert.Equal("newbie@example.com", body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("enable").GetBoolean());
        Assert.DoesNotContain("Trial until", body.GetProperty("comment").GetString());
        Assert.DoesNotContain("Referred by", body.GetProperty("comment").GetString());

        var link = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains($"t.me/TestBot?start=ref_{TelegramId}", link.Body);
        Assert.Contains("Invite Friends", link.Body);
    }

    [Fact]
    public async Task FriendInvite_InvalidInviterEmail_RequiresEmailUpdate()
    {
        var requests = new List<HttpRequestMessage>();
        var inviter = ActiveClient() with { Email = "bademail" };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(inviter));

            if (path == "/admin/panel/api/clients/get/pal@example.com")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/update/bademail")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.FriendRegister), CancellationToken.None);

        var prompt = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("one-time", prompt.Body);
        Assert.DoesNotContain("as referrer", prompt.Body);

        await handler.HandleUpdateAsync(bot, TextUpdate("pal@example.com"), CancellationToken.None);

        Assert.DoesNotContain(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");

        var link = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains($"t.me/TestBot?start=ref_{TelegramId}", link.Body);
    }

    [Fact]
    public async Task Claim_WithLegacyDuplicate_RetiresIt()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;
        var legacy = ActiveClient() with
        {
            Email = $"tg{TelegramId}",
            Enable = false,
            ExpiryTime = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds()
        };
        var invited = ActiveClient() with
        {
            Email = "newbie@example.com",
            TgId = 0,
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
            Comment = "Referrer tgId: 777 · Referred by: friend@example.com · Trial until 2099-01-01 00:00 UTC"
        };
        var claimed = invited with { TgId = TelegramId };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return Json(getCount == 1 ? ClientListJson(legacy) : ClientListJson(claimed));
            }

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(ClientByEmailJson(invited));

            if (path == "/admin/panel/api/clients/get/tg12345")
                return Json(ClientByEmailJson(legacy));

            if (path == "/admin/panel/api/clients/update/newbie@example.com" ||
                path == "/admin/panel/api/clients/update/tg12345")
                return Json(SuccessJson());

            if (path == "/admin/panel/api/clients/bulkDisable")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);

        var linkRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post &&
                r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/update/newbie@example.com");
        Assert.Equal(TelegramId, (await ReadJson(linkRequest)).GetProperty("tgId").GetInt64());

        var tombstoneRequest = Assert.Single(requests,
            r => r.Method == HttpMethod.Post &&
                r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/update/tg12345");
        var tombstoneBody = await ReadJson(tombstoneRequest);
        Assert.StartsWith("tg12345.merged-", tombstoneBody.GetProperty("email").GetString());
        Assert.False(tombstoneBody.GetProperty("enable").GetBoolean());

        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");

        var menu = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", menu.Body);
    }

    [Fact]
    public async Task ActiveUser_TypingEmail_GoesToSupportWithoutPanelCalls()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);

            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return Json(ClientListJson(ActiveClient()));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("someone@example.com"), CancellationToken.None);

        var fallback = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("https://t.me/support", fallback.Body);
        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Post);
    }

    private static string SummaryListJson(params (int Id, string Email)[] rows)
    {
        var summaries = rows.Select(r => new PanelClientSummaryDto(
            r.Id, r.Email, $"sub{r.Id}", null, 100, 0, true, null, null)).ToList();
        return JsonSerializer.Serialize(new PanelApiResponseDto<List<PanelClientSummaryDto>>(true, "ok", summaries));
    }

    [Fact]
    public async Task AdminLookup_Partial_Multiple_ShowsPickListAndSelects()
    {
        var first = ActiveClient() with { Email = "user01@example.com", TgId = 111 };
        var second = ActiveClient() with { Email = "user02@example.com", TgId = 222 };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/user")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/list")
                return Json(SummaryListJson((1, "user01@example.com"), (2, "user02@example.com")));

            if (path == "/admin/panel/api/clients/get/user02@example.com")
                return Json(ClientByEmailJson(second));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLookup), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user"), CancellationToken.None);

        var pick = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("Multiple matches", pick.Body);
        Assert.Contains("admin:pick:0", pick.Body);
        Assert.Contains("admin:pick:1", pick.Body);

        await handler.HandleUpdateAsync(bot, CallbackUpdate($"{MenuService.AdminPick}:1"),
            CancellationToken.None);

        var card = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("User lookup", card.Body);
        Assert.Contains("user02@example.com", card.Body);
        Assert.DoesNotContain("user01@example.com", card.Body);
    }

    [Fact]
    public async Task AdminLookup_Partial_Single_AutoResolves()
    {
        var only = ActiveClient() with { Email = "user01@example.com", TgId = 111 };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/user01")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/list")
                return Json(SummaryListJson((1, "user01@example.com"), (2, "other@example.com")));

            if (path == "/admin/panel/api/clients/get/user01@example.com")
                return Json(ClientByEmailJson(only));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLookup), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user01"), CancellationToken.None);

        var card = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("User lookup", card.Body);
        Assert.Contains("user01@example.com", card.Body);
        Assert.DoesNotContain(bot.Requests, r => r.Body.Contains("admin:pick:"));
    }

    [Fact]
    public async Task AdminLookup_NoMatch_ReportsNotFound()
    {
        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/zzz")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/list")
                return Json(SummaryListJson((1, "user01@example.com")));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminLookup), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("zzz"), CancellationToken.None);

        var msg = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("No client found", msg.Body);
    }

    [Fact]
    public async Task AdminBan_Partial_Pick_ConfirmsAndExecutes()
    {
        var requests = new List<HttpRequestMessage>();
        var target = ActiveClient() with { Email = "user01@example.com", TgId = 111 };

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/user")
                return Json(FailureJson("not found"));

            if (path == "/admin/panel/api/clients/list")
                return Json(SummaryListJson((1, "user01@example.com"), (2, "user02@example.com")));

            if (path == "/admin/panel/api/clients/get/user01@example.com")
                return Json(ClientByEmailJson(target));

            if (path == "/admin/panel/api/clients/bulkDisable")
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminBan), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("user"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate($"{MenuService.AdminPick}:0"),
            CancellationToken.None);

        var confirm = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("admin:confirm:ban:user01@example.com", confirm.Body);

        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmBan}:user01@example.com"), CancellationToken.None);

        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");
        var done = bot.Requests.Last(r => r.MethodName == "editMessageText");
        Assert.Contains("Banned", done.Body);
    }

    [Fact]
    public async Task AdminPick_StaleSelection_Expires()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()), adminIds: [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate($"{MenuService.AdminPick}:0"),
            CancellationToken.None);

        var msg = bot.Requests.Last(r => r.MethodName == "sendMessage");
        Assert.Contains("expired", msg.Body);
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
        Assert.Contains("Paste", edit.Body);
        Assert.Contains("Allow Paste", edit.Body);
        Assert.Contains("big button", edit.Body);

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
    public async Task AdminGrant_ReportsReferralBonusWhenSettled()
    {
        var trialEnds = DateTimeOffset.UtcNow.AddHours(2);
        var referee = ActiveClient() with
        {
            Email = "newbie@example.com",
            TgId = 111,
            Enable = true,
            ExpiryTime = trialEnds.ToUnixTimeMilliseconds(),
            Comment = "Telegram ID: 111 · Referrer tgId: 777 · Referred by: friend@example.com · " +
                      ReferralService.WithTrialTag(null, trialEnds)
        };
        var referrer = ReferrerAccount();

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/111") return Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777") return Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return Json(ClientByEmailJson(referrer));

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminGrant), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("newbie@example.com"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, TextUpdate("30"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot,
            CallbackUpdate($"{MenuService.AdminConfirmGrant}:newbie@example.com:30"), CancellationToken.None);

        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Granted"));

        Assert.Contains("Granted", edit.Body);
        Assert.Contains("Referral bonus paid", edit.Body);
    }

    [Fact]
    public async Task AdminSettle_RunsSweepAndReportsCount()
    {
        var referee = ActiveClient() with
        {
            Email = "newbie@example.com",
            TgId = 111,
            Comment = "Telegram ID: 111 · Referrer tgId: 777 · Referred by: friend@example.com"
        };
        var referrer = ReferrerAccount();

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/admin/panel/api/clients/list")
                return Json(JsonSerializer.Serialize(new PanelApiResponseDto<List<PanelClientSummaryDto>>(true, "ok",
                    new List<PanelClientSummaryDto>
                    {
                        new(1, "newbie@example.com", "sub1", "uuid-1", 100, referee.ExpiryTime, true,
                            new List<int> { 1 }, null)
                    })));

            if (path == "/admin/panel/api/clients/get/newbie@example.com")
                return Json(ClientByEmailJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/111") return Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777") return Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return Json(ClientByEmailJson(referrer));

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
                return Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, [TelegramId]);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminSettle), CancellationToken.None);

        await bot.WaitForAsync((_, body) =>
            body.Contains("Settled") || body.Contains("No pending referral bonuses"));

        var edits = bot.Requests.Where(r => r.MethodName == "editMessageText").ToList();

        Assert.Contains(edits, r => r.Body.Contains("Scanning"));
        Assert.Contains(edits, r => r.Body.Contains("Settled"));
    }

    [Fact]
    public async Task AdminSettle_WhenAlreadyRunning_DoesNotStartSecondSweep()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminPanelService = new StubAdminPanelService
        {
            SettleHandler = async cancellationToken =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return 0;
            }
        };

        var bot = new FakeTelegramBotClient();
        var telegramOptions = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            TributeSubscriptionUrl = "https://tribute.test",
            BotUsername = "TestBot",
            DefaultInboundIds = new[] { 2, 6 },
            WebhookPath = "/webhooks/telegram",
            AdminIds = new[] { TelegramId }
        });

        var panelClient = new PanelApiGateway(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("panel not used")))
            {
                BaseAddress = new Uri(PanelBaseUri)
            },
            NullLogger<PanelApiGateway>.Instance);

        var settlementRunner = new ReferralSettlementRunner(adminPanelService,
            new TestHostApplicationLifetime(), NullLogger<ReferralSettlementRunner>.Instance);

        var handler = new TelegramHandlers(new SessionStore(), panelClient,
            new StubPanelDirectory { DefaultInboundIds = [2, 6] }, adminPanelService, settlementRunner,
            telegramOptions, Options.Create(new PanelOptions { BaseUri = PanelBaseUri, ApiKey = "key" }),
            NullLogger<TelegramHandlers>.Instance);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminSettle), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.AdminSettle), CancellationToken.None);

        Assert.Equal(1, adminPanelService.SettleCalls);
        Assert.Contains(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("already in progress"));

        release.SetResult(true);

        await bot.WaitForAsync((_, body) => body.Contains("No pending referral bonuses"));

        Assert.Equal(1, adminPanelService.SettleCalls);
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
            .Select(i => new PanelClientSummaryDto(
                i, $"user{i:00}@example.com", $"sub{i:00}", null, 100,
                0, i % 2 == 0, null, null))
            .ToList();

        var listJson = JsonSerializer.Serialize(new PanelApiResponseDto<List<PanelClientSummaryDto>>(true, "ok", summaries));

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
            .Select(i => new PanelClientSummaryDto(
                i, $"user{i:00}@example.com", $"sub{i:00}", null, 100,
                0, i % 2 == 0, null, null))
            .ToList();

        var listJson = JsonSerializer.Serialize(new PanelApiResponseDto<List<PanelClientSummaryDto>>(true, "ok", summaries));

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