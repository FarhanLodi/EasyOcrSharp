using System.Reflection;
using System.Text.Json;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Structure;
using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Layout;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// <see cref="StructureResult.OrphanLines"/> (mirrors PaddleOcrNet 2.4.0's orphan-line fix): OCR lines the
/// block matching cannot give to exactly one layout block are kept as evidence instead of being dropped.
/// They reach <see cref="StructureResult.ToJson()"/> (only when there are any, so other pages serialize
/// exactly as before) but never the Markdown export.
/// </summary>
[Collection(OcrIntegrationCollection.Name)]
public class StructureOrphanTests
{
    private static OcrLine Line(string text)
    {
        var poly = new[] { new OcrPoint(10, 20), new OcrPoint(100, 20), new OcrPoint(100, 40), new OcrPoint(10, 40) };
        return new OcrLine { Text = text, Confidence = 0.8, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) };
    }

    [Fact]
    public void Orphan_lines_default_to_empty_and_are_omitted_from_json()
    {
        var result = new StructureResult
        {
            Blocks = new[] { new StructureBlock(StructureBlockType.Text, new OcrBoundingBox(0, 0, 50, 10), 0, "Owned") },
            SourceWidth = 200,
            SourceHeight = 100,
        };

        Assert.Empty(result.OrphanLines);
        Assert.Empty(StructureResult.Empty.OrphanLines);
        using var json = JsonDocument.Parse(result.ToJson());
        Assert.False(json.RootElement.TryGetProperty("OrphanLines", out _));
    }

    [Fact]
    public void Orphan_lines_are_serialized_but_not_rendered_to_markdown()
    {
        var result = new StructureResult
        {
            Blocks = new[] { new StructureBlock(StructureBlockType.Text, new OcrBoundingBox(0, 0, 50, 10), 0, "Owned") },
            SourceWidth = 200,
            SourceHeight = 100,
            OrphanLines = new[] { Line("Unowned text") },
        };

        using var json = JsonDocument.Parse(result.ToJson());
        var orphans = json.RootElement.GetProperty("OrphanLines");
        Assert.Equal(1, orphans.GetArrayLength());
        Assert.Equal("Unowned text", orphans[0].GetProperty("Text").GetString());
        Assert.Equal(0.8, orphans[0].GetProperty("Confidence").GetDouble(), 6);
        Assert.DoesNotContain("Unowned text", result.ToMarkdown());

        // The caller-options overload goes through the same DTO.
        using var custom = JsonDocument.Parse(result.ToJson(new JsonSerializerOptions { WriteIndented = false }));
        Assert.Equal(1, custom.RootElement.GetProperty("OrphanLines").GetArrayLength());
    }

    /// <summary>
    /// Model-backed regression (PaddleOcrNet 2.4.0): a layout pass that finds only one vision region over the
    /// whole page used to drop every recognized line, because none of them belongs to a text block. The
    /// readings now survive as orphan lines. The layout detector is swapped for a fake through the engine's
    /// per-model cache; detection and recognition models still run (they download on first use).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Engine_preserves_real_ocr_readings_when_layout_assigns_only_a_vision_region()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "057_receipts_invoices.png");
        Assert.True(File.Exists(path), $"Fixture missing: {path}");
        using var image = await Image.LoadAsync<Rgb24>(path);

        await using var engine = new EasyOcrSharp.Structure.StructureEngine(new StructureEngineOptions(), null);
        var detectors = (Dictionary<LayoutModel, ILayoutDetector>)typeof(EasyOcrSharp.Structure.StructureEngine)
            .GetField("_layoutDetectors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(engine)!;
        lock (detectors) detectors[LayoutModel.RtDetrL] = new VisionOnlyLayout();

        var result = await engine.AnalyzeAsync(image, new StructureOptions
        {
            LayoutModel = LayoutModel.RtDetrL,
            Languages = new[] { OcrLanguage.English },
            RecognizeFormulas = false,
            RecognizeSeals = false,
            RecognizeTables = false,
        }, CancellationToken.None);

        Assert.Equal(StructureBlockType.Figure, Assert.Single(result.Blocks).Type);
        Assert.NotEmpty(result.OrphanLines);
        Assert.Contains(result.OrphanLines, l => l.Text.Length > 3);
        using var json = JsonDocument.Parse(result.ToJson());
        Assert.Equal(result.OrphanLines.Count, json.RootElement.GetProperty("OrphanLines").GetArrayLength());
    }

    private sealed class VisionOnlyLayout : ILayoutDetector
    {
        public IReadOnlyList<LayoutRegion> Detect(Image<Rgb24> image, float scoreThreshold)
            => new[] { new LayoutRegion(StructureBlockType.Figure, new OcrBoundingBox(0, 0, image.Width, image.Height), 1, 0, "image") };

        public void Dispose() { }
    }
}
