using System.Globalization;
using System.Text;
using ZXing;
using ZXing.Common;

namespace WarehouseEPI.Infrastructure.Labels;

public sealed record Code128RenderOptions(int Width = 900, int Height = 150, int QuietZoneModules = 10, int PrinterDpi = 203)
{
    public void Validate()
    {
        if (Width is < 120 or > 11000)
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (Height is < 32 or > 800)
            throw new ArgumentOutOfRangeException(nameof(Height));
        if (QuietZoneModules is < 10 or > 100)
            throw new ArgumentOutOfRangeException(nameof(QuietZoneModules));
        if (PrinterDpi is < 100 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(PrinterDpi));
    }
}

public sealed record BarcodeSvg(
    string Payload,
    int Width,
    int Height,
    int ModuleCount,
    int DotsPerModule,
    decimal PrintWidthInches,
    decimal MinimumWidthInches,
    bool IsBelowRecommendedDensity,
    string Markup);

public sealed class BarcodeRenderingService
{
    private readonly MultiFormatWriter writer = new();

    public BarcodeSvg RenderCode128Svg(string payload, Code128RenderOptions? options = null)
    {
        ValidatePayload(payload);
        options ??= new Code128RenderOptions();
        options.Validate();

        var matrix = writer.encode(
            payload,
            BarcodeFormat.CODE_128,
            0,
            1,
            new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = options.QuietZoneModules,
                [EncodeHintType.CHARACTER_SET] = "ISO-8859-1"
            });

        var moduleCount = matrix.Width;
        var availableDots = options.Width * options.PrinterDpi / 1000;
        var dotsPerModule = availableDots / moduleCount;
        var printWidthInches = dotsPerModule > 0
            ? (decimal)moduleCount * dotsPerModule / options.PrinterDpi
            : (decimal)options.Width / 1000;
        var minimumWidthInches = (decimal)moduleCount * 2 / options.PrinterDpi;

        var markup = new StringBuilder(moduleCount * 4);
        markup.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
            .Append(moduleCount.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(options.Height.ToString(CultureInfo.InvariantCulture))
            .Append("\" role=\"img\" aria-label=\"Código de barras Code 128\" preserveAspectRatio=\"none\" shape-rendering=\"crispEdges\">")
            .Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>");

        for (var x = 0; x < moduleCount;)
        {
            if (!matrix[x, 0])
            {
                x++;
                continue;
            }

            var start = x;
            while (x < matrix.Width && matrix[x, 0])
                x++;
            markup.Append("<rect x=\"").Append(start.ToString(CultureInfo.InvariantCulture))
                .Append("\" y=\"0\" width=\"").Append((x - start).ToString(CultureInfo.InvariantCulture))
                .Append("\" height=\"").Append(options.Height.ToString(CultureInfo.InvariantCulture))
                .Append("\" fill=\"#000\"/>");
        }

        markup.Append("</svg>");
        return new(payload, moduleCount, options.Height, moduleCount, dotsPerModule, printWidthInches,
            minimumWidthInches, dotsPerModule < 2, markup.ToString());
    }

    private static void ValidatePayload(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is < 1 or > 80)
            throw new ArgumentException("El contenido de Code 128 debe tener entre 1 y 80 caracteres.", nameof(payload));
        if (payload.Any(character => character is < ' ' or > '~'))
            throw new ArgumentException("El contenido de Code 128 solo admite caracteres ASCII imprimibles.", nameof(payload));
    }
}
