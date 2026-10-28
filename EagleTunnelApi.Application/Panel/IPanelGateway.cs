namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Panel read/write port. Implementations own the panel wire format and map it
/// to the application models in this namespace.
/// </summary>
public interface IPanelGateway
{
    /// <summary>
    /// Finds a panel client by Telegram id. Returns <see langword="null"/> only
    /// for a definitive not-found response; transport and panel failures throw
    /// <see cref="PanelApiException"/>.
    /// </summary>
    Task<PanelClientDetails?> GetClientByTelegramIdAsync(long tgId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds a panel client by email. Returns <see langword="null"/> only for a
    /// definitive not-found response; transport and panel failures throw
    /// <see cref="PanelApiException"/>.
    /// </summary>
    Task<PanelClientDetails?> GetClientByEmailAsync(string email, CancellationToken cancellationToken);

    Task AddClientAsync(PanelClientCreate client, CancellationToken cancellationToken);

    Task UpdateClientAsync(PanelClientUpdate request, CancellationToken cancellationToken);

    Task UpdateClientAsync(string keyEmail, PanelClientUpdate request, CancellationToken cancellationToken);

    Task BulkDisableClientsAsync(IEnumerable<string> emails, CancellationToken cancellationToken);

    Task BulkEnableClientsAsync(IEnumerable<string> emails, CancellationToken cancellationToken);

    Task<IReadOnlyList<PanelClientSummary>> GetAllClientsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Fetches one page of slim panel client rows. The page is a change
    /// detector, not a complete client record; changed rows must be refreshed
    /// with <see cref="GetClientByEmailAsync"/>.
    /// </summary>
    Task<PanelClientPage> GetClientsPageAsync(PanelClientPageRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PanelInboundOption>> GetInboundOptionsAsync(CancellationToken cancellationToken);

    Task ResetClientTrafficAsync(string email, CancellationToken cancellationToken);
}
