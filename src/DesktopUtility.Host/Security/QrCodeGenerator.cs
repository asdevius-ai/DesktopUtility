using QRCoder;

namespace DesktopUtility.Host.Security;

public static class QrCodeGenerator
{
    /// <summary>Renders <paramref name="content"/> as a PNG QR code.</summary>
    public static byte[] GeneratePngBytes(string content, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(pixelsPerModule);
    }
}
