using System.Net.Http.Headers;
using System.Text.Json;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Handlers;
using EagleTunnelApi.Webhook.Security;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.Configure<JsonOptions>(options => { options.SerializerOptions.PropertyNameCaseInsensitive = true; });
builder.Services.AddScoped<IVerifier, Verifier>();
builder.Services.AddHttpClient<ITributeEventsHandler, TributeEventsHandler>((sp, client) =>
{
    client.BaseAddress = new Uri("https://vpn.kimfom.com.ng");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer",
            sp.GetRequiredService<IConfiguration>()
                .GetValue<string>("Remnawave:ApiKey"));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUi(options => options.DocumentPath = "openapi/v1.json");
}

app.UseHttpsRedirection();

app.MapPost("/webhooks/tribute", async (HttpRequest request, IVerifier verifier, ITributeEventsHandler eventsHandler,
    CancellationToken cancellationToken) =>
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
            case "cancelled_subscription":
                var cancelledSubscription = webhookEvent.Payload.Deserialize<CancelledSubscription>();
                await eventsHandler.HandleCancelledSubscription(cancelledSubscription!, cancellationToken);
                break;
            case "renewed_subscription":
                var renewedSubscription = webhookEvent.Payload.Deserialize<RenewedSubscription>();
                await eventsHandler.HandleRenewedSubscription(renewedSubscription!, cancellationToken);
                break;
            default:
                throw new InvalidOperationException();
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

await app.RunAsync();