using System.Text.Json;
using System.Text.Json.Serialization;

namespace EagleTunnelApi.TributeShop;

public interface ITributeShopClient
{
    Task<ShopOrderResponse?> CreateOrderAsync(CreateShopOrderRequest request, CancellationToken cancellationToken);
}

public class TributeShopClient(HttpClient httpClient, ILogger<TributeShopClient> logger) : ITributeShopClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ShopOrderResponse?> CreateOrderAsync(CreateShopOrderRequest request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating shop order. CustomerId: {CustomerId}, Plan: {Period}, Amount: {Amount}",
            request.CustomerId, request.Period, request.Amount);

        using var response = await httpClient.PostAsJsonAsync(BuildUri("/shop/orders"), request, SerializerOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var message = await TryReadErrorMessage(response, cancellationToken);
            logger.LogError("Shop order creation failed. Status: {Status}, Message: {Message}",
                response.StatusCode, message);
            throw new TributeShopException($"Creating shop order failed: {message}");
        }

        var order = await response.Content.ReadFromJsonAsync<ShopOrderResponse>(cancellationToken);

        if (order is null)
        {
            logger.LogError("Shop order creation returned an empty response.");
            throw new TributeShopException("Creating shop order failed: response was empty");
        }

        logger.LogInformation("Shop order created. Uuid: {Uuid}, Status: {Status}", order.Uuid, order.Status);

        return order;
    }

    private Uri BuildUri(string path) =>
        new($"{httpClient.BaseAddress!.AbsoluteUri.TrimEnd('/')}{path}");

    private static async Task<string> TryReadErrorMessage(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<TributeErrorResponse>(cancellationToken);
            return error?.Message ?? response.ReasonPhrase ?? "unknown";
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "unknown";
        }
    }
}

public class TributeShopException(string message) : Exception(message);