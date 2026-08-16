using EasyOcrSharp.Barcodes;
using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using ZXing.Common;

// ZXing has its own BarcodeFormat; alias it so the unqualified name keeps meaning the library's
// public enum, which is what these tests are asserting against.
using ZFormat = ZXing.BarcodeFormat;
using ZWriter = ZXing.BarcodeWriterPixelData;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for barcode and QR reading. Every fixture is generated at test time with ZXing's own
/// writer and round-tripped back through the public API, so these run with no models, no network and
/// no committed binary assets.
/// </summary>
public class BarcodeTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>Renders a barcode into a white image using ZXing's platform-independent writer.</summary>
    private static Image<Rgb24> Render(
        ZFormat format,
        string payload,
        int width = 320,
        int height = 320)
    {
        var writer = new ZWriter
        {
            Format = format,
            Options = new EncodingOptions { Width = width, Height = height, Margin = 8, PureBarcode = true },
        };

        var pixels = writer.Write(payload);
        var image = new Image<Rgb24>(pixels.Width, pixels.Height);

        // BarcodeWriterPixelData emits BGRA, four bytes per pixel.
        var data = pixels.Pixels;
        for (int y = 0; y < pixels.Height; y++)
        {
            for (int x = 0; x < pixels.Width; x++)
            {
                int i = ((y * pixels.Width) + x) * 4;
                image[x, y] = new Rgb24(data[i + 2], data[i + 1], data[i]);
            }
        }

        return image;
    }

    /// <summary>A plain white image containing nothing to find.</summary>
    private static Image<Rgb24> Blank(int width = 200, int height = 200)
    {
        var image = new Image<Rgb24>(width, height);
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));
        return image;
    }

    /// <summary>Places <paramref name="source"/> onto a larger white canvas at (x, y).</summary>
    private static Image<Rgb24> PlaceOnCanvas(Image<Rgb24> source, int canvasWidth, int canvasHeight, int x, int y)
    {
        var canvas = new Image<Rgb24>(canvasWidth, canvasHeight);
        canvas.Mutate(ctx => ctx.BackgroundColor(Color.White).DrawImage(source, new Point(x, y), 1f));
        return canvas;
    }

    // ---------------------------------------------------------------- round-trip

    [Theory]
    [InlineData(ZFormat.QR_CODE, "https://example.com/ocr?id=42", BarcodeFormat.QrCode)]
    [InlineData(ZFormat.CODE_128, "ABC-12345", BarcodeFormat.Code128)]
    [InlineData(ZFormat.CODE_39, "HELLO39", BarcodeFormat.Code39)]
    [InlineData(ZFormat.EAN_13, "5901234123457", BarcodeFormat.Ean13)]
    [InlineData(ZFormat.PDF_417, "PDF417 payload", BarcodeFormat.Pdf417)]
    [InlineData(ZFormat.DATA_MATRIX, "DM-PAYLOAD", BarcodeFormat.DataMatrix)]
    public async Task A_generated_code_round_trips_with_its_format_mapped(
        ZFormat written,
        string payload,
        BarcodeFormat expected)
    {
        using var image = Render(written, payload);

        var codes = await BarcodeScanner.ReadBarcodesAsync(image);

        var code = Assert.Single(codes);
        Assert.Equal(payload, code.Text);
        Assert.Equal(expected, code.Format);
    }

    [Fact]
    public async Task Latin1_payloads_survive_the_round_trip()
    {
        // ZXing's QR writer encodes as ISO-8859-1 unless told otherwise, so this is the accented text
        // that round-trips with the writer's defaults.
        using var image = Render(ZFormat.QR_CODE, "naïve café");

        var code = Assert.Single(await BarcodeScanner.ReadBarcodesAsync(image));

        Assert.Equal("naïve café", code.Text);
    }

    [Fact]
    public async Task Utf8_payloads_survive_the_round_trip_when_the_code_declares_the_character_set()
    {
        // A QR code carrying non-Latin-1 text has to declare UTF-8 via an ECI segment; the reader then
        // picks it up automatically. This proves the decoded text reaches the caller intact.
        var writer = new ZWriter
        {
            Format = ZFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions
            {
                Width = 360,
                Height = 360,
                Margin = 8,
                CharacterSet = "UTF-8",
            },
        };

        var pixels = writer.Write("日本語 — ✓");
        using var image = new Image<Rgb24>(pixels.Width, pixels.Height);
        var data = pixels.Pixels;
        for (int y = 0; y < pixels.Height; y++)
        {
            for (int x = 0; x < pixels.Width; x++)
            {
                int i = ((y * pixels.Width) + x) * 4;
                image[x, y] = new Rgb24(data[i + 2], data[i + 1], data[i]);
            }
        }

        var code = Assert.Single(await BarcodeScanner.ReadBarcodesAsync(image));

        Assert.Equal("日本語 — ✓", code.Text);
    }

    [Fact]
    public async Task The_reported_geometry_lies_inside_the_image()
    {
        using var image = Render(ZFormat.QR_CODE, "geometry");

        var code = Assert.Single(await BarcodeScanner.ReadBarcodesAsync(image));

        Assert.NotEmpty(code.BoundingPolygon);
        Assert.All(code.BoundingPolygon, p =>
        {
            Assert.InRange(p.X, 0, image.Width);
            Assert.InRange(p.Y, 0, image.Height);
        });

        Assert.InRange(code.BoundingBox.MinX, 0, image.Width);
        Assert.InRange(code.BoundingBox.MinY, 0, image.Height);
        Assert.True(code.BoundingBox.Width > 0, "the bounding box should not be degenerate");
        Assert.True(code.BoundingBox.Height > 0, "the bounding box should not be degenerate");
    }

    [Fact]
    public async Task Raw_bytes_are_exposed_for_binary_payloads()
    {
        using var image = Render(ZFormat.QR_CODE, "raw-bytes");

        var code = Assert.Single(await BarcodeScanner.ReadBarcodesAsync(image));

        Assert.False(code.RawBytes.IsEmpty);
    }

    // ---------------------------------------------------------------- no-match behaviour

    [Fact]
    public async Task An_image_with_no_barcode_returns_an_empty_list_rather_than_throwing()
    {
        using var image = Blank();

        var codes = await BarcodeScanner.ReadBarcodesAsync(image);

        Assert.NotNull(codes);
        Assert.Empty(codes);
    }

    [Fact]
    public async Task A_single_pixel_image_is_handled_without_throwing()
    {
        using var image = new Image<Rgb24>(1, 1);

        Assert.Empty(await BarcodeScanner.ReadBarcodesAsync(image));
    }

    // ---------------------------------------------------------------- options

    [Fact]
    public async Task MultipleCodes_finds_every_code_while_the_default_stops_at_one()
    {
        using var first = Render(ZFormat.QR_CODE, "left", 200, 200);
        using var second = Render(ZFormat.QR_CODE, "right", 200, 200);

        using var canvas = new Image<Rgb24>(520, 240);
        canvas.Mutate(ctx => ctx
            .BackgroundColor(Color.White)
            .DrawImage(first, new Point(10, 20), 1f)
            .DrawImage(second, new Point(300, 20), 1f));

        var many = await BarcodeScanner.ReadBarcodesAsync(canvas, new BarcodeOptions { MultipleCodes = true });
        var one = await BarcodeScanner.ReadBarcodesAsync(canvas, new BarcodeOptions { MultipleCodes = false });

        Assert.Equal(2, many.Count);
        Assert.Equal(new[] { "left", "right" }, many.Select(c => c.Text).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Single(one);
    }

    [Fact]
    public async Task Formats_filtering_ignores_a_symbology_that_was_not_requested()
    {
        using var image = Render(ZFormat.CODE_128, "ABC-12345");

        var asQr = await BarcodeScanner.ReadBarcodesAsync(
            image,
            new BarcodeOptions { Formats = new[] { BarcodeFormat.QrCode } });

        var asCode128 = await BarcodeScanner.ReadBarcodesAsync(
            image,
            new BarcodeOptions { Formats = new[] { BarcodeFormat.Code128 } });

        Assert.Empty(asQr);
        Assert.Single(asCode128);
    }

    [Fact]
    public async Task A_region_restricts_the_scan_to_part_of_the_page()
    {
        using var code = Render(ZFormat.QR_CODE, "top-left", 180, 180);
        using var canvas = PlaceOnCanvas(code, 600, 400, 10, 10);

        // The code sits entirely in the top-left; scanning only the bottom half must not find it.
        var bottom = await BarcodeScanner.ReadBarcodesAsync(
            canvas,
            new BarcodeOptions { Region = OcrRegion.Fraction(0, 0.55, 1, 0.45) });

        var top = await BarcodeScanner.ReadBarcodesAsync(
            canvas,
            new BarcodeOptions { Region = OcrRegion.Fraction(0, 0, 0.6, 0.7) });

        Assert.Empty(bottom);
        Assert.Single(top);
        Assert.Equal("top-left", top[0].Text);
    }

    [Fact]
    public async Task A_region_reports_coordinates_in_the_original_image_space()
    {
        using var code = Render(ZFormat.QR_CODE, "offset", 160, 160);
        using var canvas = PlaceOnCanvas(code, 600, 400, 380, 200);

        var found = await BarcodeScanner.ReadBarcodesAsync(
            canvas,
            new BarcodeOptions { Region = OcrRegion.Pixels(360, 180, 240, 220) });

        var hit = Assert.Single(found);

        // Had the region offset been dropped, the box would sit near the origin instead.
        Assert.True(hit.BoundingBox.MinX > 300, $"expected the box near x=380 in page space, got {hit.BoundingBox.MinX}");
        Assert.True(hit.BoundingBox.MinY > 150, $"expected the box near y=200 in page space, got {hit.BoundingBox.MinY}");
    }

    [Fact]
    public async Task TryInverted_is_what_makes_a_colour_inverted_code_readable()
    {
        using var image = Render(ZFormat.QR_CODE, "inverted");
        image.Mutate(ctx => ctx.Invert());

        var without = await BarcodeScanner.ReadBarcodesAsync(image, new BarcodeOptions { TryInverted = false });
        var with = await BarcodeScanner.ReadBarcodesAsync(image, new BarcodeOptions { TryInverted = true });

        Assert.Empty(without);
        Assert.Equal("inverted", Assert.Single(with).Text);
    }

    [Fact]
    public async Task MaxImagePixels_guards_the_overloads_that_decode_the_image_themselves()
    {
        using var image = Render(ZFormat.QR_CODE, "guarded");
        using var buffer = new MemoryStream();
        await image.SaveAsPngAsync(buffer);
        byte[] png = buffer.ToArray();

        await Assert.ThrowsAnyAsync<Exception>(() => BarcodeScanner.ReadBarcodesAsync(
            png,
            new BarcodeOptions { MaxImagePixels = 16 }));
    }

    [Fact]
    public async Task MaxImagePixels_does_not_apply_to_an_image_the_caller_already_decoded()
    {
        // Documented contract: an image you already hold has by definition already been decoded, so
        // the pixel-flood guard has nothing left to protect against and is deliberately not applied.
        using var image = Render(ZFormat.QR_CODE, "already-decoded");

        var codes = await BarcodeScanner.ReadBarcodesAsync(image, new BarcodeOptions { MaxImagePixels = 16 });

        Assert.Equal("already-decoded", Assert.Single(codes).Text);
    }

    // ---------------------------------------------------------------- input overloads

    [Fact]
    public async Task Bytes_and_streams_decode_the_same_code_as_the_decoded_image()
    {
        using var image = Render(ZFormat.QR_CODE, "overloads");

        using var buffer = new MemoryStream();
        await image.SaveAsPngAsync(buffer);
        byte[] png = buffer.ToArray();

        var fromBytes = await BarcodeScanner.ReadBarcodesAsync(png);

        using var stream = new MemoryStream(png);
        var fromStream = await BarcodeScanner.ReadBarcodesAsync(stream);

        Assert.Equal("overloads", Assert.Single(fromBytes).Text);
        Assert.Equal("overloads", Assert.Single(fromStream).Text);
    }

    [Fact]
    public async Task A_file_path_decodes_the_same_code()
    {
        using var image = Render(ZFormat.QR_CODE, "from-disk");
        string path = Path.Combine(Path.GetTempPath(), $"barcode-{Guid.NewGuid():N}.png");

        try
        {
            await image.SaveAsPngAsync(path);

            Assert.Equal("from-disk", Assert.Single(await BarcodeScanner.ReadBarcodesAsync(path)).Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- cancellation & guards

    [Fact]
    public async Task A_cancelled_token_stops_the_scan()
    {
        using var image = Render(ZFormat.QR_CODE, "cancelled");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BarcodeScanner.ReadBarcodesAsync(image, BarcodeOptions.Default, cts.Token));
    }

    [Fact]
    public async Task A_null_image_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => BarcodeScanner.ReadBarcodesAsync((Image<Rgb24>)null!));
    }

    [Fact]
    public async Task The_caller_keeps_ownership_of_the_image()
    {
        using var image = Render(ZFormat.QR_CODE, "not-disposed");

        _ = await BarcodeScanner.ReadBarcodesAsync(image);

        // Would throw ObjectDisposedException had the scanner disposed the caller's image.
        Assert.Equal("not-disposed", Assert.Single(await BarcodeScanner.ReadBarcodesAsync(image)).Text);
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void Default_options_scan_broadly_and_conservatively()
    {
        var defaults = BarcodeOptions.Default;

        Assert.True(defaults.TryHarder);
        Assert.Null(defaults.Formats);          // null = every supported symbology
        Assert.False(defaults.MultipleCodes);   // first hit wins unless asked otherwise
        Assert.False(defaults.TryInverted);
        Assert.Null(defaults.Region);
    }

    // ---------------------------------------------------------------- luminance source

    [Fact]
    public void The_luminance_source_reports_the_image_dimensions()
    {
        using var image = new Image<Rgb24>(37, 19);

        var source = ImageSharpLuminanceSource.Create(image, 0, 0, image.Width, image.Height);

        Assert.Equal(37, source.Width);
        Assert.Equal(19, source.Height);
        Assert.Equal(37 * 19, source.Matrix.Length);
    }

    [Fact]
    public void The_luminance_source_maps_black_and_white_to_the_ends_of_the_range()
    {
        using var image = new Image<Rgb24>(2, 1);
        image[0, 0] = new Rgb24(0, 0, 0);
        image[1, 0] = new Rgb24(255, 255, 255);

        var row = ImageSharpLuminanceSource.Create(image, 0, 0, image.Width, image.Height).getRow(0, null);

        Assert.Equal(0, row[0]);
        Assert.Equal(255, row[1]);
    }

    [Fact]
    public void The_luminance_source_supports_the_transforms_the_scanner_relies_on()
    {
        using var image = new Image<Rgb24>(8, 4);
        var source = ImageSharpLuminanceSource.Create(image, 0, 0, image.Width, image.Height);

        Assert.True(source.CropSupported);
        Assert.True(source.InversionSupported);
        Assert.True(source.RotateSupported);

        var cropped = source.crop(2, 1, 4, 2);
        Assert.Equal(4, cropped.Width);
        Assert.Equal(2, cropped.Height);

        var rotated = source.rotateCounterClockwise();
        Assert.Equal(4, rotated.Width);
        Assert.Equal(8, rotated.Height);
    }
}
