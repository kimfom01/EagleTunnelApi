using EagleTunnelApi.Telegram;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class QrCodeServiceTests
{
    [Fact]
    public void GeneratePng_ReturnsValidPng()
    {
        var png = QrCodeService.GeneratePng("https://panel.test:2096/add/sub123");

        Assert.NotEmpty(png);
        // PNG magic bytes: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
    }

    [Fact]
    public void GeneratePng_EmptyPayload_Throws()
    {
        Assert.Throws<ArgumentException>(() => QrCodeService.GeneratePng("  "));
    }
}
