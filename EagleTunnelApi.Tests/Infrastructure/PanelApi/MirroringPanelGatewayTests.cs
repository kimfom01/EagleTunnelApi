using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.PanelApi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.PanelApi;

public class MirroringPanelGatewayTests
{
    [Fact]
    public async Task AddClientAsync_AfterPanelWrite_MirrorsFetchedSnapshot()
    {
        var (gateway, inner, writer) = CreateGateway();
        var request = CreateCreate("new@example.com");
        inner.Details = CreateDetails(request.Email);

        await gateway.AddClientAsync(request, CancellationToken.None);

        Assert.Equal(["add:new@example.com"], inner.Writes);
        Assert.Equal(["new@example.com"], inner.Lookups);
        var mirrored = Assert.Single(writer.Calls);
        Assert.Equal(request.Email, mirrored.Client.Email);
    }

    [Fact]
    public async Task UpdateClientAsync_AfterPanelWrite_MirrorsFetchedSnapshot()
    {
        var (gateway, inner, writer) = CreateGateway();
        var request = CreateUpdate("user@example.com");

        await gateway.UpdateClientAsync(request, CancellationToken.None);

        Assert.Equal(["update:user@example.com"], inner.Writes);
        Assert.Equal(["user@example.com"], inner.Lookups);
        var mirrored = Assert.Single(writer.Calls);
        Assert.Equal(request.Email, mirrored.Client.Email);
    }

    [Fact]
    public async Task UpdateClientAsync_WithKeyEmail_MirrorsTheNewEmail()
    {
        var (gateway, inner, writer) = CreateGateway();
        var request = CreateUpdate("new@example.com");

        await gateway.UpdateClientAsync("old@example.com", request, CancellationToken.None);

        Assert.Equal(["update:old@example.com->new@example.com"], inner.Writes);
        Assert.Equal(["new@example.com"], inner.Lookups);
        Assert.Single(writer.Calls);
    }

    [Fact]
    public async Task BulkDisableAndEnable_MirrorEachDistinctEmailAfterSuccess()
    {
        var (gateway, inner, writer) = CreateGateway();

        await gateway.BulkDisableClientsAsync(
            ["a@example.com", "A@example.com", "b@example.com", " "], CancellationToken.None);

        var disabled = Assert.Single(inner.BulkEmails);
        Assert.Equal(["a@example.com", "A@example.com", "b@example.com", " "], disabled);
        Assert.Equal(2, writer.Calls.Count);

        await gateway.BulkEnableClientsAsync(["b@example.com"], CancellationToken.None);

        Assert.Equal(2, inner.BulkEmails.Count);
        Assert.Equal(["b@example.com"], inner.BulkEmails[1]);
        Assert.Equal(3, writer.Calls.Count);
        Assert.Equal(["a@example.com", "b@example.com", "b@example.com"], inner.Lookups);
    }

    [Fact]
    public async Task ResetClientTrafficAsync_MirrorsAfterSuccess()
    {
        var (gateway, inner, writer) = CreateGateway();

        await gateway.ResetClientTrafficAsync("user@example.com", CancellationToken.None);

        Assert.Equal(["reset:user@example.com"], inner.Writes);
        Assert.Single(writer.Calls);
    }

    [Fact]
    public async Task PanelWriteFailure_DoesNotAttemptMirror()
    {
        var (gateway, inner, writer) = CreateGateway();
        inner.WriteException = new PanelApiException("panel unavailable");

        await Assert.ThrowsAsync<PanelApiException>(() =>
            gateway.UpdateClientAsync(CreateUpdate(), CancellationToken.None));

        Assert.Empty(inner.Lookups);
        Assert.Equal(0, writer.Attempts);
    }

    [Fact]
    public async Task MirrorFailure_DoesNotFailThePanelWrite()
    {
        var (gateway, inner, writer) = CreateGateway();
        writer.Exception = new InvalidOperationException("postgres unavailable");

        await gateway.UpdateClientAsync(CreateUpdate(), CancellationToken.None);

        Assert.Single(inner.Writes);
        Assert.Single(inner.Lookups);
        Assert.Equal(1, writer.Attempts);
    }

