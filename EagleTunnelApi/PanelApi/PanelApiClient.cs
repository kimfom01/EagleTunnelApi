using System.Text.Json;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Webhook.Exceptions;

namespace EagleTunnelApi.PanelApi;

public class PanelApiClient(HttpClient httpClient, ILogger<PanelApiClient> logger) : IPanelClient
{
    public async Task<PanelClientResponse?> GetClientByTelegramIdAsync(long tgId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching Client From Panel. TelegramId: {TelegramId}", tgId);

        var response = await httpClient.GetFromJsonAsync<PanelApiResponse<List<PanelClientResponse>>>(
            $"/admin/panel/api/clients/get/tgId/{tgId}", cancellationToken);

        if (response is null)
        {
            throw new PanelApiException("Panel returned an empty response");
        }

        if (!response.Success)
        {
            logger.LogError("Panel fetch failed. TelegramId: {TelegramId}, Message: {Msg}", tgId, response.Msg);
            throw new PanelApiException($"Panel fetch failed: {response.Msg}");
        }

        if (response.Obj is not { Count: > 0 } clientResponses)
        {
            logger.LogWarning("Client not found for TelegramId: {TelegramId}", tgId);
            return null;
        }

        if (clientResponses.Count > 1)
        {
            var emails = string.Join(", ", clientResponses.Select(c => c.Client.Email));
            logger.LogWarning(
                "Multiple clients found for TelegramId: {TelegramId}. Using first match. Emails: {Emails}",
                tgId, emails);
        }

        return clientResponses[0];
    }

    public async Task<PanelClientResponse?> GetClientByEmailAsync(string email, CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching Client From Panel. Email: {Email}", email);

        var response = await httpClient.GetFromJsonAsync<PanelApiResponse<PanelClientResponse>>(
            $"/admin/panel/api/clients/get/{email}", cancellationToken);

        if (response is null)
        {
            throw new PanelApiException("Panel returned an empty response");
        }

        if (!response.Success)
        {
            logger.LogError("Panel fetch failed. Email: {Email}, Message: {Msg}", email, response.Msg);
            throw new PanelApiException($"Panel fetch failed: {response.Msg}");
        }

        return response.Obj;
    }

    public async Task AddClientAsync(CreateClientPayload payload, CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating Client At Panel. Email: {Email}, InboundIds: {@InboundIds}",
            payload.Client.Email, payload.InboundIds);

        await PostAndValidateAsync("/admin/panel/api/clients/add", payload, "Creating client", cancellationToken);

        logger.LogInformation("Successfully Created Client At Panel. Email: {Email}", payload.Client.Email);
    }

    public async Task UpdateClientAsync(UpdateClientRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Updating Client At Panel. Email: {Email}", request.Email);

        await PostAndValidateAsync($"/admin/panel/api/clients/update/{request.Email}", request, "Updating client",
            cancellationToken);

        logger.LogInformation("Successfully Updated Client At Panel. Email: {Email}", request.Email);
    }

    public async Task BulkDisableClientsAsync(IEnumerable<string> emails, CancellationToken cancellationToken)
    {
        var emailList = emails.ToList();

        logger.LogInformation("Disabling Client(s) At Panel. Emails: {@Emails}", emailList);

        await PostAndValidateAsync("/admin/panel/api/clients/bulkDisable", new { emails = emailList },
            "Disabling clients", cancellationToken);

        logger.LogInformation("Successfully Disabled Client(s) At Panel. Emails: {@Emails}", emailList);
    }

    public async Task BulkEnableClientsAsync(IEnumerable<string> emails, CancellationToken cancellationToken)
    {
        var emailList = emails.ToList();

        logger.LogInformation("Enabling Client(s) At Panel. Emails: {@Emails}", emailList);

        await PostAndValidateAsync("/admin/panel/api/clients/bulkEnable", new { emails = emailList },
            "Enabling clients", cancellationToken);

        logger.LogInformation("Successfully Enabled Client(s) At Panel. Emails: {@Emails}", emailList);
    }

    public async Task<IReadOnlyList<PanelClientSummary>> GetAllClientsAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching All Clients From Panel");

        var response = await httpClient.GetFromJsonAsync<PanelApiResponse<List<PanelClientSummary>>>(
            "/admin/panel/api/clients/list", cancellationToken);

        if (response is null)
        {
            throw new PanelApiException("Panel returned an empty response");
        }

        if (!response.Success)
        {
            logger.LogError("Panel clients list fetch failed. Message: {Msg}", response.Msg);
            throw new PanelApiException($"Panel fetch failed: {response.Msg}");
        }

        var clients = response.Obj ?? new List<PanelClientSummary>();

        logger.LogInformation("Successfully Fetched All Clients From Panel. Count: {Count}", clients.Count);

        return clients;
    }

    public async Task ResetClientTrafficAsync(string email, CancellationToken cancellationToken)
    {
        logger.LogInformation("Resetting Client Traffic At Panel. Email: {Email}", email);

        await PostAndValidateAsync($"/admin/panel/api/clients/resetTraffic/{email}", new { }, "Resetting client traffic",
            cancellationToken);

        logger.LogInformation("Successfully Reset Client Traffic At Panel. Email: {Email}", email);
    }

    private async Task PostAndValidateAsync(string url, object body, string action,
        CancellationToken cancellationToken)
    {
        var responseMessage = await httpClient.PostAsJsonAsync(url, body, cancellationToken);

        PanelApiResponse<object>? apiResponse = null;
        if (responseMessage.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase)
            is true)
        {
            try
            {
                apiResponse = await responseMessage.Content.ReadFromJsonAsync<PanelApiResponse<object>>(cancellationToken);
            }
            catch (JsonException)
            {
                apiResponse = null;
            }
        }

        if (!responseMessage.IsSuccessStatusCode || apiResponse is null || !apiResponse.Success)
        {
            var message = apiResponse?.Msg ?? responseMessage.ReasonPhrase ?? "unknown";
            logger.LogError("{Action} failed. Status: {Status}, Message: {Message}", action,
                responseMessage.StatusCode, message);
            throw new PanelApiException($"{action} failed: {message}");
        }
    }
}
