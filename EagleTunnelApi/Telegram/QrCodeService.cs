using QRCoder;

namespace EagleTunnelApi.Telegram;

/// <summary>
/// Generates QR code images for subscription links so an inviter can show
/// the code in person (or forward the image) when a plain link cannot be sent.
/// Pure-managed PNG rendering (no System.Drawing) so it runs on Linux containers.
/// </summary>
public static class QrCodeService
{
    public static byte[] GeneratePng(string payload, int pixelsPerModule = 20)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("QR payload must not be empty.", nameof(payload));

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}
