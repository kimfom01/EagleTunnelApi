using System.Net.Http.Json;
using System.Text.Json;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.PanelApi.Models;
using Microsoft.Extensions.Logging;

namespace EagleTunnelApi.Infrastructure.PanelApi;

public sealed class PanelApiGateway(HttpClient httpClient, ILogger<PanelApiGateway> logger) : IPanelGateway
{
    private static readonly string[] NotFoundPhrases =
    [
        "not found",
        "does not exist",
        "no such client",
        "no client found"
    ];

    public async Task<PanelClientDetails?> GetClientByTelegramIdAsync(long tgId,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching Client From Panel. TelegramId: {TelegramId}", tgId);

        var response = await GetJsonAsync<PanelApiResponseDto<List<PanelClientResponseDto>>>(
            $"/admin/panel/api/clients/get/tgId/{tgId}", cancellationToken);

        if (response is null) throw new PanelApiException("Panel returned an empty response");

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

        return Map(clientResponses[0]);
    }

    public async Task<PanelClientDetails?> GetClientByEmailAsync(string email, CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching Client From Panel. Email: {Email}", email);

        var response = await GetJsonAsync<PanelApiResponseDto<PanelClientResponseDto>>(
            $"/admin/panel/api/clients/get/{email}", cancellationToken);

        if (response is null) throw new PanelApiException("Panel returned an empty response");

        if (!response.Success)
        {
            if (IsDefinitiveNotFound(response.Msg))
            {
                logger.LogInformation("Client not found for Email: {Email}", email);
                return null;
            }

            logger.LogError("Panel fetch failed. Email: {Email}, Message: {Msg}", email, response.Msg);
            throw new PanelApiException($"Panel fetch failed: {response.Msg}");
        }

        return response.Obj is null ? null : Map(response.Obj);
    }

    public async Task AddClientAsync(PanelClientCreate client, CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating Client At Panel. Email: {Email}, InboundIds: {@InboundIds}",
            client.Email, client.InboundIds);

        var payload = new CreateClientPayloadDto(
            new CreateClientRequestDto(
                client.Email,
                client.Enable,
                client.ExpiryTime,
                client.TotalGB,
                client.TgId,
                client.Comment,
                client.LimitHwid,
                client.TrafficReset,
                client.TrafficResetDay,
                client.SubId,
                client.Password,
                client.Auth,
                client.Flow),
            client.InboundIds.ToList());

        await PostAndValidateAsync("/admin/panel/api/clients/add", payload, "Creating client", cancellationToken);

