using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Handlers;
using EagleTunnelApi.Webhook.Security;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options => { options.SerializerOptions.PropertyNameCaseInsensitive = true; });
builder.Services.AddScoped<ITributeEventsHandler, TributeEventsHandler>();
builder.Services.AddScoped<IVerifier, Verifier>();

var app = builder.Build();

app.UseHttpsRedirection();

app.MapPost("/webhooks/tribute", async (HttpRequest request, IVerifier verifier, ITributeEventsHandler eventsHandler) =>
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
                await eventsHandler.HandleNewSubscription(webhookEvent);
                break;
            case "cancelled_subscription":
                await eventsHandler.HandleCancelledSubscription(webhookEvent);
                break;
            case "renewed_subscription":
                await eventsHandler.HandleRenewedSubscription(webhookEvent);
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
});

await app.RunAsync();