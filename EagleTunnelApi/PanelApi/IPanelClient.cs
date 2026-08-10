using EagleTunnelApi.PanelApi.Models;

namespace EagleTunnelApi.PanelApi;

public interface IPanelClient
{
    Task<PanelClientResponse?> GetClientByTelegramIdAsync(long tgId, CancellationToken cancellationToken);

    Task AddClientAsync(CreateClientPayload payload, CancellationToken cancellationToken);

    Task UpdateClientAsync(UpdateClientRequest request, CancellationToken cancellationToken);

    Task BulkDisableClientsAsync(IEnumerable<string> emails, CancellationToken cancellationToken);
}
