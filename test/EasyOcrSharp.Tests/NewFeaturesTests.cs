using System.Text.Json;
using EasyOcrSharp.Export;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

public class ExporterTests
{
    private static OcrResult SampleResult()
    {
        var poly = new OcrPoint[] { new(10, 10), new(110, 10), new(110, 30), new(10, 30) };
        var line = new OcrLine
        {
            Text = "Hello World",
            Confidence = 0.95,
            BoundingPolygon = poly,
            BoundingBox = OcrBoundingBox.FromPoints(poly),
        };
        return new OcrResult
        {
            FullText = "Hello World",
            Lines = new[] { line },
            Languages = new[] { "en" },
            Duration = TimeSpan.FromMilliseconds(12),
        };
    }

    [Fact]
    public void ToJson_RoundTrips()
    {
        var result = SampleResult();
        var json = result.ToJson(indented: true);

        Assert.Contains("Hello World", json);
        var back = JsonSerializer.Deserialize(json, EasyOcrJsonContext.Default.OcrResult);
        Assert.NotNull(back);
        Assert.Equal("Hello World", back!.FullText);
        Assert.Single(back.Lines);
        Assert.Equal(0.95, back.Lines[0].Confidence, 3);
    }

    [Fact]
    public void ToHocr_HasLinesWordsAndBboxes()
    {
        var hocr = SampleResult().ToHocr(pageWidth: 200, pageHeight: 100, imageName: "page.png");
        Assert.Contains("class='ocr_page'", hocr);
        Assert.Contains("class='ocr_line'", hocr);
        Assert.Contains("class='ocrx_word'", hocr);
        Assert.Contains("bbox 0 0 200 100", hocr);
        Assert.Contains("x_wconf 95", hocr);
        Assert.Contains("Hello", hocr);
        Assert.Contains("World", hocr);
    }

    [Fact]
    public void ToAlto_IsWellFormedXmlWithContent()
    {
        var alto = SampleResult().ToAlto(pageWidth: 200, pageHeight: 100);
        Assert.Contains("<alto", alto);
        Assert.Contains("WC=\"0.95\"", alto);
        Assert.Contains("CONTENT=\"Hello\"", alto);
        // Ensure it parses as XML.
        var doc = System.Xml.Linq.XDocument.Parse(alto);
        Assert.NotNull(doc.Root);
    }

    [Fact]
    public void ToTsv_HasHeaderAndWordRows()
    {
        var tsv = SampleResult().ToTsv();
        var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("level\tpage_num", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 words
        Assert.Contains("Hello", lines[1]);
        Assert.Contains("World", lines[2]);
    }

    [Fact]
    public void XmlSpecialCharacters_AreEscaped()
    {
        var poly = new OcrPoint[] { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var result = new OcrResult
        {
            FullText = "a<b>&\"'",
            Lines = new[] { new OcrLine { Text = "a<b>&\"'", Confidence = 0.5, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) } },
            Languages = new[] { "en" },
        };
        var alto = result.ToAlto(20, 20);
        Assert.DoesNotContain("<b>", alto);
        Assert.Contains("&lt;b&gt;", alto);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(alto).Root);
    }
}

public class VisualizationTests
{
    [Fact]
    public void DrawAnnotations_ReturnsNewImage_AndMarksBox()
    {
        using var image = new Image<Rgb24>(40, 40, new Rgb24(255, 255, 255));
        var poly = new OcrPoint[] { new(5, 5), new(30, 5), new(30, 20), new(5, 20) };
        var result = new OcrResult
        {
            FullText = "x",
            Lines = new[] { new OcrLine { Text = "x", Confidence = 1, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) } },
            Languages = new[] { "en" },
        };

        using var annotated = image.DrawAnnotations(result, new Rgb24(255, 0, 0), thickness: 2);

        Assert.NotSame(image, annotated);
        Assert.Equal(image.Width, annotated.Width);
        Assert.Equal(255, image[5, 5].R); // original untouched (still white) ...
        Assert.Equal(255, image[5, 5].G);

        bool foundRed = false;
        for (int x = 0; x < annotated.Width && !foundRed; x++)
            for (int y = 0; y < annotated.Height && !foundRed; y++)
            {
                var p = annotated[x, y];
                if (p is { R: 255, G: 0, B: 0 }) foundRed = true;
            }
        Assert.True(foundRed, "Expected at least one red outline pixel.");
    }
}

public class SearchablePdfBuilderTests
{
    [Fact]
    public void Build_ProducesValidPdf_WithInvisibleText()
    {
        using var page = new Image<Rgb24>(120, 60, new Rgb24(255, 255, 255));
        var poly = new OcrPoint[] { new(10, 10), new(100, 10), new(100, 30), new(10, 30) };
        var ocr = new OcrResult
        {
            FullText = "INVOICE",
            Lines = new[] { new OcrLine { Text = "INVOICE", Confidence = 0.9, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) } },
            Languages = new[] { "en" },
        };