    [Fact]
    public async Task MissingClientAfterWrite_IsSkippedWithoutFailing()
    {
        var (gateway, inner, writer) = CreateGateway();
        inner.Details = null;

        await gateway.ResetClientTrafficAsync("user@example.com", CancellationToken.None);

        Assert.Single(inner.Writes);
        Assert.Single(inner.Lookups);
        Assert.Equal(0, writer.Attempts);
    }

    private static (MirroringPanelGateway Gateway, FakePanelGateway Inner, FakeMirrorWriter Writer)
        CreateGateway()
    {
        var inner = new FakePanelGateway();
        var writer = new FakeMirrorWriter();
        var gateway = new MirroringPanelGateway(inner, writer,
            NullLogger<MirroringPanelGateway>.Instance);

        return (gateway, inner, writer);
    }

    private static PanelClientCreate CreateCreate(string email)
    {
        return new PanelClientCreate(email, true, 0, 0, 0, null, 1, "monthly", 0,
            "sub-id", "password", "auth", "flow", [1]);
    }

    private static PanelClientUpdate CreateUpdate(string email = "user@example.com")
    {
        return new PanelClientUpdate(email, true, 0, 0, 0, null, 1, 1, "monthly", 0, 0,
            null, "sub-id", "flow", [1]);
    }

    private static PanelClientDetails CreateDetails(string email)
    {
        var client = new PanelClient("uuid", email, true, 0, 0, 0, null, 1, 1,
            "monthly", 0, 0, null, "sub-id", "flow", 42, [1]);

        return new PanelClientDetails(client, null, [1], 123);
    }

    private sealed class FakePanelGateway : IPanelGateway
    {
        public PanelClientDetails? Details { get; set; } = CreateDetails("user@example.com");

        public Exception? WriteException { get; set; }

        public List<string> Writes { get; } = [];

        public List<string> Lookups { get; } = [];

        public List<string[]> BulkEmails { get; } = [];

        public Task<PanelClientDetails?> GetClientByTelegramIdAsync(long tgId,
            CancellationToken cancellationToken) => Task.FromResult<PanelClientDetails?>(null);

        public Task<PanelClientDetails?> GetClientByEmailAsync(string email,
            CancellationToken cancellationToken)
        {
            Lookups.Add(email);

            return Task.FromResult(Details);
        }

        public Task AddClientAsync(PanelClientCreate client, CancellationToken cancellationToken)
        {
            Writes.Add($"add:{client.Email}");

            return Complete();
        }

        public Task UpdateClientAsync(PanelClientUpdate request, CancellationToken cancellationToken)
        {
            Writes.Add($"update:{request.Email}");

            return Complete();
        }

        public Task UpdateClientAsync(string keyEmail, PanelClientUpdate request,
            CancellationToken cancellationToken)
        {
            Writes.Add($"update:{keyEmail}->{request.Email}");

            return Complete();
        }

        public Task BulkDisableClientsAsync(IEnumerable<string> emails,
            CancellationToken cancellationToken)
        {
            BulkEmails.Add(emails.ToArray());
            Writes.Add("disable");

            return Complete();
        }

        public Task BulkEnableClientsAsync(IEnumerable<string> emails,
            CancellationToken cancellationToken)
        {
            BulkEmails.Add(emails.ToArray());
            Writes.Add("enable");

            return Complete();
        }

        public Task<IReadOnlyList<PanelClientSummary>> GetAllClientsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PanelClientPage> GetClientsPageAsync(PanelClientPageRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PanelInboundOption>> GetInboundOptionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ResetClientTrafficAsync(string email, CancellationToken cancellationToken)
        {
            Writes.Add($"reset:{email}");

            return Complete();
        }

        private Task Complete()
        {
            return WriteException is null ? Task.CompletedTask : Task.FromException(WriteException);
        }
    }

    private sealed class FakeMirrorWriter : IPanelMirrorWriter
    {
        public List<PanelClientDetails> Calls { get; } = [];

        public int Attempts { get; private set; }

        public Exception? Exception { get; set; }

        public Task<PanelMirrorWriteResult> UpsertAsync(PanelClientDetails client,
            PanelTrafficSummary? traffic, CancellationToken cancellationToken)
        {
            Attempts++;

            if (Exception is not null)
                return Task.FromException<PanelMirrorWriteResult>(Exception);

            Calls.Add(client);

            return Task.FromResult(new PanelMirrorWriteResult(client.Client.Id, Guid.NewGuid(), null,
                true, []));
        }
    }
}
