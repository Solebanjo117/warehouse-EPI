using System.Globalization;
using System.Text.RegularExpressions;
using WarehouseEPI.Infrastructure.Labels;
using ZXing;
using ZXing.Common;

namespace WarehouseEPI.Tests.Labels;

public sealed partial class BarcodeRenderingServiceTests
{
    [Theory]
    [InlineData("ABC-123")]
    [InlineData("Y6-E-60SP-18M-CFX06-US")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890")]
    [InlineData("6")]
    [InlineData("12.5")]
    public void Generated_code128_is_deterministic_and_decodes_to_the_original_payload(string payload)
    {
        var service = new BarcodeRenderingService();

        var first = service.RenderCode128Svg(payload);
        var second = service.RenderCode128Svg(payload);

        Assert.Equal(first, second);
        Assert.Equal(payload, Decode(first));
        Assert.Contains("shape-rendering=\"crispEdges\"", first.Markup, StringComparison.Ordinal);
        Assert.Contains("preserveAspectRatio=\"none\"", first.Markup, StringComparison.Ordinal);
        Assert.Equal(first.Width, first.ModuleCount);
        Assert.True(first.ModuleCount > 20);
        var firstBlackBar = BlackRectangle().Matches(first.Markup).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Min();
        Assert.True(firstBlackBar >= 10, $"La zona silenciosa izquierda fue de {firstBlackBar}px.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC\n123")]
    [InlineData("CÓDIGO")]
    public void Rejects_empty_control_or_non_ascii_payloads(string payload)
    {
        var service = new BarcodeRenderingService();
        Assert.Throws<ArgumentException>(() => service.RenderCode128Svg(payload));
    }

    [Fact]
    public void Rejects_payloads_longer_than_the_operational_limit()
    {
        var service = new BarcodeRenderingService();
        Assert.Throws<ArgumentException>(() => service.RenderCode128Svg(new string('A', 81)));
    }

    [Fact]
    public void Printable_markup_characters_are_encoded_but_never_embedded_in_svg()
    {
        var barcode = new BarcodeRenderingService().RenderCode128Svg("<script>alert(1)</script>");
        Assert.DoesNotContain("<script>", barcode.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<script>alert(1)</script>", Decode(barcode));
    }

    [Fact]
    public void Print_metrics_snap_to_whole_203_dpi_dots_and_report_dense_codes()
    {
        var service = new BarcodeRenderingService();
        var probe = service.RenderCode128Svg("SKU-LARGO-1234567890");
        var safeWidthMils = (int)Math.Ceiling(probe.ModuleCount * 3m * 1000m / 203m);
        var safe = service.RenderCode128Svg("SKU-LARGO-1234567890", new(safeWidthMils, 150));
        var dense = service.RenderCode128Svg("SKU-LARGO-1234567890", new(120, 150));

        Assert.Equal(3, safe.DotsPerModule);
        Assert.False(safe.IsBelowRecommendedDensity);
        Assert.Equal(safe.ModuleCount * safe.DotsPerModule / 203m, safe.PrintWidthInches);
        Assert.Equal(safe.ModuleCount * 2m / 203m, safe.MinimumWidthInches);
        Assert.True(dense.IsBelowRecommendedDensity);
        Assert.True(dense.DotsPerModule < 2);
    }

    private static string Decode(BarcodeSvg barcode)
    {
        var pixels = Enumerable.Repeat((byte)255, barcode.Width * barcode.Height * 3).ToArray();
        foreach (Match rectangle in BlackRectangle().Matches(barcode.Markup))
        {
            var x = int.Parse(rectangle.Groups[1].Value, CultureInfo.InvariantCulture);
            var width = int.Parse(rectangle.Groups[2].Value, CultureInfo.InvariantCulture);
            for (var y = 0; y < barcode.Height; y++)
                Array.Clear(pixels, (y * barcode.Width + x) * 3, width * 3);
        }

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.CODE_128],
                TryHarder = true
            }
        };
        return reader.Decode(pixels, barcode.Width, barcode.Height, RGBLuminanceSource.BitmapFormat.RGB24)?.Text
            ?? throw new InvalidOperationException("ZXing no pudo volver a decodificar el SVG generado.");
    }

    [GeneratedRegex("<rect x=\\\"(\\d+)\\\" y=\\\"0\\\" width=\\\"(\\d+)\\\" height=\\\"\\d+\\\" fill=\\\"#000\\\"/>", RegexOptions.CultureInvariant)]
    private static partial Regex BlackRectangle();
}
