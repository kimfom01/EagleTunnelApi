using System.Net;
using System.Text.Json;
using EagleTunnelApi.Infrastructure.PanelApi;
using EagleTunnelApi.Infrastructure.PanelApi.Models;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EagleTunnelApi.Tests.PanelApi;

public class PanelInboundPreflightServiceTests
{
    private const string PanelBaseUri = "https://panel.test";

    private static (PanelInboundPreflightService Service, CapturingLogger<PanelInboundPreflightService> Logger)
        CreateService(IEnumerable<int> availableInboundIds, params int[] effectiveInboundIds)
    {
        var inbounds = availableInboundIds
            .Select(id => new PanelInboundOptionDto(id, $"Inbound {id}", $"tag-{id}", "vless", 443, true))
            .ToList();

        var json = JsonSerializer.Serialize(new PanelApiResponseDto<List<PanelInboundOptionDto>>(true, "ok", inbounds));

        var panelClient = new PanelApiGateway(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            }))
            {
                BaseAddress = new Uri(PanelBaseUri)
            },
            NullLogger<PanelApiGateway>.Instance);

        var logger = new CapturingLogger<PanelInboundPreflightService>();
        var directory = new StubPanelDirectory { DefaultInboundIds = effectiveInboundIds };

        var service = new PanelInboundPreflightService(panelClient, directory, logger);

        return (service, logger);
    }

    [Fact]
    public async Task Check_AllEffectiveInboundsExist_LogsInformation()
    {
        var (service, logger) = CreateService([2, 6], 2, 6);

        await service.CheckAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains(logger.Entries,
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("preflight passed") &&
                entry.Message.Contains("2, 6"));
    }

    [Fact]
    public async Task Check_StaleInboundId_LogsCriticalWithMissingAndAvailableIds()
    {
        var (service, logger) = CreateService([2, 6], 1, 2);

        await service.CheckAsync(CancellationToken.None);

        var critical = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains("1", critical.Message);
        Assert.Contains("2", critical.Message);
        Assert.Contains("6", critical.Message);
        Assert.Contains("effective default inbound ids", critical.Message);
    }

    [Fact]
    public async Task Check_PanelFailure_LogsWarningAndDoesNotThrow()
    {
        var panelClient = new PanelApiGateway(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            {
                BaseAddress = new Uri(PanelBaseUri)
            },
            NullLogger<PanelApiGateway>.Instance);

        var logger = new CapturingLogger<PanelInboundPreflightService>();
        var directory = new StubPanelDirectory { DefaultInboundIds = [2, 6] };
        var service = new PanelInboundPreflightService(panelClient, directory, logger);

        await service.CheckAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Critical);
    }
}
