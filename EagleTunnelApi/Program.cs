using System.Net.Http.Headers;
using System.Text.Json;
using EagleTunnelApi.ServiceDefaults;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Handlers;
using EagleTunnelApi.Webhook.Security;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();

builder.Services.Configure<JsonOptions>(options => { options.SerializerOptions.PropertyNameCaseInsensitive = true; });
builder.Services.AddScoped<IVerifier, Verifier>();
builder.Services.AddHttpClient<ITributeEventsHandler, TributeEventsHandler>((sp, client) =>
{
    client.BaseAddress = new Uri(sp.GetRequiredService<IConfiguration>()
                                     .GetValue<string>("Remnawave:BaseUri") ??
                                 throw new InvalidOperationException("Remnawave Base Uri not found in environment"));
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer",
            sp.GetRequiredService<IConfiguration>()
                .GetValue<string>("Remnawave:ApiKey") ??
            throw new InvalidOperationException("Remnawave Base API Key not found in environment"));
});

var app = builder.Build();

app.MapDefaultEndpoints();

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

await app.RunAsync();