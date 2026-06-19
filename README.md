<div align="center">
  <img src="src/EasyOcrSharp/Assets/icon.gif" alt="EasyOcrSharp Logo" width="128" height="128">
  <h1>EasyOcrSharp</h1>

  **High-accuracy, fully-offline OCR for .NET — EasyOCR's neural models running natively on ONNX Runtime. No Python.**

  [![NuGet](https://img.shields.io/nuget/v/EasyOcrSharp.svg)](https://www.nuget.org/packages/EasyOcrSharp)
  [![NuGet Downloads](https://img.shields.io/nuget/dt/EasyOcrSharp.svg)](https://www.nuget.org/packages/EasyOcrSharp)
  [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
  [![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
</div>

---

EasyOcrSharp runs EasyOCR's exact **CRAFT** text detector and **CRNN** recognizers, exported to ONNX,
through `Microsoft.ML.OnnxRuntime`. You get EasyOCR-grade accuracy in a tiny managed package — no
Python interpreter, no PyTorch, no native OCR binaries, and nothing leaves the machine.

| | |
|---|---|
| 🌍 **86 languages** | 13 script families: Latin, Cyrillic, Arabic, Devanagari, Bengali, Chinese (Simplified & Traditional), Korean, Japanese, Thai, Tamil, Telugu, Kannada |
| 📦 **~3 MB package** | Models download on demand and are cached locally — nothing is bundled |
| 🔒 **Verified & private** | Every model download is SHA256-checked; OCR runs fully offline |
| ⚡ **Fast** | Concurrent multi-region recognition; automatic CUDA GPU with CPU fallback; tunable thread limits |
| 🧩 **Flexible** | File / `Stream` / `byte[]` / `Image` / **PDF** input, region-of-interest, recognize-from-boxes, word/line/paragraph grouping, auto language detection |
| 📄 **Document-ready** | **Searchable-PDF** output, plus hOCR / ALTO / TSV / JSON exporters |
| 🎯 **Accurate fields** | Allow/block-lists, **beam / word-beam decoders**, per-box **rotation**, custom recognizers, and exposed detection/grouping/contrast thresholds |
| 📊 **Production-grade** | OpenTelemetry metrics & tracing, health check, resilient resumable downloads, batch API |
| 🩺 **Scan-ready** | Optional deskew, orientation correction, adaptive binarize & denoise |
| 🛠️ **Modern .NET** | AOT- & single-file-friendly, DI-ready, .NET 10 |

## Table of contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [The result model](#the-result-model)
- [Input sources](#input-sources)
- [Recognition options](#recognition-options)
- [Region of interest](#region-of-interest)
- [Multiple languages](#multiple-languages)
- [Automatic language detection](#automatic-language-detection)
- [Scanned-document preprocessing](#scanned-document-preprocessing)
- [PDF input & searchable PDF](#pdf-input--searchable-pdf)
- [Output formats (hOCR / ALTO / TSV / JSON)](#output-formats-hocr--alto--tsv--json)
- [Constrained fields (allow/block-lists & detection thresholds)](#constrained-fields-allowblock-lists--detection-thresholds)
- [Decoders, rotation & batching](#decoders-rotation--batching)
- [Recognize from known boxes](#recognize-from-known-boxes)
- [Custom recognizers](#custom-recognizers)
- [Fine-tuning grouping & contrast](#fine-tuning-grouping--contrast)
- [Quantized models](#quantized-models)
- [Detection-only & visualization](#detection-only--visualization)
- [Batch processing](#batch-processing)
- [Observability & health checks](#observability--health-checks)
- [Dependency injection](#dependency-injection)
- [GPU & execution providers](#gpu--execution-providers)
- [Hardening & resource limits](#hardening--resource-limits)
- [Resilient & offline model downloads](#resilient--offline-model-downloads)
- [Supported languages](#supported-languages)
- [How model downloads work](#how-model-downloads-work)
- [Accuracy notes](#accuracy-notes)
- [Building & testing](#building--testing)
- [License](#license)

## Installation

```bash
dotnet add package EasyOcrSharp
```

PDF input and searchable-PDF output are **built in** — no extra package.

For NVIDIA GPU acceleration (Windows/Linux x64, CUDA 12+):

```bash
dotnet add package EasyOcrSharp.Gpu
```

> Upgrading from 1.x? v2 replaced the ~1.5 GB embedded Python+PyTorch runtime with native ONNX.
> The public API (`EasyOcrService`, `OcrResult`, `OcrLine`, `OcrBoundingBox`) is unchanged.

## Quick start

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

## The result model

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

## Input sources

OCR from a file path, a `Stream`, raw encoded bytes, or an already-decoded ImageSharp image:

```csharp
await ocr.ExtractTextFromImage("photo.jpg",            new[] { "en" });
await ocr.ExtractTextFromImage(stream,                  new[] { "en" });
await ocr.ExtractTextFromImage(File.ReadAllBytes("p"),  new[] { "en" });   // byte[]
await ocr.ExtractTextFromImage(memory,                  new[] { "en" });   // ReadOnlyMemory<byte>
await ocr.ExtractTextFromImage(image,                   new[] { "en" });   // Image<Rgb24> (caller-owned)
```

All overloads accept an optional `RecognitionOptions` and a `CancellationToken`.

## Recognition options

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

## Region of interest

Restrict OCR to a rectangle — ideal for a fixed field (price, license plate, banner) and faster than
scanning the whole image. Boxes are always reported in the **original image's** coordinates.

```csharp
// Absolute pixels:
var roi = new RecognitionOptions { Region = OcrRegion.Pixels(x: 40, y: 320, width: 500, height: 80) };

// Resolution-independent fractions — e.g. the bottom third:
var bottom = new RecognitionOptions { Region = OcrRegion.Fraction(0, 0.66, 1, 0.34) };

var result = await ocr.ExtractTextFromImage("receipt.png", new[] { "en" }, bottom);
```

## Multiple languages

Pass several codes for mixed-script images — each region is read by every requested script pack and
the highest-confidence result wins:

```csharp
var result = await ocr.ExtractTextFromImage("street_sign.png", new[] { "en", "ch_sim", "ru" });
```

Each additional script family loads its own model, so request only the scripts you expect.

## Automatic language detection

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

## Scanned-document preprocessing

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
    },
};
var result = await ocr.ExtractTextFromImage("scan.jpg", new[] { "en" }, opts);
```

When `Deskew` or `DetectOrientation` rotate the image, bounding boxes are reported in the corrected
image's coordinate space.

## PDF input & searchable PDF

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
callback. The searchable text layer is best for Latin scripts (base-14 font, WinAnsi encoding).

## Output formats (hOCR / ALTO / TSV / JSON)

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

## Constrained fields (allow/block-lists & detection thresholds)

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

## Decoders, rotation & batching

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

## Recognize from known boxes

If you already have regions — from `DetectRegionsAsync`, a previous run, or your own layout analysis —
recognize them directly and skip detection (EasyOCR's `recognize()`):

```csharp
using var image = Image.Load<Rgb24>("form.png");

// e.g. reuse a detection pass, or pass your own polygons (pixel coordinates):
IReadOnlyList<DetectedRegion> regions = await ocr.DetectRegionsAsync(image);

OcrResult result = await ocr.RecognizeRegionsAsync(image, regions, new[] { "en" });
```

There's also an overload taking raw polygons (`IEnumerable<IReadOnlyList<OcrPoint>>`).

## Custom recognizers

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

## Fine-tuning grouping & contrast

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

## Quantized models

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

## Detection-only & visualization

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
await annotated.SaveAsPngAsync("page.annotated.png");
```

## Batch processing

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

## Observability & health checks

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

## Dependency injection

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

## GPU & execution providers

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

(NuGet can't add the package for you at restore time, and a library can't install one at runtime — so
once `EasyOcrSharp.Gpu` is referenced, GPU is used automatically with no code change; adding the package
is the only manual step.)

## Hardening & resource limits

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

### Warm-up (remove cold-start latency)

Preload the detector and recognizer packs so the first real request doesn't pay model-download +
session-init latency — ideal for serverless / scale-out:

```csharp
await ocr.WarmUp(new[] { "en" });   // downloads + initializes once, up front
```

## Resilient & offline model downloads

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

## Supported languages

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

## How model downloads work

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

## Accuracy notes

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

## Building & testing

```bash
git clone https://github.com/easyocrsharp/EasyOcrSharp.git
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
full PDF pipeline) — no mocks. The **PDF** tests need two small fixtures you generate yourself; they're
**skipped** (never failed) until present. See [test/assets/pdf/README.md](test/assets/pdf/README.md)
for the exact one-page and three-page PDFs to drop in.

| Path | Purpose |
|---|---|
| `src/EasyOcrSharp` | the core library (includes PDF input + searchable-PDF output) |
| `src/EasyOcrSharp.Gpu` | CUDA execution-provider package |
| `test/EasyOcrSharp.Tests` | xUnit unit + integration tests |
| `test/EasyOcrSharp.Demo` | interactive console demo |
| `test/assets` | sample images |
| `tools/` | maintainer-only ONNX export + quantization scripts |

CI (GitHub Actions) builds and runs the unit tests on Linux and Windows for every push and PR.
See [CHANGELOG.md](CHANGELOG.md) for release history.

## License

MIT — see [LICENSE](LICENSE). EasyOCR (the upstream model authors) is also MIT-licensed.

## Acknowledgments

- [EasyOCR](https://github.com/JaidedAI/EasyOCR) — the underlying CRAFT + CRNN models
- [ONNX Runtime](https://onnxruntime.ai/) — neural network execution
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) — image I/O and resizing
