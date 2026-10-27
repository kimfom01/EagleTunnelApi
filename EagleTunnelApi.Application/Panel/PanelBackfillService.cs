namespace EagleTunnelApi.Application.Panel;

public sealed class PanelBackfillService(
    IPanelGateway panelGateway,
    IPanelMirrorWriter mirrorWriter,
    ICommentBackfill commentBackfill) : IPanelBackfill
{
    private const int PageSize = 200;
    private const int MaxPages = 10_000;

    public async Task<PanelBackfillReport> RunAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var scanned = 0;
        var applied = 0;
        var conflicted = 0;
        var detailFailures = 0;

        for (var pageNumber = 1; pageNumber <= MaxPages; pageNumber++)
        {
            var page = await panelGateway.GetClientsPageAsync(
                new PanelClientPageRequest(pageNumber, PageSize, PanelClientSortFields.Email,
                    PanelSortOrders.Ascend),
                cancellationToken);

            if (page.Items.Count == 0) break;

            foreach (var row in page.Items)
            {
                scanned++;

                var details = await panelGateway.GetClientByEmailAsync(row.Email, cancellationToken);

                if (details is null)
                {
                    detailFailures++;
                    problems.Add($"Panel client '{row.Email}' disappeared before its detail could be read.");
                    continue;
                }

                var result = await mirrorWriter.UpsertAsync(details, row.Traffic, cancellationToken);

                if (result.Applied)
                {
                    applied++;
                }
                else
                {
                    conflicted++;
                }

                problems.AddRange(result.Conflicts.Select(conflict =>
                    $"Panel client '{row.Email}' {conflict.Type} conflict: {conflict.DedupeKey}"));
            }

            var effectivePageSize = page.PageSize > 0 ? page.PageSize : PageSize;

            if (page.Total > 0 && pageNumber * effectivePageSize >= page.Total) break;
            if (page.Items.Count < effectivePageSize) break;
        }

        var comments = await commentBackfill.RunAsync(cancellationToken);

        return new PanelBackfillReport(scanned, applied, conflicted, detailFailures, problems, comments);
    }
}
