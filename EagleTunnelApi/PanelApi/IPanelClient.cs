using EagleTunnelApi.PanelApi.Models;

namespace EagleTunnelApi.PanelApi;

public interface IPanelClient
{
    Task<PanelClientResponse?> GetClientByTelegramIdAsync(long tgId, CancellationToken cancellationToken);

    Task<PanelClientResponse?> GetClientByEmailAsync(string email, CancellationToken cancellationToken);

    Task AddClientAsync(CreateClientPayload payload, CancellationToken cancellationToken);

    Task UpdateClientAsync(UpdateClientRequest request, CancellationToken cancellationToken);

    Task UpdateClientAsync(string keyEmail, UpdateClientRequest request, CancellationToken cancellationToken);

    Task BulkDisableClientsAsync(IEnumerable<string> emails, CancellationToken cancellationToken);

    Task BulkEnableClientsAsync(IEnumerable<string> emails, CancellationToken cancellationToken);

    Task<IReadOnlyList<PanelClientSummary>> GetAllClientsAsync(CancellationToken cancellationToken);

    Task ResetClientTrafficAsync(string email, CancellationToken cancellationToken);
}