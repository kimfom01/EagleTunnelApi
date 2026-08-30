using System.Net.Http.Headers;
using System.Text.Json;
using System.Linq;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.Logging;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.ServiceDefaults;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Handlers;
using EagleTunnelApi.Webhook.Security;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();

builder.Services.Configure<JsonOptions>(options => { options.SerializerOptions.PropertyNameCaseInsensitive = true; });

builder.Services.AddOptions<TelegramOptions>()
    .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName))
    .ValidateOnStart();
builder.Services.PostConfigure<TelegramOptions>(options =>
{
    var section = builder.Configuration.GetSection(TelegramOptions.SectionName);
    var inboundIdsStr = section["DefaultInboundIds"];
    if (!string.IsNullOrWhiteSpace(inboundIdsStr))
    {
        options.DefaultInboundIds = inboundIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToArray();
    }
    var adminIdsStr = section["AdminIds"];
    if (!string.IsNullOrWhiteSpace(adminIdsStr))
    {
        options.AdminIds = adminIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(long.Parse).ToArray();
    }
});
builder.Services.AddSingleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>();

builder.Services.AddOptions<PanelOptions>()
    .Bind(builder.Configuration.GetSection(PanelOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PanelOptions>, PanelOptionsValidator>();

builder.Services.AddOptions<TributeOptions>()
    .Bind(builder.Configuration.GetSection(TributeOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TributeOptions>, TributeOptionsValidator>();

builder.Services.AddScoped<IVerifier, Verifier>();

builder.Services.AddTransient<OutgoingRequestLoggingHandler>();

builder.Services.AddHttpClient<IPanelClient, PanelApiClient>((sp, client) =>
{
    var panelOptions = sp.GetRequiredService<IOptions<PanelOptions>>().Value;

    client.BaseAddress = new Uri(panelOptions.BaseUri);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", panelOptions.ApiKey);
}).AddHttpMessageHandler<OutgoingRequestLoggingHandler>();

builder.Services.AddHttpClient("telegram_bot_client")
    .AddTypedClient<ITelegramBotClient>((httpClient, sp) =>
    {
        var telegramOptions = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
        return new TelegramBotClient(telegramOptions.BotToken, httpClient);
    })
    .AddHttpMessageHandler<OutgoingRequestLoggingHandler>();

builder.Services.AddSingleton<ISubscriptionProvisioner, SubscriptionProvisioner>();
builder.Services.AddSingleton<ITributeEventsHandler, TributeEventsHandler>();
builder.Services.AddSingleton<IAdminPanelService, AdminPanelService>();

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<TelegramHandlers>();
builder.Services.AddSingleton<IUpdateHandler>(sp => sp.GetRequiredService<TelegramHandlers>());

if (!builder.Environment.IsProduction())
{
    builder.Services.AddHostedService<TelegramPollingService>();
}

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUi(options => options.DocumentPath = "openapi/v1.json");
}

app.UseHttpsRedirection();

app.UseMiddleware<CorrelationIdMiddleware>();

app.MapPost("/webhooks/tribute", async (HttpRequest request, IVerifier verifier,
    ITributeEventsHandler eventsHandler, CancellationToken cancellationToken) =>
{
    try
    {
        var webhookEvent = await verifier.VerifySignature(request);

        if (webhookEvent is null)
        {
            throw new InvalidPayloadException();
        }

        switch (webhookEvent.Name)
        {
            case "new_subscription":
                var newSubscription = webhookEvent.Payload.Deserialize<NewSubscription>();
                await eventsHandler.HandleNewSubscription(newSubscription!, cancellationToken);
                break;
            case "renewed_subscription":
                var renewedSubscription = webhookEvent.Payload.Deserialize<RenewedSubscription>();
                await eventsHandler.HandleRenewedSubscription(renewedSubscription!, cancellationToken);
                break;
            default:
                await eventsHandler.UnhandledEvent(webhookEvent.Name);
                break;
        }

        return Results.Ok();
    }
    catch (InvalidPayloadException)
    {
        return Results.BadRequest("Invalid webhook payload");
    }
    catch (InvalidSignatureException)
    {
        return Results.Unauthorized();
    }
    catch (NotFoundException)
    {
        return Results.StatusCode(500);
    }
});

var telegramOptions = app.Services.GetRequiredService<IOptions<TelegramOptions>>().Value;

app.MapPost(telegramOptions.WebhookPath, async (HttpRequest request, IUpdateHandler updateHandler,
    ITelegramBotClient botClient, CancellationToken cancellationToken) =>
{
    var configuredSecretToken = telegramOptions.WebhookSecretToken;

    if (!string.IsNullOrEmpty(configuredSecretToken) &&
        !request.Headers["X-Telegram-Bot-Api-Secret-Token"].Equals(configuredSecretToken))
    {
        return Results.Unauthorized();
    }

    Update? update;
    try
    {
        update = await request.ReadFromJsonAsync<Update>(JsonBotAPI.Options, cancellationToken);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON payload");
    }

    if (update is null)
    {
        return Results.BadRequest("Invalid body");
    }

    await updateHandler.HandleUpdateAsync(botClient, update, cancellationToken);

    return Results.Ok();
});

if (app.Environment.IsProduction())
{
    var botClient = app.Services.GetRequiredService<ITelegramBotClient>();

    var webhookEndpoint = new Uri(new Uri(telegramOptions.WebhookUrl), telegramOptions.WebhookPath).ToString();

    await botClient.SetWebhook(webhookEndpoint,
        secretToken: string.IsNullOrEmpty(telegramOptions.WebhookSecretToken) ? null : telegramOptions.WebhookSecretToken);

    app.Services.GetRequiredService<ILogger<EagleTunnelApi.Program>>().LogInformation("Telegram webhook set to {WebhookEndpoint}",
        webhookEndpoint);
}

await app.RunAsync();

namespace EagleTunnelApi
{
    public partial class Program;
}