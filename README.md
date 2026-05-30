<div align="center">
  <img src="src/EasyOcrSharp/Assets/icon.gif" alt="EasyOcrSharp Logo" width="128" height="128">
  <h1>EasyOcrSharp</h1>

  [![NuGet](https://img.shields.io/nuget/v/EasyOcrSharp.svg)](https://www.nuget.org/packages/EasyOcrSharp)
  [![NuGet Downloads](https://img.shields.io/nuget/dt/EasyOcrSharp.svg)](https://www.nuget.org/packages/EasyOcrSharp)
  [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
</div>

**Native .NET 10 OCR powered by EasyOCR's neural models on ONNX Runtime.** No Python, no PyTorch, no embedded interpreter — just a small managed library that downloads the per-language ONNX models it needs on first use.

## What changed in v2

EasyOcrSharp 1.x bundled a full Python 3.11 + PyTorch + EasyOCR runtime inside the NuGet package (~1.5 GB). v2 replaces that with the same neural networks exported to ONNX and run via `Microsoft.ML.OnnxRuntime`.

| | v1 (Python bundled) | v2 (native ONNX) |
|---|---|---|
| Package size | ~1.5 GB | ~3 MB + on-demand models |
| First-run cost | Minutes (Python boot + model dl) | Seconds (just model dl) |
| Per-language model size | ~50 MB | ~5–15 MB |
| AOT / single-file publish | No | Yes |
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

var result = await ocr.ExtractTextFromImage("sample.png", new[] { "en", "hi" });

Console.WriteLine(result.FullText);
foreach (var line in result.Lines)
{
    Console.WriteLine($"  {line.Text} (conf {line.Confidence:P0})");
}
```

## How model downloads work

On the first call for a given language, EasyOcrSharp downloads two files:

1. **CRAFT detector** (`craft_mlt_25k.onnx`, ~80 MB) — shared across all languages, downloaded once
2. **CRNN recognizer** for the language's script pack (e.g. `latin_g2.onnx`, ~15 MB) — one per script family

Files are cached at `%LOCALAPPDATA%\EasyOcrSharp\models` on Windows (or the equivalent on Linux/macOS). Override the cache location three ways:

```csharp
// Per-instance:
await using var ocr = new EasyOcrService(modelCachePath: @"D:\MyApp\Models");
```

```bash
# Environment variable:
EASYOCRSHARP_CACHE=/var/cache/easyocr
```

```bash
# Private model mirror (e.g. for air-gapped environments):
EASYOCRSHARP_MODEL_BASE_URL=https://files.mycorp.example/easyocr
```

## Supported languages

Languages are grouped by script. One CRNN recognizer covers an entire group, so requesting `["en", "es", "fr"]` only loads one recognizer file.

| Pack | Languages |
|---|---|
| `latin_g2` | en, es, fr, de, it, pt, nl, pl, cs, sv, hu, fi, ro, no, da, hr, sk, sl, tr, ca, eu, id, ms, tl, vi, … |
| `cyrillic_g2` | ru, sr, kk, az, uz, ky, mn, be, uk, bg, mk, tg |
| `arabic_g2` | ar, fa, ur, ug, ps |
| `devanagari_g2` | hi, mr, ne, sa |
| `bengali_g2` | bn, as |
| `zh_sim_g2` | ch_sim, zh_sim |
| `korean_g2` | ko |
| `japanese_g2` | ja |

If you request a language that doesn't have a pack yet, EasyOcrSharp logs a warning and skips it — other requested languages still work.

## GPU acceleration

```csharp
await using var ocr = new EasyOcrService(useGpu: true, logger: logger);
```

Requires `EasyOcrSharp.Gpu`, CUDA 12+ on PATH, and a compatible NVIDIA GPU. Falls back silently to CPU if CUDA isn't available.

## Building from source

```bash
git clone https://github.com/easyocrsharp/EasyOcrSharp.git
cd EasyOcrSharp
dotnet build
```

## Re-exporting models from EasyOCR

The ONNX files served from the GitHub Release are produced by `tools/export_onnx.py`. See [tools/README.md](tools/README.md) for the maintainer workflow.

## License

MIT. See [LICENSE](LICENSE). EasyOCR (the upstream model authors) is also MIT-licensed.

## Acknowledgments

- [EasyOCR](https://github.com/JaidedAI/EasyOCR) — the underlying CRAFT + CRNN models
- [ONNX Runtime](https://onnxruntime.ai/) — neural network execution
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) — image I/O and resizing
