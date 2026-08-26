<div align="center">

<img src="https://raw.githubusercontent.com/FarhanLodi/EasyOcrSharp/main/src/EasyOcrSharp/Assets/icon.png" alt="EasyOcrSharp" width="120" height="120">

# EasyOcrSharp

### High-accuracy, fully-offline OCR for .NET — EasyOCR's neural models, running natively on ONNX Runtime. **No Python.**

[![NuGet](https://img.shields.io/nuget/v/EasyOcrSharp.svg?label=NuGet&color=004880&logo=nuget)](https://www.nuget.org/packages/EasyOcrSharp)
[![Downloads](https://img.shields.io/nuget/dt/EasyOcrSharp.svg?label=Downloads&color=success)](https://www.nuget.org/packages/EasyOcrSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/FarhanLodi/EasyOcrSharp/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg?logo=dotnet)](https://dotnet.microsoft.com/)
[![AOT ready](https://img.shields.io/badge/Native%20AOT-ready-6f42c1.svg)](#gpu--execution-providers)

**[Quick start](#-quick-start)** · **[Documents & tables](#-document-structure--tables)** · **[PDF](#-pdf-input--searchable-pdf)** · **[Languages](#-supported-languages)** · **[Production](#-production--operations)**

</div>

---

EasyOcrSharp runs EasyOCR's exact **CRAFT** text detector and **CRNN** recognizers — exported to ONNX and
executed through `Microsoft.ML.OnnxRuntime`. You get **EasyOCR-grade accuracy** in a tiny managed package:
no Python interpreter, no PyTorch, no native OCR binaries, and **nothing ever leaves the machine**.

```csharp
await using var ocr = new EasyOcrService();
var result = await ocr.ExtractTextFromImage("receipt.png", new[] { "en" });
Console.WriteLine(result.FullText);
```

## ✨ Highlights

| | |
|---|---|
| 🌍 **86 languages** | 13 script families: Latin, Cyrillic, Arabic, Devanagari, Bengali, Chinese (Simplified & Traditional), Korean, Japanese, Thai, Tamil, Telugu, Kannada |
| 📦 **~3 MB package** | Models download on demand and cache locally — nothing is bundled |
| 🔒 **Verified & private** | Every model download is SHA256-checked; OCR runs fully offline |
| ⚡ **Fast** | Concurrent multi-region recognition; automatic CUDA GPU with CPU fallback; tunable threads |
| 🧩 **Flexible input** | File / `Stream` / `byte[]` / `Image` / **PDF**, region-of-interest, recognize-from-boxes, word/line/paragraph grouping, auto language detection |
| 🧱 **Document structure** | **`AnalyzeDocumentAsync`**: layout regions, **tables as HTML**, formulas, seals & reading order (PP-StructureV3, built in), with Markdown / JSON export |
| 📄 **Document-ready** | **Searchable-PDF** output, plus hOCR / ALTO / TSV / JSON exporters |
| ✍️ **Handwriting** | **TrOCR** encoder/decoder recognition for handwritten text — something Python EasyOCR cannot do at all |
| 🔳 **Barcodes & QR** | Read barcodes and QR codes alongside text in a single pass |
| 🖍️ **Redaction** | Find by regex/keyword and **permanently** paint over it — Luhn-checked cards, mod-97 IBANs, emails, SSNs |
| 🧾 **Fields & tables** | Anchor-based **key/value extraction** with invoice presets, plus recovered tables as `DataTable` / CSV |
| 🎯 **Accurate fields** | Allow/block-lists, **beam / word-beam decoders**, per-box **rotation**, custom recognizers, exposed detection/grouping/contrast thresholds, **post-OCR correction** & CER/WER metrics |
| 📐 **Word geometry** | True per-**word** and per-**character** boxes from CTC alignment — not width estimates |
| 🖥️ **CLI + web sample** | `dotnet tool install -g EasyOcrSharp.Cli`, plus a Dockerized ASP.NET Core service sample |
| 🩺 **Scan-ready** | Deskew, orientation correction, adaptive binarize, denoise & **sharpen**, plus model-based **document orientation** & **page unwarp** |
| 📊 **Production-grade** | OpenTelemetry metrics & tracing, health checks, resilient resumable downloads, batch API |
| 🛠️ **Modern .NET** | AOT- & single-file-friendly, DI-ready, .NET 10 |
| 🖼️ **MIT all the way down** | Imaging runs on [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp) and the structure engine is built in — no build-time licence key, no commercial tier, no split-licensed package anywhere in the graph |

## 🆚 Why EasyOcrSharp?

|  | **EasyOcrSharp** | Python EasyOCR | Cloud OCR APIs | Tesseract (.NET wrappers) |
|---|:---:|:---:|:---:|:---:|
| **Runtime** | Pure .NET + ONNX | Python + PyTorch | Remote HTTP service | Native binary via P/Invoke |
| **Install** | `dotnet add package` | pip + CUDA toolchain | API key + billing | Native libs + tessdata files |
| **Privacy** | 🟢 100% offline | 🟢 offline | 🔴 data leaves the machine | 🟢 offline |
| **Accuracy** | 🟢 EasyOCR neural models | 🟢 EasyOCR neural models | 🟢 high | 🟡 classical (weaker on hard text) |
| **Tables / layout** | 🟢 built-in (PP-Structure) | 🟡 add-ons | 🟢 yes | 🔴 no |
| **PDF in & searchable out** | 🟢 built-in | 🔴 DIY | 🟢 yes | 🔴 DIY |
| **GPU** | 🟢 CUDA (opt-in package) | 🟢 CUDA | n/a | 🔴 no |
| **Native AOT / trimming** | 🟢 yes | n/a | n/a | 🟡 limited |
| **Cost** | 🟢 free (MIT) | 🟢 free | 🔴 per-call | 🟢 free |

> Same models as Python EasyOCR, none of the Python. Fully local, so no per-page cost and no data egress.

---

## 📥 Installation

```bash
dotnet add package EasyOcrSharp
```

PDF input and searchable-PDF output are **built in** — no extra package. For NVIDIA GPU acceleration
(Windows/Linux x64, CUDA 12+):

```bash
dotnet add package EasyOcrSharp.Gpu
```

> **Upgrading from 1.x?** v2 replaced the ~1.5 GB embedded Python + PyTorch runtime with native ONNX.
> The public API (`EasyOcrService`, `OcrResult`, `OcrLine`, `OcrBoundingBox`) is unchanged.

> **Upgrading from 2.x?** v3 changes the imaging library behind the pixel types on the public API —
> see [Imaging](#imaging) below and the [changelog](CHANGELOG.md). Method names, parameters and results
> are unchanged; what changes is the namespace the `Image<Rgb24>` in your `using` directives comes from.

### Imaging

Decoding, encoding and every pixel operation run on
**[EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp)** — MIT-licensed, fully managed, AOT- and
trimming-friendly, with no build-time licence key and no commercial tier for consumers to inherit. It is
maintained by the same author as this library, so a fix OCR needs does not wait on a third party.

It brings PNG, JPEG (baseline, progressive and CMYK), WebP (decode), GIF, BMP, TIFF (including CCITT G3/G4
and JPEG-in-TIFF), TGA, Netpbm, QOI and ICO, so `Image.Load` accepts anything you are likely to scan or be
sent. Pixel types (`Image<Rgb24>`, `Rgba32`, ...), geometry (`Point`, `Size`, `Rectangle`) and the
`Mutate` / `Clone` processing pipeline live in the `EasyImageSharp`, `EasyImageSharp.PixelFormats` and
`EasyImageSharp.Processing` namespaces:

```csharp
using EasyImageSharp;                 // Image, Image.Load, Color, Rectangle
using EasyImageSharp.PixelFormats;    // Rgb24, Rgba32, L8
using EasyImageSharp.Processing;      // Mutate/Clone: Resize, Rotate, Crop, Grayscale, Deskew, ...

using var img = Image.Load<Rgb24>("page.png");
var result = await ocr.ExtractTextFromImage(img, new[] { "en" });
```

## 🚀 Quick start

```csharp
using EasyOcrSharp.Services;

await using var ocr = new EasyOcrService();

var result = await ocr.ExtractTextFromImage("sample.png", new[] { "en" });

Console.WriteLine(result.FullText);

foreach (var line in result.Lines)
    Console.WriteLine($"{line.Text}  (confidence {line.Confidence:P0})");
```

The first call for a language downloads its model (cached afterwards). Detected text is returned as
**reading-order lines** (top-to-bottom), matching EasyOCR's `readtext()`.

## 📦 The result model

```csharp
public sealed record OcrResult
{
    public string FullText { get; }                 // all lines joined by newlines (reading order)
    public IReadOnlyList<OcrLine> Lines { get; }
    public IReadOnlyList<string> Languages { get; }
    public TimeSpan Duration { get; }
    public bool UsedGpu { get; }
    public int SourceWidth { get; }                 // dimensions OCR ran on (0 if unknown) — handy for exporters
    public int SourceHeight { get; }
}

public sealed record OcrLine
{
    public string Text { get; }
    public double Confidence { get; }                       // 0..1
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; } // 4 corners
    public OcrBoundingBox BoundingBox { get; }              // MinX/MinY/MaxX/MaxY + Width/Height/Center
}
```

<br>

# 🧭 Core OCR

### Input sources

OCR from a file path, a `Stream`, raw encoded bytes, or an already-decoded `EasyImageSharp` image:

```csharp
await ocr.ExtractTextFromImage("photo.jpg",            new[] { "en" });
await ocr.ExtractTextFromImage(stream,                  new[] { "en" });
await ocr.ExtractTextFromImage(File.ReadAllBytes("p"),  new[] { "en" });   // byte[]
await ocr.ExtractTextFromImage(memory,                  new[] { "en" });   // ReadOnlyMemory<byte>
await ocr.ExtractTextFromImage(image,                   new[] { "en" });   // Image<Rgb24> (caller-owned)
```

All overloads accept an optional `RecognitionOptions` and a `CancellationToken`.

### Recognition options

```csharp
var options = new RecognitionOptions
{
    Grouping = TextGrouping.Line,    // Word | Line (default) | Paragraph
    MinConfidence = 0.3,             // drop results below this confidence
    MaxDegreeOfParallelism = 8,      // regions recognized concurrently (default: CPU count)
    AdjustContrast = true,           // low-confidence contrast-retry pass (EasyOCR's 2nd pass)
    Region = null,                   // optional region of interest (see below)
};

var result = await ocr.ExtractTextFromImage("doc.png", new[] { "en" }, options);
```

| Grouping | Behaviour |
|---|---|
| `Word` | One result per detected box (≈ per word) |
| `Line` | Adjacent boxes merged into lines (default; matches EasyOCR) |
| `Paragraph` | Nearby lines merged into paragraph blocks |

### Region of interest

Restrict OCR to a rectangle — ideal for a fixed field (price, license plate, banner) and faster than
scanning the whole image. Boxes are always reported in the **original image's** coordinates.

```csharp
// Absolute pixels:
var roi = new RecognitionOptions { Region = OcrRegion.Pixels(x: 40, y: 320, width: 500, height: 80) };

// Resolution-independent fractions — e.g. the bottom third:
var bottom = new RecognitionOptions { Region = OcrRegion.Fraction(0, 0.66, 1, 0.34) };

var result = await ocr.ExtractTextFromImage("receipt.png", new[] { "en" }, bottom);
```

### Multiple languages

Pass several codes for mixed-script images — each region is read by every requested script pack and
the highest-confidence result wins:

```csharp
var result = await ocr.ExtractTextFromImage("street_sign.png", new[] { "en", "ch_sim", "ru" });
```

Each additional script family loads its own model, so request only the scripts you expect.

### Automatic language detection

Don't know the language? Let the engine detect it — pass no codes and set `AutoDetectLanguage`:

```csharp
var result = await ocr.ExtractTextFromImage("unknown.png", Array.Empty<string>(),
    new RecognitionOptions { AutoDetectLanguage = true });

// Or just detect, without recognizing:
IReadOnlyList<string> langs = await ocr.DetectLanguagesAsync("unknown.png");
```

Detection samples the largest text regions and scores candidate script packs by confidence.
Candidates default to a common set (Latin, Cyrillic, Chinese, Japanese, Korean); widen them when you
expect heavier scripts:

```csharp
var opts = new RecognitionOptions
{
    AutoDetectLanguage = true,
    AutoDetectCandidates = new[] { "en", "ar", "hi", "ch_sim" },
};
```

### Word & character geometry

Each line normally carries one polygon. Ask for `WordLevelDetail` and every line also reports its
**words** and, optionally, its **characters** — with real boxes derived from the recognizer's CTC
alignment (which timestep emitted which glyph), not estimated by splitting the line width.

```csharp
var result = await ocr.ExtractTextFromImage("receipt.png", new[] { "en" }, new RecognitionOptions
{
    WordLevelDetail = WordLevelDetail.Words,   // or .Characters for per-glyph boxes
});

foreach (var word in result.Lines.SelectMany(l => l.Words))
    Console.WriteLine($"{word.Text,-20} {word.Confidence:P0}  {word.BoundingBox}");
```

Defaults to `WordLevelDetail.None`, so nothing changes — and costs nothing — unless you ask.
Rotated lines produce genuinely rotated word quads, and the hOCR / ALTO / TSV exporters and the
searchable-PDF text layer all sharpen automatically when word detail is present.

### Streaming results

For multi-page documents or a responsive UI, take lines as they are recognized instead of waiting for
the whole page:

```csharp
await foreach (var line in ocr.ExtractTextStreamAsync("poster.png", new[] { "en" }))
    Console.WriteLine(line.Text);   // arrives as each region finishes
```

### ✍️ Handwriting (TrOCR)

Handwritten text needs a different model than printed text — EasyOCR's CRNN recognizers are trained on
printed glyphs and cannot read cursive at all. Switch it on and call `RecognizeHandwritingAsync`:

```csharp
var service = new EasyOcrService(new EasyOcrServiceOptions
{
    Handwriting = HandwritingOptions.Default,   // that's it
});

var notes = await service.RecognizeHandwritingAsync("handwritten-note.png");
```

The TrOCR models download into the same model cache as everything else on the first handwriting call,
checksum-verified like the rest. `Handwriting` is **null by default**, so a service that doesn't ask
for it never downloads a byte and behaves exactly as before.

```csharp
Handwriting = new HandwritingOptions
{
    Quantize  = false,   // full precision (~1.5 GB) instead of the int8 default (~520 MB)
    BeamWidth = 4,       // 1 = greedy (default)
},
```

`Quantize` defaults to **true**: int8 weights are a third of the size and roughly twice as fast, at a
small accuracy cost on unusual words. Full precision is the more accurate of the two — worth the extra
download for archival work.

The hosted weights are an ONNX export of Microsoft's MIT-licensed
[`trocr-base-handwritten`](https://huggingface.co/microsoft/trocr-base-handwritten), produced by
`tools/export_trocr_onnx.py`.

**Using your own export instead.** Any standard [Optimum](https://huggingface.co/docs/optimum) TrOCR
export works — an encoder taking `pixel_values`, a decoder taking `input_ids` + `encoder_hidden_states`
(with or without `past_key_values` caching), and a byte-level BPE vocabulary — so you can swap in
`trocr-base-printed`, a larger checkpoint, or your own fine-tune. Point the three paths at it and
nothing is ever downloaded:

```csharp
// Explicit paths…
Handwriting = new HandwritingOptions
{
    EncoderModelPath = "models/trocr/encoder_model.onnx",
    DecoderModelPath = "models/trocr/decoder_model.onnx",
    TokenizerPath    = "models/trocr/vocab.json",
};

// …or a folder holding those three conventional file names.
Handwriting = HandwritingOptions.FromDirectory("models/trocr"),
```

Setting only some paths is fine: whatever you leave null is fetched from the hosted set, so you can
override just the decoder and keep the rest.

For long lines you can point `DecoderModelPath` at a `decoder_model_merged.onnx` — the runtime detects
the `past_key_values` inputs and uses KV caching automatically. Do **not** use
`decoder_with_past_model.onnx` on its own; it is the second half of a two-file pipeline and consumes
caches it never produces.

Note the checkpoint is fine-tuned on **handwriting**: it reads printed text too, but the dedicated
printed recognizers are better at that.

### 🔳 Barcodes & QR codes

Documents that need OCR usually carry codes too. Read them from the same image, with no OCR model
involved:

```csharp
foreach (var code in await BarcodeScanner.ReadBarcodesAsync("label.png",
             new BarcodeOptions { MultipleCodes = true }))
{
    Console.WriteLine($"{code.Format}: {code.Text}");
}

// …or both in one pass
var page = await ocr.ExtractTextAndBarcodesAsync("label.png", new[] { "en" });
Console.WriteLine($"{page.Ocr.Lines.Count} lines, {page.Barcodes.Count} codes");
```

`BarcodeOptions` covers `Formats`, `TryHarder`, `MultipleCodes`, `TryInverted`, `AutoRotate` and a
`Region` restriction.

<br>

# 📄 Documents, scans & PDF

### Scanned-document preprocessing

For photos and scans, enable clean-up via `RecognitionOptions.Preprocessing`:

```csharp
var opts = new RecognitionOptions
{
    Preprocessing = new PreprocessingOptions
    {
        Deskew = true,            // straighten small tilt (±15°)
        DetectOrientation = true, // fix 90°/180°/270° rotation (≈4× cost)
        Binarize = true,          // adaptive black/white for uneven lighting
        Denoise = true,           // suppress scanner speckle
        Sharpen = true,           // unsharp-mask for soft / low-DPI scans (SharpenAmount tunes strength)
    },
};
var result = await ocr.ExtractTextFromImage("scan.jpg", new[] { "en" }, opts);
```

Two **model-based document steps** are also available — small dedicated neural models that download
on first use (SHA256-verified like every other model):

```csharp
var docOpts = new RecognitionOptions
{
    Preprocessing = new PreprocessingOptions
    {
        DocumentOrientation = true, // PP-LCNet doc classifier: fixes 90°/180°/270° in ONE tiny model
                                    // pass — much cheaper than DetectOrientation's 4× OCR
        DocumentUnwarp = true,      // UVDoc: dewarps curved/folded pages (photographed book pages,
                                    // creased receipts) before OCR
    },
};
```

> **Coordinate spaces.** `DetectOrientation` reads the page at whichever 90°/180°/270° rotation scores
> best, then maps the boxes **back onto your original image** — results stay anchored to the input you
> passed and agree with the reported `SourceWidth`/`SourceHeight`. The model-based `DocumentOrientation`
> / `DocumentUnwarp` steps instead hand the whole pipeline a corrected page, so *their* boxes (and
> `SourceWidth`/`SourceHeight`) are in that corrected image's coordinate space.

### 🧱 Document structure & tables

Beyond plain text OCR, `AnalyzeDocumentAsync` recovers a page's **structure** — layout regions,
**tables (as HTML)**, formulas (as LaTeX), seals/stamps, and reading order — powered by PaddleOCR's
PP-StructureV3 models, running on an engine built into this package (no extra dependency; the models
download on first use):

```csharp
using var ocr = new EasyOcrService();

var doc = await ocr.AnalyzeDocumentAsync("report_page.png");

foreach (var block in doc.Blocks)                    // in reading order
{
    Console.WriteLine($"{block.Order}: {block.Type} @ {block.Bounds}");
    if (block.TableHtml is not null)                 // tables come back as structured HTML
        Console.WriteLine(block.TableHtml);
}

string markdown = doc.ToMarkdown();                  // whole page as Markdown (tables included)
string json = doc.ToJson();
```

Tune what runs with `DocumentAnalysisOptions` (all models download on demand, SHA256-verified):

```csharp
var doc = await ocr.AnalyzeDocumentAsync("scan.jpg", new DocumentAnalysisOptions
{
    DocumentOrientation = true,             // upright a rotated page first
    DocumentUnwarp = true,                  // dewarp a curved/folded page first
    RecognizeTables = true,                 // table structure as HTML (default on)
    RecognizeFormulas = false,              // skip LaTeX formula recognition
    RecognizeSeals = false,                 // skip seal/stamp recognition
    TableModel = DocumentTableModel.SlaNeXt,// higher-accuracy table model (default: SlanetPlus)
    Languages = new[] { "en" },             // text language(s); default pack covers ch/en/ja
});
```

The analyzer shares the service's execution provider, thread limits, cache path (when set) and
download-resilience settings, loads lazily on first use, and is disposed with the service. Regular
OCR calls never touch it.

### 📑 PDF input & searchable PDF

OCR scanned PDFs and emit searchable PDFs — **built into the main package** (no extra install needed).
Pages are rasterized with PDFium and processed one at a time, so memory stays low even on large documents.

```csharp
using EasyOcrSharp.Pdf;

await using var ocr = new EasyOcrService();

// 1) Extract text from every page:
PdfOcrResult doc = await ocr.ExtractTextFromPdfAsync("scan.pdf", new[] { "en" });
Console.WriteLine(doc.FullText);
foreach (var page in doc.Pages)
    Console.WriteLine($"Page {page.PageNumber}: {page.Ocr.Lines.Count} lines");

// 2) Produce a searchable PDF (original pages + invisible, selectable text layer):
await ocr.CreateSearchablePdfAsync("scan.pdf", "scan.searchable.pdf", new[] { "en" },
    pdfOptions: new PdfOcrOptions { Dpi = 250, JpegQuality = 80 });
```

`PdfOcrOptions` controls render `Dpi`, searchable-PDF `JpegQuality`, and a per-page `Progress`
callback.

#### Unicode text layers

The invisible text layer uses the base-14 Helvetica font for Latin-1 text, and automatically switches
to an **embedded, subsetted Type0 / `Identity-H` font** (with a `ToUnicode` CMap) when the recognized
text needs it — so Chinese, Japanese, Korean, Arabic, Devanagari, Thai and Greek PDFs are genuinely
searchable and copy-pasteable.

```csharp
var (result, pdf) = await ocr.CreateSearchablePdfAsync(bytes, new[] { "ch_sim" }, pdfOptions: new()
{
    TextLayerFont     = PdfTextLayerFontMode.Auto,   // Auto | Never | Always
    TextLayerFontPath = "/usr/share/fonts/noto/NotoSansCJK-Regular.ttc",  // optional
});

if (result.TextLayerFontStatus == PdfTextLayerFontStatus.Unavailable)
    logger.LogWarning("No font covered this script — the text layer fell back to Latin-1.");
```

> **No font is bundled** — a CJK font alone is tens of megabytes. Either point `TextLayerFontPath` at
> one, or let the built-in probe find an installed system font. If nothing suitable exists the output
> falls back to the old Helvetica layer rather than failing, and `TextLayerFontStatus` tells you so.

### 🖼️ Multi-frame TIFF

Scanners emit multi-page TIFFs. Read every frame, not just the first:

```csharp
var doc = await ocr.ExtractTextFromFramesAsync("scan.tif", new[] { "en" });
Console.WriteLine($"{doc.Frames.Count} frames in {doc.Duration.TotalSeconds:0.0}s");

// or stream them, so a 200-page TIFF starts producing results immediately
await foreach (var frame in ocr.StreamTextFromFramesAsync("scan.tif", new[] { "en" }))
    Console.WriteLine($"page {frame.FrameIndex}: {frame.Ocr.FullText}");
```

The pixel-flood guard applies per frame, `MaxFrames` bounds the document, and a single-frame image
flows through the same call and returns exactly one result.

### 📊 Tables as data

`AnalyzeDocumentAsync` recovers tables as HTML. Turn them into something .NET can use:

```csharp
var structure = await ocr.AnalyzeDocumentAsync("invoice.png");

foreach (var table in structure.Tables())
{
    IReadOnlyList<IReadOnlyList<string>> rows = table.ToRows();
    DataTable dt = table.ToDataTable();      // header row detected from <th>
    string csv  = table.ToCsv();             // RFC 4180 quoting
}
```

Merged cells are expanded into repeated values, HTML entities are decoded, and malformed markup is
tolerated rather than thrown on.

<br>

# 🔌 Output & integration

### Output formats (hOCR / ALTO / TSV / JSON)

Any `OcrResult` converts to the interchange formats DMS and archival pipelines expect:

```csharp
using EasyOcrSharp.Export;

using var img = Image.Load<Rgb24>("page.png");
var result = await ocr.ExtractTextFromImage(img, new[] { "en" });

string hocr = result.ToHocr(pageWidth: img.Width, pageHeight: img.Height); // hOCR (HTML)
string alto = result.ToAlto(pageWidth: img.Width, pageHeight: img.Height); // ALTO XML v4
string tsv  = result.ToTsv();                                              // Tesseract-style TSV
string json = result.ToJson(indented: true);                              // AOT-safe JSON
```

`ToJson` uses a source-generated `EasyOcrJsonContext`, so it works in trimmed / Native-AOT apps with
no reflection warnings.

### Recognize from known boxes

If you already have regions — from `DetectRegionsAsync`, a previous run, or your own layout analysis —
recognize them directly and skip detection (EasyOCR's `recognize()`):

```csharp
using var image = Image.Load<Rgb24>("form.png");

// e.g. reuse a detection pass, or pass your own polygons (pixel coordinates):
IReadOnlyList<DetectedRegion> regions = await ocr.DetectRegionsAsync(image);

OcrResult result = await ocr.RecognizeRegionsAsync(image, regions, new[] { "en" });
```

There's also an overload taking raw polygons (`IEnumerable<IReadOnlyList<OcrPoint>>`).

### Detection-only & visualization

Locate text regions **without** recognizing them — fast and language-independent, ideal for layout
analysis, redaction, or cropping fields before a targeted recognition pass:

```csharp
IReadOnlyList<DetectedRegion> regions = await ocr.DetectRegionsAsync("form.png");
```

Draw the boxes onto a copy of the image for debugging (no extra dependency; original is untouched):

```csharp
using EasyOcrSharp.Export;

using var img = Image.Load<Rgb24>("page.png");
var result = await ocr.ExtractTextFromImage(img, new[] { "en" });
using var annotated = img.DrawAnnotations(result, new Rgb24(255, 0, 0), thickness: 2);
await annotated.SaveAsync("page.annotated.png");
```

### Batch processing

Process a folder or queue with bounded concurrency. Results stream as they complete; a failed image
is captured (not thrown), so one bad file never aborts the batch:

```csharp
var files = Directory.EnumerateFiles("inbox", "*.png");

await foreach (var item in ocr.ExtractTextFromImagesAsync(files, new[] { "en" }, maxConcurrency: 4))
{
    if (item.Succeeded) Console.WriteLine($"{item.Source}: {item.Result!.Lines.Count} lines");
    else                Console.Error.WriteLine($"{item.Source} failed: {item.Error!.Message}");
}
```

### 🖍️ Redaction

Find sensitive text and **permanently** destroy those pixels — the region is painted over, not covered
by an annotation someone can remove:

```csharp
var redacted = await ocr.RedactAsync("statement.png", new[] { "en" }, new RedactionOptions
{
    Rules    = RedactionPatterns.Common,     // email, phone, card, IBAN, SSN, long digit runs
    Keywords = new[] { "Account Holder" },
    Style    = RedactionStyle.FilledBox,     // or Blur / Pixelate
    Scope    = RedactionScope.MatchedWords,  // only the matched words, not the whole line
});

await redacted.Image.SaveAsync("statement.redacted.png");
Console.WriteLine($"{redacted.RedactedRegionCount} regions removed");
Console.WriteLine(redacted.SanitizedText);   // the text with matches masked out

// PDFs too
var safe = await ocr.RedactPdfAsync(pdfBytes, new[] { "en" }, options);
```

The card and IBAN presets are **validated, not just matched**: `CreditCard` applies a Luhn check and
`Iban` a mod-97 check, so a random 16-digit order number is not mistaken for a card number.

### 🧾 Field extraction

Pull structured values out of invoices, receipts and forms using the label positions OCR already
produced — no LLM involved:

```csharp
var result = await ocr.ExtractTextFromImage("invoice.png", new[] { "en" });

var fields = result.ExtractFields(new[]
{
    FieldPresets.InvoiceNumber,
    FieldPresets.InvoiceDate,
    FieldPresets.Total,
    new FieldDefinition
    {
        Name      = "Customer PO",
        Anchors   = new[] { "Customer PO", "PO Number" },
        Direction = FieldDirection.Right | FieldDirection.Below,
    },
});

foreach (var f in fields)
    Console.WriteLine($"{f.Name}: {f.Value}  ({f.Confidence:P0})");
```

Anchor matching is fuzzy, so OCR damage like `Totai` still resolves; distances are expressed as
multiples of the anchor's line height, so the same definition works at any resolution; and a plain
`"Total: 42.00"` on one line is extracted without any geometry at all.

<br>

# 🎯 Accuracy & tuning

### Constrained fields (allow/block-lists & detection thresholds)

For fixed-format fields, restrict the character set — this sharply cuts errors:

```csharp
// Digits only (invoice totals, IDs, meter readings):
var digits = new RecognitionOptions { Allowlist = "0123456789.," };

// License plate (upper-case + digits):
var plate = new RecognitionOptions { Allowlist = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-" };

var total = await ocr.ExtractTextFromImage("receipt.png", new[] { "en" }, digits);
```

Use `Blocklist` to forbid specific characters instead. For hard inputs, the CRAFT detector thresholds
are exposed via `RecognitionOptions.Detection` (defaults match EasyOCR):

```csharp
var opts = new RecognitionOptions
{
    Detection = new DetectionOptions
    {
        TextThreshold = 0.6,  // lower → catch fainter text
        LowText = 0.3,        // lower → keep more of each glyph
        MagRatio = 1.5,       // enlarge before detection (small text)
    },
};
```

### Decoders, rotation & batching

Switch the CTC decoder, recognize rotated text, or batch boxes through the model — all via
`RecognitionOptions` (every option defaults to the previous behaviour):

```csharp
var opts = new RecognitionOptions
{
    Decoder = DecoderType.BeamSearch,  // Greedy (default) | BeamSearch | WordBeamSearch
    BeamWidth = 10,                    // explored hypotheses (beam decoders)
    RotationInfo = new[] { 90, 270 },  // also try each box rotated; keep the best reading
    BatchSize = 16,                    // batch boxes through one ONNX run (see note below)
};

var result = await ocr.ExtractTextFromImage("rotated_labels.png", new[] { "en" }, opts);
```

> **`BatchSize` note.** Batching needs a batch-capable recognizer export; the currently hosted models
> are exported with batch fixed at 1, so `BatchSize > 1` transparently **falls back to per-box**
> inference today (results are unaffected). Per-box recognition already runs concurrently — tune
> throughput with `MaxDegreeOfParallelism`.

`WordBeamSearch` constrains output to a lexicon you supply, which is powerful for closed vocabularies
(part numbers, place names, a product catalogue):

```csharp
var opts = new RecognitionOptions
{
    Decoder = DecoderType.WordBeamSearch,
    Dictionary = new[] { "INVOICE", "TOTAL", "SUBTOTAL", "TAX" },
};
```

### Custom recognizers

Register your own exported CRNN ONNX model (e.g. a fine-tuned EasyOCR `recog_network`) for chosen
language codes. A custom recognizer takes precedence over the built-in pack and is loaded straight
from disk — never downloaded:

```csharp
var options = new EasyOcrServiceOptions();
options.CustomRecognizers.Add(new CustomRecognizer
{
    Name = "my_meter_reader",
    ModelPath = @"D:\models\meter_g2.onnx",
    VocabPath = @"D:\models\meter_g2.vocab.json", // or set Characters = "0123456789." inline
    Languages = new[] { "en" },                    // claim the codes it should handle
});

await using var ocr = new EasyOcrService(options);
```

### Fine-tuning grouping & contrast

The thresholds that merge boxes into lines/paragraphs and trigger the contrast-retry pass are exposed
for difficult layouts (defaults reproduce EasyOCR's behaviour):

```csharp
var opts = new RecognitionOptions
{
    GroupingOptions = new GroupingOptions
    {
        SlopeThreshold = 0.1,        // tolerate gently tilted lines (slope_ths)
        YCenterThreshold = 0.5,      // vertical tolerance for same-line boxes (ycenter_ths)
        WidthThreshold = 1.0,        // max horizontal gap to merge on a line (width_ths)
        ParagraphYThreshold = 1.0,   // vertical reach when forming paragraphs (y_ths)
    },
    ContrastThreshold = 0.1,         // re-recognize below this confidence (contrast_ths)
    AdjustContrastTarget = 0.5,      // grey-stretch target for the retry pass (adjust_contrast)
};
```

### Quantized models

Set `Quantize` to fetch the int8-quantized recognizers instead of the float ones — EasyOCR's
`quantize=True`, for smaller downloads:

```csharp
await using var ocr = new EasyOcrService(new EasyOcrServiceOptions { Quantize = true });
```

The int8 variants are hosted alongside the float models and SHA256-verified on download, and text
output is effectively unchanged. The win is **vocabulary-dependent**: ONNX Runtime (CPU) int8-quantizes
the matmul/linear layers but not the BiLSTM/convolutions, so large-vocabulary packs shrink most
(e.g. `zh_sim` ~22 → ~16 MB) while small-vocabulary packs change little. The detector stays float (as
in EasyOCR). Opt-in; the float models are the default.

### Post-OCR correction

Fix the recognizer's mistakes with a domain lexicon — while leaving text it was confident about
completely alone:

```csharp
var corrected = result.Correct(new CorrectionOptions
{
    Dictionary             = File.ReadLines("part-numbers.txt").ToArray(),
    MaxEditDistance        = 2,
    MinConfidenceToCorrect = 0.85,   // only touch tokens the model itself flagged as shaky
    Normalizers            = new[] { FieldNormalizers.Iban(), FieldNormalizers.Date() },
});
```

Candidate ranking is weighted by the confusions OCR actually makes (`0`/`O`, `1`/`l`/`I`, `5`/`S`,
`8`/`B`, `rn`/`m`). The normalizers go further than validation: where a checksum identifies the wrong
character — IBAN mod-97, ICAO 9303 MRZ check digits — they repair it. `Correct` never mutates the
input; it returns a new `OcrResult`.

### Measuring accuracy (CER / WER)

```csharp
double cer = result.CharacterErrorRate(expectedText);
double wer = result.WordErrorRate(expectedText);

var report = result.Compare(expectedText, TextComparisonOptions.Relaxed);
Console.WriteLine($"CER {report.CharacterErrorRate:P2} — " +
                  $"{report.Characters.Substitutions} sub, " +
                  $"{report.Characters.Insertions} ins, " +
                  $"{report.Characters.Deletions} del");
```

Useful for benchmarking a preprocessing change or a decoder setting against your own documents rather
than trusting a generic accuracy claim.

<br>

# 📊 Production & operations

### GPU & execution providers

**GPU is automatic.** `ExecutionProvider` defaults to `Auto`: on the first run EasyOcrSharp asks ONNX
Runtime what accelerators are actually present and uses the best one, falling back to CPU when there's
none. You don't pick a provider — you just install the package for the hardware you have:

| Install this package | What `Auto` enables |
|---|---|
| `EasyOcrSharp` (base) | CPU only |
| `EasyOcrSharp.Gpu` | NVIDIA CUDA (needs CUDA 12+ on PATH) |

> Why a separate package? The ONNX Runtime variants ship the *same* `onnxruntime.dll` compiled with
> different providers, so only one can be referenced at a time — and the CUDA build is several hundred
> MB. Shipping it as an opt-in package keeps the base library small and cross-platform; `Auto` then
> lights up whatever you installed.

```csharp
// Nothing to configure — add the EasyOcrSharp.Gpu package and a GPU is used if present.
await using var ocr = new EasyOcrService();
```

You can still pin a provider explicitly (e.g. to force CPU, or to require CUDA) and set ONNX Runtime
thread limits:

```csharp
await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
{
    ExecutionProvider = OcrExecutionProvider.Cuda, // Auto (default) | Cpu | Cuda | CoreMl
    IntraOpNumThreads = 4,   // cap CPU use in multi-tenant servers (null = runtime default)
});
```

| Provider | Package | Notes |
|---|---|---|
| `Auto` | (any) | **Default.** Probes the runtime; uses the best installed accelerator, else CPU |
| `Cpu` | (built-in) | Always available |
| `Cuda` | `EasyOcrSharp.Gpu` | NVIDIA, CUDA 12+ on PATH |
| `CoreMl` | CoreML-enabled ORT build | macOS / Apple Silicon |

Any non-CPU provider **falls back to CPU automatically** (with a logged warning) if its runtime is
missing or the device fails to initialize — your app keeps working. The legacy `useGpu: true` flag
still works and forces CUDA. Check `ocr.UseGpu` to see whether an accelerator was selected.

**GPU upgrade hint.** When `Auto` runs on CPU but a real NVIDIA GPU is physically present, EasyOcrSharp
detects it and can tell you the *exact* package to add — `EasyOcrSharp.Gpu`. It's **silent by default**:
the hint is exposed as a property you can surface yourself, and nothing is logged unless you opt in with
`LogGpuHint = true`.

```csharp
// Silent by default — read it only if you want to nudge the user yourself:
await using var ocr = new EasyOcrService();
if (ocr.GpuAccelerationHint is { } hint) Console.WriteLine(hint);
// e.g. "EasyOcrSharp: an NVIDIA GPU was detected but OCR is running on CPU. Install the
//       'EasyOcrSharp.Gpu' NuGet package for CUDA acceleration. ..."

// Opt in to a one-time startup warning in the logs instead:
await using var verbose = new EasyOcrService(new EasyOcrServiceOptions { LogGpuHint = true });
```

### Observability & health checks

EasyOcrSharp emits OpenTelemetry-ready **metrics** and **traces**, always-on with near-zero cost when
nobody is listening:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(EasyOcrDiagnostics.MeterName))   // operations, duration, lines, model loads/bytes
    .WithTracing(t => t.AddSource(EasyOcrDiagnostics.ActivitySourceName));
```

Add a readiness probe that reports whether the models for your languages are cached (so the first real
request won't block on a download):

```csharp
builder.Services.AddHealthChecks()
    .AddEasyOcrHealthCheck(languages: new[] { "en" });
```

### Dependency injection

```csharp
services.AddEasyOcrSharp(o =>
{
    o.ModelCachePath = "/var/cache/easyocr";
    // GPU is automatic (ExecutionProvider = Auto). To force CPU in a multi-tenant host:
    // o.ExecutionProvider = OcrExecutionProvider.Cpu;
});

public class ReceiptParser(IEasyOcrService ocr) { /* inject anywhere */ }
```

Registered as a **singleton** — ONNX sessions are expensive to build and thread-safe to reuse.

### Hardening & resource limits

When OCR-ing **untrusted** images or PDFs, EasyOcrSharp guards against decompression-bomb / pixel-flood
denial of service. The defaults are generous; raise them if you legitimately process larger inputs, or
set them to `0` to disable a guard.

```csharp
await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
{
    MaxImagePixels = 100_000_000,   // reject images over 100 MP from the header, before decode (default)
});

var pdfOptions = new PdfOcrOptions
{
    MaxPages = 5000,                // reject documents with more pages (default)
    MaxPageMegapixels = 200,        // reject a page that would rasterize larger at the chosen DPI (default)
};
```

Failures surface as **typed exceptions** (all derive `EasyOcrSharpException`, so a catch-all still works):

| Exception | When |
|---|---|
| `ImageTooLargeException` | image exceeds `MaxImagePixels` |
| `PdfProcessingException` | corrupt / encrypted PDF, or a page/size guard tripped |
| `ModelDownloadException` | download failed, or a non-HTTPS / malformed model source |
| `ModelChecksumException` | downloaded model failed (or lacks) SHA256 verification |
| `OfflineModelMissingException` | model not cached and `Offline = true` |

**Warm-up (remove cold-start latency).** Preload the detector and recognizer packs so the first real
request doesn't pay model-download + session-init latency — ideal for serverless / scale-out:

```csharp
await ocr.WarmUp(new[] { "en" });   // downloads + initializes once, up front
```

### Resilient & offline model downloads

Model downloads are production-hardened: atomic, SHA256-verified, **resumable** (HTTP range), and
**retried** with exponential backoff. By default the model source must be **HTTPS** and every model must
have a known checksum. Tune everything via `ModelDownloadOptions`:

```csharp
await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
{
    Download = new ModelDownloadOptions
    {
        MaxRetries = 5,
        Offline = false,                              // true = never download; fail fast if not cached
        BaseUrlOverride = "https://mirror.corp/ocr",  // private mirror (must be https unless opted out)
        HttpClientFactory = () => httpClientFactory.CreateClient("ocr"), // proxy / corporate certs
        // AllowInsecureModelSource = true,           // permit a plain-http mirror you control
        // AllowUnverifiedModels   = true,            // permit unlisted models that have no registry checksum
        Progress = new Progress<ModelDownloadProgress>(p =>
            Console.WriteLine($"{p.FileName}: {p.Fraction:P0}")),
    },
});
```

For **air-gapped** deployments, pre-seed the cache and set `Offline = true` — a missing model then
throws a clear error instead of attempting a download.

### 🖥️ Command-line tool

```bash
dotnet tool install -g EasyOcrSharp.Cli
```

```bash
# recognize an image, a folder, or a glob
easyocrsharp scan receipt.png
easyocrsharp scan scans/ -r -l en,de --format json -o out/

# make a scanned PDF searchable
easyocrsharp pdf scan.pdf -o searchable.pdf

# pre-download models for an air-gapped host, then check what's there
easyocrsharp models pull en,fr
easyocrsharp models list
easyocrsharp models path

# versions, active execution provider, GPU status, cache location
easyocrsharp info
```

`scan` writes results to stdout and errors to stderr so it pipes cleanly, exits non-zero on failure,
and accepts the same tuning as the library (`--allowlist`, `--min-confidence`, `--paragraph`,
`--preprocess deskew,binarize,sharpen`, `--gpu`, `--jobs`). Add `--help` to any command.

### 🌐 Web service sample

[`samples/EasyOcrSharp.WebApi`](https://github.com/FarhanLodi/EasyOcrSharp/blob/main/samples/EasyOcrSharp.WebApi) is a runnable ASP.NET Core service —
`POST /ocr`, `POST /ocr/pdf`, `GET /health` and a browser upload page — with bounded concurrency,
upload limits, problem-details errors and a Dockerfile that already includes the native prerequisites
PDFium and ONNX Runtime need.

```bash
dotnet run --project samples/EasyOcrSharp.WebApi
curl -X POST "http://localhost:5000/ocr?lang=en&format=text" -F "file=@receipt.png"
```

<br>

# 📚 Reference

### 🌍 Supported languages

Languages are grouped by **script**; one recognizer covers an entire group, so `["en","es","fr"]`
loads a single model. Pack sizes vary widely with the network each script was trained on — some are a
few MB, some ~210 MB — which affects **first-run download size only**, not runtime behaviour.

| Pack | Size | Languages |
|---|---|---|
| `latin_g2` | ~15 MB | af, az, bs, cs, cy, da, de, en, es, et, fr, ga, hr, hu, id, is, it, ku, la, lt, lv, mi, ms, mt, nl, no, oc, pi, pl, pt, ro, rs_latin, sk, sl, sq, sv, sw, tl, tr, uz, vi |
| `cyrillic_g2` | ~15 MB | ru, rs_cyrillic, be, bg, uk, mn, abq, ady, kbd, ava, dar, inh, che, lbe, lez, tab, tjk |
| `zh_sim_g2` | ~22 MB | ch_sim |
| `korean_g2` | ~16 MB | ko |
| `japanese_g2` | ~17 MB | ja |
| `telugu_g2` | ~15 MB | te |
| `kannada_g2` | ~15 MB | kn |
| `arabic_g2` | ~210 MB | ar, fa, ug, ur |
| `devanagari_g2` | ~210 MB | hi, mr, ne, bh, mai, ang, bho, mah, sck, new, gom, sa, bgc |
| `bengali_g2` | ~210 MB | bn, as, mni |
| `thai_g1` | ~210 MB | th |
| `tamil_g1` | ~210 MB | ta |
| `zh_tra_g1` | ~215 MB | ch_tra |

That's **all 86 languages EasyOCR supports**, mapped exactly to the model each was trained on.

> **Not supported:** Greek (`el`) and Hebrew (`he`) — upstream EasyOCR ships no model for either
> script, so they cannot be exported.

### How model downloads work

EasyOcrSharp ships **no models in the NuGet package**. On the first call for a language it downloads,
into a local cache:

1. **CRAFT detector** (`craft_mlt_25k.onnx`, ~80 MB) — shared by all languages, downloaded once.
2. **CRNN recognizer** for the language's script pack (e.g. `latin_g2.onnx`).
3. A small **vocabulary sidecar** (`<pack>.vocab.json`).

Every file is **SHA256-verified** against a checksum baked into the library, so corrupted or tampered
downloads are rejected. Models are hosted on
[Hugging Face](https://huggingface.co/EasyOcrSharp/EasyOcrSharp-models).

Default cache: `%LOCALAPPDATA%\EasyOcrSharp\models` (Windows) or the platform equivalent. Override it:

```csharp
await using var ocr = new EasyOcrService(modelCachePath: @"D:\MyApp\Models");
```

```bash
EASYOCRSHARP_CACHE=/var/cache/easyocr                          # cache directory
EASYOCRSHARP_MODEL_BASE_URL=https://files.mycorp.example/ocr   # private/offline mirror
```

> **Offline / air-gapped:** pre-seed your cache directory with the `.onnx` + `.vocab.json` files from
> the [model repo](https://huggingface.co/EasyOcrSharp/EasyOcrSharp-models/tree/main) — no network is
> needed at runtime.

### Accuracy notes

EasyOcrSharp reproduces EasyOCR's pipeline faithfully — aspect-preserving resize, normalization, a
low-confidence contrast-retry pass, CRAFT box dilation, perspective de-warping of rotated text, and
CTC decoding (greedy by default, with optional beam / word-beam search) — so output matches upstream
EasyOCR. On top of that:

- **Reading order** is column-aware and bands rows by a line-height-relative tolerance, so headings,
  high-DPI scans, and multi-column pages come out in natural reading order.
- **Overlapping detections are de-duplicated** with IoU NMS (`DetectionOptions.NmsIouThreshold`, default
  `0.6`; set `0` to disable).
- On **multi-language** requests, scoring is biased toward the page's dominant script so an over-confident
  wrong-script pack can't hijack individual boxes.

As with any OCR:

- Visually identical glyphs (capital `I` vs lowercase `l`, `$` vs `8`) can be confused.
- Handwriting and low-resolution / low-contrast text are harder than clean printed text.
- Right-to-left scripts (Arabic) are returned in the model's character order.

### Building & testing

```bash
git clone https://github.com/FarhanLodi/EasyOcrSharp.git
cd EasyOcrSharp
dotnet build -c Release

# Everything — unit + real end-to-end integration tests. Downloads the models on first run
# and reports a pass/fail summary:
dotnet test

# Fast unit tests only (no models, no network):
dotnet test --filter "Category!=Integration"

# Only the model-backed integration tests:
dotnet test --filter "Category=Integration"

# Interactive console demo:
dotnet run --project test/EasyOcrSharp.Demo
```

The integration tests exercise every feature against the **real** engine (allow/block-lists,
detection-only, exporters, batch, metrics/tracing, health check, execution-provider fallback, and the
full PDF pipeline) — no mocks. The PDF fixtures live in
[test/assets/pdf/](https://github.com/FarhanLodi/EasyOcrSharp/tree/main/test/assets/pdf) and are
committed, so those tests run out of the box. A couple of tests still **skip** (never fail) until you
supply an optional fixture — a password-protected PDF named e.g. `encrypted_secret.pdf` for the
encrypted-document path, and `EASYOCRSHARP_TROCR_DIR` pointing at a TrOCR export for the handwriting
integration test.

| Path | Purpose |
|---|---|
| `src/EasyOcrSharp` | the core library (includes PDF input + searchable-PDF output) |
| `src/EasyOcrSharp.Gpu` | CUDA execution-provider package |
| `src/EasyOcrSharp.Cli` | the `easyocrsharp` command-line tool (`dotnet tool`) |
| `samples/EasyOcrSharp.WebApi` | ASP.NET Core service sample + Dockerfile |
| `test/EasyOcrSharp.Tests` | xUnit unit + integration tests |
| `test/EasyOcrSharp.Demo` | interactive console demo |
| `test/assets` | sample images |
| `tools/` | maintainer-only ONNX export + quantization scripts |

CI (GitHub Actions) builds and runs the unit tests on Linux and Windows for every push and PR.
See [CHANGELOG.md](https://github.com/FarhanLodi/EasyOcrSharp/blob/main/CHANGELOG.md) for release history.

<br>

## 🤝 Contributing

**Contributions are welcome!** New features, accuracy improvements, performance tuning, bug fixes,
additional language/model coverage, documentation, and tests are all appreciated.

- 🐛 **Found a bug?** Open an [issue](https://github.com/FarhanLodi/EasyOcrSharp/issues) with a
  minimal repro (image/PDF + the code and options you used).
- 💡 **Have an idea or feature request?** Open an issue to discuss it first, then send a PR.
- 🔧 **Sending a PR?** Branch from `main`, keep changes focused, and make sure `dotnet build -c Release`
  and the unit tests (`dotnet test --filter "Category!=Integration"`) pass.

If you're working on something larger, or want to collaborate on a feature, feel free to reach out
before starting so we can align on the approach.

## 💖 Support

If EasyOcrSharp saves you time, consider supporting development:

- 💳 **PayPal** — [paypal.me/FarhanLodi](https://paypal.me/FarhanLodi)
- 📱 **UPI (India)** — `farhanlodi5@oksbi`
- 🏦 **Bank transfer (USD)** — details below

<details>
<summary><b>USD bank transfer details (Wise)</b></summary>

<br>

USD account details for Farhan Lodi on Wise. Sending from a bank in the US? Use these details for a
domestic transfer. Sending from anywhere else? Make an international SWIFT transfer.

| Field | Value |
|---|---|
| Name | Farhan Lodi |
| Account type | Deposit |
| Routing number (wire and ACH) | `084009519` |
| Account number | `420927686563885` |
| SWIFT/BIC | `TRWIUS35XXX` |
| Bank address | Wise US Inc, 108 W 13th St, Wilmington, DE, 19801, United States |

Use the routing and account numbers when sending from the US, and the SWIFT/BIC when sending from
outside the US.

</details>

📧 Need more details, a different payment method, or have a question? Email
[farhanlodi31@gmail.com](mailto:farhanlodi31@gmail.com).

## 📬 Contact

For work inquiries, collaboration, feature requests, or any questions, reach out to:

**Farhan Lodi** — [farhanlodi31@gmail.com](mailto:farhanlodi31@gmail.com)

## 📄 License

MIT — see [LICENSE](https://github.com/FarhanLodi/EasyOcrSharp/blob/main/LICENSE). The code has no
copyleft or commercially-tiered dependency at any depth. The neural weights downloaded at runtime keep
their own upstream licences — EasyOCR and PaddleOCR models are Apache-2.0, TrOCR is MIT — and are
attributed in [NOTICE](https://github.com/FarhanLodi/EasyOcrSharp/blob/main/NOTICE).

## 🙏 Acknowledgments

- [EasyOCR](https://github.com/JaidedAI/EasyOCR) — the underlying CRAFT + CRNN models
- [ONNX Runtime](https://onnxruntime.ai/) — neural network execution
- [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp) — image decoding, encoding and the processing pipeline
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — the PP-StructureV3 models behind `AnalyzeDocumentAsync`

<div align="center">
<br>

**[⬆ Back to top](#easyocrsharp)**

<sub>Built with ❤️ for the .NET community · EasyOCR accuracy, zero Python</sub>

</div>
