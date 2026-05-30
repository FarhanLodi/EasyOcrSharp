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
| ⚡ **Fast** | Concurrent multi-region recognition; optional CUDA GPU |
| 🧩 **Flexible** | File / `Stream` / `byte[]` / `Image` input, region-of-interest, word/line/paragraph grouping, auto language detection |
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
- [Dependency injection](#dependency-injection)
- [GPU acceleration](#gpu-acceleration)
- [Supported languages](#supported-languages)
- [How model downloads work](#how-model-downloads-work)
- [Accuracy notes](#accuracy-notes)
- [Building & testing](#building--testing)
- [License](#license)

## Installation

```bash
dotnet add package EasyOcrSharp
```

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
    public string FullText { get; }                 // all lines joined by newlines
    public IReadOnlyList<OcrLine> Lines { get; }
    public IReadOnlyList<string> Languages { get; }
    public TimeSpan Duration { get; }
    public bool UsedGpu { get; }
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

## Dependency injection

```csharp
services.AddEasyOcrSharp(o =>
{
    o.UseGpu = false;
    o.ModelCachePath = "/var/cache/easyocr";
});

public class ReceiptParser(IEasyOcrService ocr) { /* inject anywhere */ }
```

Registered as a **singleton** — ONNX sessions are expensive to build and thread-safe to reuse.

## GPU acceleration

```csharp
await using var ocr = new EasyOcrService(useGpu: true);
```

Requires the `EasyOcrSharp.Gpu` package, CUDA 12+ on PATH, and a compatible NVIDIA GPU. Falls back to
CPU automatically if CUDA isn't available.

## Supported languages

Languages are grouped by **script**; one recognizer covers an entire group, so `["en","es","fr"]`
loads a single model. Sizes differ because EasyOCR ships small "generation-2" models for some scripts
and only larger "generation-1" models for others — this affects **first-run download size only**.

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
CTC greedy decoding — so output matches upstream EasyOCR. As with any OCR:

- Visually identical glyphs (capital `I` vs lowercase `l`, `$` vs `8`) can be confused.
- Handwriting and low-resolution / low-contrast text are harder than clean printed text.
- Right-to-left scripts (Arabic) are returned in the model's character order.

## Building & testing

```bash
git clone https://github.com/easyocrsharp/EasyOcrSharp.git
cd EasyOcrSharp
dotnet build -c Release

# Unit tests (no model downloads needed):
dotnet test --filter "Category!=Integration"

# Accuracy/integration tests (need models — point the cache at a folder containing them):
EASYOCRSHARP_CACHE=/path/to/onnx_models dotnet test --filter "Category=Integration"

# Interactive console demo:
dotnet run --project test/EasyOcrSharp.Demo
```

| Path | Purpose |
|---|---|
| `src/EasyOcrSharp` | the library |
| `src/EasyOcrSharp.Gpu` | the CUDA execution-provider package |
| `test/EasyOcrSharp.Tests` | xUnit unit + integration tests |
| `test/EasyOcrSharp.Demo` | interactive console demo |
| `test/assets` | sample images |

CI (GitHub Actions) builds and runs the unit tests on Linux and Windows for every push and PR.
See [CHANGELOG.md](CHANGELOG.md) for release history.

## License

MIT — see [LICENSE](LICENSE). EasyOCR (the upstream model authors) is also MIT-licensed.

## Acknowledgments

- [EasyOCR](https://github.com/JaidedAI/EasyOCR) — the underlying CRAFT + CRNN models
- [ONNX Runtime](https://onnxruntime.ai/) — neural network execution
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) — image I/O and resizing
