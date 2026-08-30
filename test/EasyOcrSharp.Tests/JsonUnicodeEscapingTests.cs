using System.Text.Encodings.Web;
using System.Text.Json;
using EasyOcrSharp.Export;
using EasyOcrSharp.Models;
using EasyOcrSharp.Structure;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the JSON exporters' character escaping.
/// System.Text.Json escapes everything outside Basic Latin by default, so recognized text in Cyrillic,
/// CJK, Arabic, … used to reach the caller as <c>\uXXXX</c> sequences — valid JSON, but unreadable in a
/// plain text editor. Both <see cref="OcrExportExtensions.ToJson(OcrResult, bool)"/> and
/// <see cref="StructureResult.ToJson()"/> now serialize through <see cref="EasyOcrJson.Encoder"/>, which
/// emits those scripts verbatim while still escaping the HTML-sensitive characters. These tests pin that
/// behaviour, the round-trip equivalence, and the caller-supplied-options overloads.
/// </summary>
public class JsonUnicodeEscapingTests
{
    private const string Cyrillic = "Привет, мир";
    private const string Cjk = "文档识别";
    private const string Arabic = "مرحبا";

    private static OcrResult Result(params string[] lines) => new()
    {
        FullText = string.Join('\n', lines),
        Lines = lines.Select(t => new OcrLine
        {
            Text = t,
            Confidence = 0.97,
            BoundingBox = new OcrBoundingBox(10, 20, 210, 60),
        }).ToArray(),
        Languages = new[] { "ru" },
        SourceWidth = 640,
        SourceHeight = 480,
    };

    private static StructureResult Document(params StructureBlock[] blocks)
        => new() { Blocks = blocks, SourceWidth = 640, SourceHeight = 480 };

    private static StructureBlock TextBlock(string text)
        => new(StructureBlockType.Text, new OcrBoundingBox(0, 0, 600, 40), 0, Text: text);

    /// <summary>The <c>\uXXXX</c> sequence a JSON writer emits when it escapes <paramref name="c"/>.</summary>
    private static string JsonEscape(char c) => @"\u" + ((int)c).ToString("X4");

    // ---- OcrResult.ToJson ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OcrResultToJson_WritesNonAsciiVerbatim(bool indented)
    {
        var json = Result(Cyrillic, Cjk, Arabic).ToJson(indented);

        Assert.Contains(Cyrillic, json);
        Assert.Contains(Cjk, json);
        Assert.Contains(Arabic, json);
        Assert.DoesNotContain(@"\u04", json);   // Cyrillic block
        Assert.DoesNotContain(@"\u06", json);   // Arabic block
    }

    [Fact]
    public void OcrResultToJson_StillEscapesHtmlSensitiveCharacters()
    {
        // Leaving non-ASCII unescaped must not mean UnsafeRelaxedJsonEscaping: recognized markup pasted
        // into a page still has to be inert.
        var json = Result("<script>алерт</script>").ToJson();

        Assert.DoesNotContain("<script>", json);
        Assert.Contains(JsonEscape('<'), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("алерт", json);
    }

    [Fact]
    public void OcrResultToJson_RoundTripsToTheSameText()
    {
        var json = Result(Cyrillic, Cjk).ToJson();

        var back = JsonSerializer.Deserialize(json, EasyOcrJsonContext.Default.OcrResult);

        Assert.NotNull(back);
        Assert.Equal(new[] { Cyrillic, Cjk }, back!.Lines.Select(l => l.Text));
    }

    [Fact]
    public void OcrResultToJson_IndentationStillFollowsTheFlag()
    {
        Assert.DoesNotContain('\n', Result(Cyrillic).ToJson());
        Assert.Contains('\n', Result(Cyrillic).ToJson(indented: true));
    }

    [Fact]
    public void OcrResultToJson_HonoursCallerSuppliedOptions()
    {
        var escaped = Result(Cyrillic).ToJson(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
            WriteIndented = true,
        });

        // Opting back into the stricter encoder is exactly the escape hatch the overload exists for.
        Assert.DoesNotContain(Cyrillic, escaped);
        Assert.Contains(JsonEscape('П'), escaped, StringComparison.OrdinalIgnoreCase);
        Assert.Contains('\n', escaped);
    }

    [Fact]
    public void ToJson_DoesNotMutateOrCaptureCallerOptions()
    {
        // The options are copied, so the caller's instance stays mutable and reusable afterwards.
        var options = new JsonSerializerOptions { WriteIndented = true };

        _ = Result(Cyrillic).ToJson(options);
        _ = Document(TextBlock(Cyrillic)).ToJson(options);

        Assert.Null(options.TypeInfoResolver);
        options.WriteIndented = false;   // throws if an exporter had sealed the instance
        Assert.False(options.WriteIndented);
    }

    [Fact]
    public void ToJson_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Result(Cyrillic).ToJson(null!));
        Assert.Throws<ArgumentNullException>(() => StructureResult.Empty.ToJson(null!));
    }

    // ---- StructureResult.ToJson ----

    [Fact]
    public void StructureResultToJson_WritesNonAsciiVerbatim()
    {
        var doc = Document(
            new StructureBlock(StructureBlockType.DocTitle, new OcrBoundingBox(0, 0, 600, 40), 0, Text: "Отчёт"),
            new StructureBlock(StructureBlockType.Text, new OcrBoundingBox(0, 50, 600, 90), 1, Text: Cyrillic),
            new StructureBlock(StructureBlockType.Formula, new OcrBoundingBox(0, 100, 600, 140), 2, Latex: @"\alpha_{я}"));

        var json = doc.ToJson();

        Assert.Contains("Отчёт", json);
        Assert.Contains(Cyrillic, json);
        Assert.Contains(@"\\alpha_{я}", json);   // JSON-escaped backslash, literal Cyrillic subscript
        Assert.DoesNotContain(@"\u04", json);
    }

    [Fact]
    public void StructureResultToJson_TableHtmlStaysEscaped()
    {
        var doc = Document(new StructureBlock(
            StructureBlockType.Table, new OcrBoundingBox(0, 0, 600, 200), 0,
            TableHtml: "<table><tr><td>Итого</td></tr></table>"));

        var json = doc.ToJson();

        Assert.Contains("Итого", json);
        Assert.DoesNotContain("<table>", json);
    }

    [Fact]
    public void StructureResultToJson_HonoursCallerSuppliedOptions()
    {
        var compact = Document(TextBlock(Cyrillic)).ToJson(new JsonSerializerOptions
        {
            Encoder = EasyOcrJson.Encoder,
            WriteIndented = false,
        });

        Assert.Contains(Cyrillic, compact);
        Assert.DoesNotContain('\n', compact);
    }

    [Fact]
    public void StructureResultToJson_DefaultStaysIndented()
    {
        Assert.Contains('\n', Document(TextBlock(Cyrillic)).ToJson());
    }
}