        logger.LogInformation("Successfully Created Client At Panel. Email: {Email}", client.Email);
    }

    public Task UpdateClientAsync(PanelClientUpdate request, CancellationToken cancellationToken)
    {
        return UpdateClientAsync(request.Email, request, cancellationToken);
    }

    public async Task UpdateClientAsync(string keyEmail, PanelClientUpdate request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Updating Client At Panel. Email: {Email}", request.Email);

        await PostAndValidateAsync($"/admin/panel/api/clients/update/{keyEmail}", ToDto(request),
            "Updating client", cancellationToken);

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

        var response = await GetJsonAsync<PanelApiResponseDto<List<PanelClientSummaryDto>>>(
            "/admin/panel/api/clients/list", cancellationToken);

        if (response is null) throw new PanelApiException("Panel returned an empty response");

        if (!response.Success)
        {
            logger.LogError("Panel clients list fetch failed. Message: {Msg}", response.Msg);
            throw new PanelApiException($"Panel fetch failed: {response.Msg}");
        }

        var clients = (response.Obj ?? []).Select(Map).ToList();

        logger.LogInformation("Successfully Fetched All Clients From Panel. Count: {Count}", clients.Count);

        return clients;
    }

    public async Task<IReadOnlyList<PanelInboundOption>> GetInboundOptionsAsync(
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching Inbound Options From Panel");

        var response = await GetJsonAsync<PanelApiResponseDto<List<PanelInboundOptionDto>>>(
            "/admin/panel/api/inbounds/options", cancellationToken);

        if (response is null) throw new PanelApiException("Panel returned an empty response");

        if (!response.Success)
        {
            logger.LogError("Panel inbound options fetch failed. Message: {Msg}", response.Msg);
            throw new PanelApiException($"Panel inbound options fetch failed: {response.Msg}");
        }

        var inbounds = (response.Obj ?? []).Select(Map).ToList();

        logger.LogInformation("Successfully Fetched Inbound Options From Panel. Count: {Count}", inbounds.Count);

        return inbounds;
    }

    public async Task ResetClientTrafficAsync(string email, CancellationToken cancellationToken)
    {
        logger.LogInformation("Resetting Client Traffic At Panel. Email: {Email}", email);

        await PostAndValidateAsync($"/admin/panel/api/clients/resetTraffic/{email}", new { },
            "Resetting client traffic",
            cancellationToken);

        logger.LogInformation("Successfully Reset Client Traffic At Panel. Email: {Email}", email);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<T>(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError("Panel request failed. Url: {Url}, Message: {Message}", url, ex.Message);
            throw new PanelApiException($"Panel request failed: {ex.Message}", ex);
        }
    }

    private async Task PostAndValidateAsync(string url, object body, string action,
        CancellationToken cancellationToken)
    {
        var responseMessage = await httpClient.PostAsJsonAsync(url, body, cancellationToken);

        PanelApiResponseDto<object>? apiResponse = null;
        if (responseMessage.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase)
            is true)
            try
            {
                apiResponse =
                    await responseMessage.Content.ReadFromJsonAsync<PanelApiResponseDto<object>>(cancellationToken);
            }
            catch (JsonException)
            {
                apiResponse = null;
            }

        if (!responseMessage.IsSuccessStatusCode || apiResponse is null || !apiResponse.Success)
        {
            var message = apiResponse?.Msg ?? responseMessage.ReasonPhrase ?? "unknown";
            logger.LogError("{Action} failed. Status: {Status}, Message: {Message}", action,
                responseMessage.StatusCode, message);
            throw new PanelApiException($"{action} failed: {message}");
        }
    }

    private static bool IsDefinitiveNotFound(string? message)
    {
        return message is not null && NotFoundPhrases.Any(phrase =>
            message.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static PanelClientDetails Map(PanelClientResponseDto response)
    {
        return new PanelClientDetails(Map(response.Client), response.ExternalLinks, response.InboundIds,
            response.UsedTraffic);
    }

    private static PanelClient Map(PanelClientDto client)
    {
        return new PanelClient(
            client.Uuid,
            client.Email,
            client.Enable,
            client.ExpiryTime,
            client.TgId,
            client.TotalGB,
            client.Comment,
            client.LimitIp,
            client.LimitHwid,
            client.TrafficReset,
            client.TrafficResetDay,
            client.Reset,
            client.Security,
            client.SubId,
            client.Flow,
            client.Id,
            client.InboundIds
        );
    }

    private static PanelClientSummary Map(PanelClientSummaryDto summary)
    {
        return new PanelClientSummary(
            summary.Id,
            summary.Email,
            summary.SubId,
            summary.Uuid,
            summary.TotalGB,
            summary.ExpiryTime,
            summary.Enable,
            summary.InboundIds,
            summary.Traffic is null
                ? null
                : new PanelTrafficSummary(summary.Traffic.Up, summary.Traffic.Down, summary.Traffic.Enable)
        );
    }

    private static PanelInboundOption Map(PanelInboundOptionDto inbound)
    {
        return new PanelInboundOption(
            inbound.Id,
            inbound.Remark,
            inbound.Tag,
            inbound.Protocol,
            inbound.Port,
            inbound.TlsFlowCapable
        );
    }

    private static UpdateClientRequestDto ToDto(PanelClientUpdate request)
    {
        return new UpdateClientRequestDto(
            request.Email,
            request.Enable,
            request.ExpiryTime,
            request.TotalGB,
            request.TgId,
            request.Comment,
            request.LimitIp,
            request.LimitHwid,
            request.TrafficReset,
            request.TrafficResetDay,
            request.Reset,
            request.Security,
            request.SubId,
            request.Flow,
            request.InboundIds?.ToList()
        );
    }
}
