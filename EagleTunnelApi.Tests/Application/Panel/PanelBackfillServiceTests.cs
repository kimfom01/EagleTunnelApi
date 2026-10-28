using EagleTunnelApi.Application.Panel;
using Xunit;
using PanelClientModel = EagleTunnelApi.Application.Panel.PanelClient;

namespace EagleTunnelApi.Tests.Application.Panel;

public class PanelBackfillServiceTests
{
    [Fact]
    public async Task RunAsync_pages_through_the_panel_and_applies_every_full_client()
    {
        var rows = Enumerable.Range(1, 201)
            .Select(index => CreateRow($"user{index}@example.com", index))
            .ToArray();

        var gateway = new FakePanelGateway(rows);
        var writer = new FakeMirrorWriter();
        var comments = new FakeCommentBackfill(new CommentBackfillReport(7, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
            11, []));

        var service = new PanelBackfillService(gateway, writer, comments);
        var report = await service.RunAsync(CancellationToken.None);

        Assert.Equal(201, report.PanelRowsScanned);
        Assert.Equal(201, report.MirrorApplied);
        Assert.Equal(0, report.MirrorConflicted);
        Assert.Equal(0, report.DetailLookupFailures);
        Assert.Empty(report.Problems);
        Assert.Equal(7, report.Comments.ClientsScanned);
        Assert.Equal(new[] { 1, 2 }, gateway.RequestedPages);
        Assert.Equal(201, writer.Calls.Count);

        var firstCall = writer.Calls[0];
        Assert.Equal("user1@example.com", firstCall.Details.Client.Email);
        Assert.NotNull(firstCall.Traffic);
        Assert.Equal(1, firstCall.Traffic!.Up);
        Assert.Equal(2, firstCall.Traffic.Down);
    }

    [Fact]
    public async Task RunAsync_reports_missing_details_and_identity_conflicts()
    {
        var rows = new[]
        {
            CreateRow("missing@example.com", 1),
            CreateRow("conflict@example.com", 2)
        };

        var gateway = new FakePanelGateway(rows)
        {
            MissingEmail = "missing@example.com"
        };

        var conflict = new PanelMirrorConflict(Guid.NewGuid(), "tg:123", PanelConflictTypes.Telegram,
            true, false);

        var writer = new FakeMirrorWriter
        {
            ResultFactory = details => new PanelMirrorWriteResult(details.Client.Id, null, null, false,
                [conflict])
        };

        var service = new PanelBackfillService(gateway, writer,
            new FakeCommentBackfill(new CommentBackfillReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])));

        var report = await service.RunAsync(CancellationToken.None);

        Assert.Equal(2, report.PanelRowsScanned);
        Assert.Equal(0, report.MirrorApplied);
        Assert.Equal(1, report.MirrorConflicted);
        Assert.Equal(1, report.DetailLookupFailures);
        Assert.Contains(report.Problems, problem => problem.Contains("disappeared"));
        Assert.Contains(report.Problems, problem => problem.Contains("tg:123"));
    }

    private static PanelClientPageRow CreateRow(string email, int index)
    {
        return new PanelClientPageRow(
            email,
            $"sub-{index}",
            true,
            DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
            100,
            1,
            1,
            0,
            0,
            0,
            0,
            [1],
            new PanelTrafficSummary(index, index + 1, true),
            DateTimeOffset.UtcNow.AddDays(-10),
            DateTimeOffset.UtcNow);
    }

    private static PanelClientDetails CreateDetails(string email, long panelClientId)
    {
        var client = new PanelClientModel(
            $"uuid-{panelClientId}",
            email,
            true,
            DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
            0,
            100,
            null,
            1,
            1,
            "monthly",
            0,
            0,
            null,
            $"sub-{panelClientId}",
            null,
            panelClientId,
            [1]);

        return new PanelClientDetails(client, null, [1], 0);
    }

    private sealed class FakePanelGateway(IReadOnlyList<PanelClientPageRow> rows) : IPanelGateway
    {
        public List<int> RequestedPages { get; } = [];

        public string? MissingEmail { get; init; }

        public Task<PanelClientPage> GetClientsPageAsync(PanelClientPageRequest request,
            CancellationToken cancellationToken)
        {
            RequestedPages.Add(request.Page);

            var items = rows
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToArray();

            return Task.FromResult(new PanelClientPage(items, request.Page, request.PageSize, rows.Count,
                rows.Count));
        }

        public Task<PanelClientDetails?> GetClientByEmailAsync(string email,
            CancellationToken cancellationToken)
        {
            if (email == MissingEmail) return Task.FromResult<PanelClientDetails?>(null);

            var index = rows
                .Select((row, position) => (row, position))
                .First(pair => pair.row.Email == email)
                .position;

            return Task.FromResult<PanelClientDetails?>(CreateDetails(email, index + 1));
        }

        public Task<PanelClientDetails?> GetClientByTelegramIdAsync(long tgId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddClientAsync(PanelClientCreate client, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateClientAsync(PanelClientUpdate request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateClientAsync(string keyEmail, PanelClientUpdate request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task BulkDisableClientsAsync(IEnumerable<string> emails,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task BulkEnableClientsAsync(IEnumerable<string> emails,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PanelClientSummary>> GetAllClientsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PanelInboundOption>> GetInboundOptionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ResetClientTrafficAsync(string email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMirrorWriter : IPanelMirrorWriter
    {
        public List<(PanelClientDetails Details, PanelTrafficSummary? Traffic)> Calls { get; } = [];

        public Func<PanelClientDetails, PanelMirrorWriteResult>? ResultFactory { get; init; }

        public Task<PanelMirrorWriteResult> UpsertAsync(PanelClientDetails client,
            PanelTrafficSummary? traffic, CancellationToken cancellationToken)
        {
            Calls.Add((client, traffic));

            if (ResultFactory is not null) return Task.FromResult(ResultFactory(client));

            return Task.FromResult(new PanelMirrorWriteResult(client.Client.Id, Guid.NewGuid(),
                Guid.NewGuid(), true, []));
        }
    }

    private sealed class FakeCommentBackfill(CommentBackfillReport report) : ICommentBackfill
    {
        public Task<CommentBackfillReport> RunAsync(CancellationToken cancellationToken) =>
            Task.FromResult(report);
    }
}
