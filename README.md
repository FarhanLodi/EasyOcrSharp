<div align="center">
  <img src="src/EasyOcrSharp/Assets/icon.gif" alt="EasyOcrSharp Logo" width="128" height="128">
  <h1>EasyOcrSharp</h1>

  [![NuGet](https://img.shields.io/nuget/v/EasyOcrSharp.svg)](https://www.nuget.org/packages/EasyOcrSharp)
  [![NuGet Downloads](https://img.shields.io/nuget/dt/EasyOcrSharp.svg)](https://www.nuget.org/packages/EasyOcrSharp)
  [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
</div>

**Native .NET 10 OCR powered by EasyOCR's neural models on ONNX Runtime.** No Python, no PyTorch, no embedded interpreter — just a small managed library that downloads the per-language ONNX models it needs on first use. Recognition accuracy matches upstream EasyOCR, because it runs EasyOCR's exact CRAFT + CRNN networks.

- 🌍 **80+ languages** across 13 script families (Latin, Cyrillic, Arabic, Devanagari, Bengali, Chinese — Simplified & Traditional, Korean, Japanese, Thai, Tamil, Telugu, Kannada)
- 📦 **~3 MB package** — models download on demand and are cached locally
- ⚡ **AOT / single-file friendly** — pure managed code over ONNX Runtime
- 🔒 **SHA256-verified** model downloads
- 🖥️ **CPU by default, optional CUDA GPU** via `EasyOcrSharp.Gpu`

## What changed in v2

EasyOcrSharp 1.x bundled a full Python + PyTorch + EasyOCR runtime inside the NuGet package (~1.5 GB). v2 replaces that with the same neural networks exported to ONNX and run via `Microsoft.ML.OnnxRuntime`.

| | v1 (Python bundled) | v2 (native ONNX) |
|---|---|---|
| Package size | ~1.5 GB | **~3 MB** + on-demand models |
| First-run cost | Minutes (Python boot + model dl) | Seconds (just model dl) |
| AOT / single-file publish | No | **Yes** |
| Native deps | Python interpreter + torch DLLs | ONNX Runtime only |
| GPU | CUDA via torch | CUDA via `EasyOcrSharp.Gpu` |

The public API (`EasyOcrService.ExtractTextFromImage`, `OcrResult`, `OcrLine`, `OcrBoundingBox`) is unchanged — v1 callers upgrade with no code changes beyond the version bump.

## Installation

```bash
dotnet add package EasyOcrSharp
```

For NVIDIA GPU acceleration (Windows/Linux x64 with CUDA 12+):

```bash
dotnet add package EasyOcrSharp.Gpu
```

## Quick start

```csharp
using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());

await using var ocr = new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());

// Extract text — pass one or more language codes.
var result = await ocr.ExtractTextFromImage("sample.png", new[] { "en" });

Console.WriteLine(result.FullText);
foreach (var line in result.Lines)
{
    Console.WriteLine($"  {line.Text}  (confidence {line.Confidence:P0})");
    var box = line.BoundingBox;
    Console.WriteLine($"    at [{box.MinX},{box.MinY} → {box.MaxX},{box.MaxY}]");
}
```

### What you get back

```csharp
public sealed class OcrResult
{
    public string FullText { get; }            // all lines joined by newlines
    public IReadOnlyList<OcrLine> Lines { get; }
    public IReadOnlyList<string> Languages { get; }
    public TimeSpan Duration { get; }
    public bool UsedGpu { get; }
}

public sealed class OcrLine
{
    public string Text { get; }
    public double Confidence { get; }                   // 0..1
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; } // 4 corners
    public OcrBoundingBox BoundingBox { get; }          // axis-aligned bounds (MinX/MinY/MaxX/MaxY + Width/Height/Center)
}
```

Detected text is grouped into **reading-order lines** (top-to-bottom), matching EasyOCR's `readtext()` output.

### Input sources

Besides a file path, you can OCR from a `Stream`, `byte[]`, `ReadOnlyMemory<byte>`, or an
already-decoded `Image<Rgb24>`:

```csharp
byte[] bytes = await File.ReadAllBytesAsync("photo.jpg");
var result = await ocr.ExtractTextFromImage(bytes, new[] { "en" });
```

### Tuning a call with `RecognitionOptions`

```csharp
var options = new RecognitionOptions
{
    Grouping = TextGrouping.Paragraph,   // Word | Line (default) | Paragraph
    MinConfidence = 0.3,                 // drop low-confidence lines
    MaxDegreeOfParallelism = 8,          // regions recognized concurrently
    AdjustContrast = true,               // low-confidence contrast retry
};
var result = await ocr.ExtractTextFromImage("doc.png", new[] { "en" }, options);
```

### Dependency injection

```csharp
services.AddEasyOcrSharp(o =>
{
    o.UseGpu = false;
    o.ModelCachePath = "/var/cache/easyocr";
});

// then inject IEasyOcrService anywhere
public class MyService(IEasyOcrService ocr) { /* ... */ }
```
The service is registered as a singleton (ONNX sessions are expensive and thread-safe to reuse).

## Multiple languages at once

Pass several language codes to OCR mixed-script images. Each detected region is read by every requested script pack, and the highest-confidence result wins:

```csharp
// A sign with English, Chinese, and Russian on it:
var result = await ocr.ExtractTextFromImage("street_signs.png",
    new[] { "en", "ch_sim", "ru" });
```

Note that each additional script family loads its own model (see sizes below), so request only the languages you actually expect in the image.

## How model downloads work

EasyOcrSharp ships **no models in the NuGet package**. On the first call for a given language it downloads, into a local cache:

1. **CRAFT text detector** (`craft_mlt_25k.onnx`, ~80 MB) — shared across all languages, downloaded once.
2. **CRNN recognizer** for the language's script pack (e.g. `latin_g2.onnx`) — one per script family.
3. A tiny **vocabulary sidecar** (`<pack>.vocab.json`) describing the characters that model emits.

Every download is **SHA256-verified** against a checksum baked into the library, so a corrupted or tampered file is rejected. Models are hosted on [Hugging Face](https://huggingface.co/EasyOcrSharp/EasyOcrSharp-models).

Files are cached at `%LOCALAPPDATA%\EasyOcrSharp\models` on Windows (or the platform equivalent on Linux/macOS). Override the cache location or the download source:

```csharp
// Per-instance cache directory:
await using var ocr = new EasyOcrService(modelCachePath: @"D:\MyApp\Models");
```

```bash
# Environment variable cache override:
EASYOCRSHARP_CACHE=/var/cache/easyocr

# Private model mirror (e.g. for air-gapped environments — pre-seed it with the .onnx + .vocab.json files):
EASYOCRSHARP_MODEL_BASE_URL=https://files.mycorp.example/easyocr
```

> **Tip for offline / air-gapped deployment:** copy the contents of the
> [model repo](https://huggingface.co/EasyOcrSharp/EasyOcrSharp-models/tree/main)
> into your cache directory ahead of time, and no network access is needed at runtime.

## Supported languages

Languages are grouped by **script**. One CRNN recognizer covers an entire group, so requesting `["en", "es", "fr"]` only loads one recognizer file. Model size varies because EasyOCR ships newer, smaller "generation-2" networks for some scripts and only larger "generation-1" networks for others — this affects **first-run download size only**, not the package.

| Pack | Model size | Languages |
|---|---|---|
| `latin_g2` | ~15 MB | en, es, fr, de, it, pt, nl, pl, cs, sv, hu, fi, ro, no, da, hr, sk, sl, sq, tr, ca, eu, gl, id, ms, tl, vi, af, et, lv, lt, is, ga, mt, cy, la, oc, ku, mi, sr_latn, rs_latin |
| `cyrillic_g2` | ~15 MB | ru, sr, kk, az, uz, ky, mn, be, uk, bg, mk, tg, ab |
| `zh_sim_g2` | ~22 MB | ch_sim, zh_sim |
| `korean_g2` | ~16 MB | ko |
| `japanese_g2` | ~17 MB | ja |
| `arabic_g2` | ~210 MB | ar, fa, ur, ug, ps |
| `devanagari_g2` | ~210 MB | hi, mr, ne, sa |
| `bengali_g2` | ~210 MB | bn, as |
| `thai_g1` | ~210 MB | th |
| `tamil_g1` | ~210 MB | ta |
| `telugu_g2` | ~15 MB | te |
| `kannada_g2` | ~15 MB | kn |
| `zh_tra_g1` | ~215 MB | ch_tra (Traditional Chinese) |

The ~210 MB packs (Arabic, Devanagari, Bengali, Thai) are EasyOCR's generation-1 networks — EasyOCR never released smaller versions for those scripts. They download once and are cached like any other model.

If you request a language that has no pack, EasyOcrSharp logs a warning and skips it — other requested languages still work.

> **Not supported:** Greek (`el`) — upstream EasyOCR has no Greek recognition model, so it can't be exported.

## GPU acceleration

```csharp
await using var ocr = new EasyOcrService(useGpu: true, logger: logger);
```

Requires the `EasyOcrSharp.Gpu` package, CUDA 12+ on PATH, and a compatible NVIDIA GPU. Falls back silently to CPU if CUDA isn't available.

## Notes on accuracy

EasyOcrSharp reproduces EasyOCR's recognition pipeline faithfully — preprocessing (aspect-preserving resize, normalization, low-confidence contrast retry), CRAFT box dilation, and CTC greedy decoding — so output matches upstream EasyOCR. As with any OCR:

- Visually identical glyphs in some fonts (capital `I` vs lowercase `l`, `$` vs `8`) can be confused.
- Handwriting and low-resolution / low-contrast text are harder than clean printed text.
- Right-to-left scripts (Arabic) are returned in the model's character order.

## Building & testing from source

```bash
git clone https://github.com/easyocrsharp/EasyOcrSharp.git
cd EasyOcrSharp
dotnet build -c Release

# Unit tests (no model downloads needed):
dotnet test --filter "Category!=Integration"

# Accuracy/integration tests (need the models — point the cache at a folder containing them):
EASYOCRSHARP_CACHE=/path/to/onnx_models dotnet test --filter "Category=Integration"

# Run the interactive console demo:
dotnet run --project test/EasyOcrSharp.Demo
```

Solution layout:

| Path | Purpose |
|---|---|
| `src/EasyOcrSharp` | the library |
| `src/EasyOcrSharp.Gpu` | the CUDA execution-provider package |
| `test/EasyOcrSharp.Tests` | xUnit unit + integration tests |
| `test/EasyOcrSharp.Demo` | interactive console demo |
| `test/assets` | sample images used by the demo and tests

CI (GitHub Actions) builds and runs the unit tests on Linux and Windows for every push and PR.

## License

MIT. See [LICENSE](LICENSE). EasyOCR (the upstream model authors) is also MIT-licensed.

## Acknowledgments

- [EasyOCR](https://github.com/JaidedAI/EasyOCR) — the underlying CRAFT + CRNN models
- [ONNX Runtime](https://onnxruntime.ai/) — neural network execution
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) — image I/O and resizing
