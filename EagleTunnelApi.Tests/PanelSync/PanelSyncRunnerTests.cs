using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.PanelSync;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EagleTunnelApi.Tests.PanelSync;

public class PanelSyncRunnerTests
{
    [Fact]
    public async Task RunNowAsync_ResolvesTheScopedSyncAndUsesCursorMode()
    {
        var sync = new StubPanelSync();
        var services = new ServiceCollection();
        services.AddScoped<IPanelSync>(_ => sync);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        var runner = new PanelSyncRunner(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await runner.RunNowAsync("admin-owner", CancellationToken.None);

        Assert.Same(sync.Result, result);
        Assert.Equal(PanelSyncMode.Cursor, sync.Mode);
        Assert.Equal("admin-owner", sync.Owner);
    }

    private sealed class StubPanelSync : IPanelSync
    {
        public PanelSyncResult Result { get; } = new(false, "cursor", 1, 1, 0, 0, 0, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

        public PanelSyncMode? Mode { get; private set; }

        public string? Owner { get; private set; }

        public Task<PanelSyncResult> RunAsync(PanelSyncMode mode, string owner,
            CancellationToken cancellationToken)
        {
            Mode = mode;
            Owner = owner;

            return Task.FromResult(Result);
        }
    }
}