        var builder = new SearchablePdfBuilder();
        builder.AddPage(page, ocr, dpi: 150, jpegQuality: 70);
        builder.AddPage(page, ocr, dpi: 150, jpegQuality: 70);
        var bytes = builder.Build();

        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.7", text);
        Assert.Contains("/Type /Catalog", text);
        Assert.Contains("/Count 2", text);
        Assert.Contains("/Filter /DCTDecode", text);
        Assert.Contains("3 Tr", text);        // invisible text render mode
        Assert.Contains("(INVOICE) Tj", text); // searchable text present
        Assert.EndsWith("%%EOF\n", text);
    }

    [Fact]
    public void PdfOcrOptions_Validate_RejectsBadValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { Dpi = 5 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { JpegQuality = 0 }.Validate());
    }
}

public class OptionsTests
{
    [Fact]
    public void RecognitionOptions_NewProperties_HaveSafeDefaults()
    {
        var o = RecognitionOptions.Default;
        Assert.Null(o.Allowlist);
        Assert.Null(o.Blocklist);
        Assert.NotNull(o.Detection);
        Assert.Equal(0.7, o.Detection.TextThreshold, 3);
        Assert.Equal(2560, o.Detection.CanvasSize);
    }

    [Fact]
    public void EasyOcrServiceOptions_MapsUseGpuToCuda()
    {
        var opts = new EasyOcrServiceOptions { UseGpu = true };
        // ExecutionProvider now defaults to Auto; UseGpu forces CUDA, which is honored at construction.
        Assert.Equal(OcrExecutionProvider.Auto, opts.ExecutionProvider);
        using var service = new EasyOcrService(opts);
        Assert.True(service.UseGpu); // accelerator requested (CUDA; may fall back to CPU at model load)
    }

    [Fact]
    public void ExecutionProviderOptions_DefaultsToAuto()
    {
        var opts = new EasyOcrServiceOptions();
        Assert.Equal(OcrExecutionProvider.Auto, opts.ExecutionProvider);
    }

    [Fact]
    public void AutoExecutionProvider_ResolvesToInstalledRuntime()
    {
        // Auto must select an accelerator only when the loaded ONNX Runtime genuinely reports one for this
        // OS — never inventing a provider whose native code isn't present — and must select one when it is.
        // The base Microsoft.ML.OnnxRuntime package is CPU-only on Windows/Linux, but its macOS build
        // bundles CoreML, so we compare against what the runtime actually offers rather than hardcoding CPU.
        var available = OrtEnv.Instance().GetAvailableProviders();
        string[] osAccelerators =
            OperatingSystem.IsWindows() ? new[] { "DmlExecutionProvider", "CUDAExecutionProvider" } :
            OperatingSystem.IsMacOS()   ? new[] { "CoreMLExecutionProvider" } :
            OperatingSystem.IsLinux()   ? new[] { "CUDAExecutionProvider" } :
            Array.Empty<string>();
        var acceleratorPresent = osAccelerators.Any(p => available.Contains(p));

        using var service = new EasyOcrService(new EasyOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Auto });

        Assert.Equal(acceleratorPresent, service.UseGpu);
    }

    [Fact]
    public void GpuProbe_DoesNotThrow()
    {
        // Detection is advisory: it must never throw, on any platform.
        var vendor = EasyOcrSharp.Internal.GpuProbe.Detect();
        Assert.True(Enum.IsDefined(vendor));
    }

    [Fact]
    public void ExplicitCpu_ProducesNoGpuHint()
    {
        // A deliberate CPU choice must not nag the user, regardless of installed hardware.
        using var service = new EasyOcrService(new EasyOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Cpu });
        Assert.Null(service.GpuAccelerationHint);
    }

    [Fact]
    public void LogGpuHint_IsSilentByDefault()
    {
        // The startup GPU warning is opt-in: off unless the caller explicitly turns it on. The hint is
        // still exposed via the property (covered by AutoGpuHint_WhenPresent_NamesTheGpuPackage).
        Assert.False(new EasyOcrServiceOptions().LogGpuHint);
    }

    [Fact]
    public void AutoGpuHint_WhenPresent_NamesTheGpuPackage()
    {
        // On CI (no GPU) this is null; on a GPU box it must name the concrete, installable package
        // (EasyOcrSharp.Gpu) so the user is never left guessing which one to add. (DirectML support is
        // not yet shipped, so the hint no longer references an EasyOcrSharp.DirectMl package.)
        using var service = new EasyOcrService(new EasyOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Auto });
        var hint = service.GpuAccelerationHint;
        if (hint is not null)
        {
            Assert.False(service.UseGpu); // a hint only appears while running on CPU
            Assert.Contains("EasyOcrSharp.Gpu", hint);
        }
    }
}
