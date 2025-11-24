<div align="center">
  <img src="src/EasyOcrSharp/Assets/icon.gif" alt="EasyOcrSharp Logo" width="128" height="128">
  <h1>EasyOcrSharp</h1>
  
  [![NuGet](https://img.shields.io/nuget/v/EasyOcrSharp.svg)](https://www.nuget.org/packages/EasyOcrSharp)
  [![NuGet Downloads](https://img.shields.io/nuget/dt/EasyOcrSharp.svg)](https://www.nuget.org/packages/EasyOcrSharp)
  [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
</div>

High-accuracy OCR for .NET 9 powered by the [EasyOCR](https://github.com/JaidedAI/EasyOCR) Python library. EasyOcrSharp uses a **two-package architecture** with a pre-bundled Python runtime, so you can ship a single .NET package **without requiring users to install Python or download dependencies at runtime**.

## Architecture

EasyOcrSharp uses a **two-package architecture** for optimal performance and reliability:

### 📦 EasyOcrSharp (Main Package)
- Contains only C# logic and API
- Lightweight and fast
- Automatically references `EasyOcrSharp.Runtime` as a dependency
- Downloads only language models on-demand (first run)

### 🐍 EasyOcrSharp.Runtime (Runtime Package)
- Contains pre-bundled Python 3.11 embedded runtime
- Includes all required site-packages:
  - `easyocr`
  - `torch`, `torchvision`
  - `pillow`, `numpy`
  - `opencv-python-headless`
  - All EasyOCR dependencies
- **NO models** (models download separately on-demand)
- Automatically installed when you install `EasyOcrSharp`

### ⚡ Runtime Behavior

When your app references `EasyOcrSharp`:
1. NuGet automatically installs `EasyOcrSharp.Runtime`
2. On first call, the library uses the Python runtime **directly from the NuGet cache** (no copying needed)
3. **Only models are downloaded on-demand** and cached permanently in LocalAppData
4. **No pip, no Python downloads, no dependency installations**
5. After models download once → **full offline operation**

## Features

- ✅ **Zero Python installation required** – Python 3.11 is pre-bundled in the runtime package
- ✅ **Zero runtime downloads** – Python and dependencies are pre-bundled, only models download on-demand
- ✅ **Fast first run** – Only model downloads happen on first use (no Python/dependency downloads)
- ✅ Async C# API with automatic GPU detection and multilingual support
- ✅ **Image OCR** – Extract text from image files (PNG, JPEG, GIF, BMP, TIFF)
- ✅ **Multilingual OCR** – Process multiple languages in parallel for better accuracy
- ✅ **Automatic Language Grouping** – Intelligently groups languages for optimal OCR performance
- ✅ **Language Compatibility Handling** – Automatically handles language dependencies (e.g., Arabic, Thai, Chinese)
- ✅ Bounding box coordinates and polygon points for each recognized line
- ✅ Automatic English inclusion for non-Latin scripts (Hindi, Arabic, etc.)
- ✅ UTF-8 console encoding support for proper Unicode character display
- ✅ Lightweight logging via `Microsoft.Extensions.Logging`
- ✅ Example console app demonstrating end-to-end usage

## Installation

Install EasyOcrSharp via NuGet Package Manager:

```bash
dotnet add package EasyOcrSharp
```

Or via Package Manager Console:

```powershell
Install-Package EasyOcrSharp
```

Or add it directly to your `.csproj` file:

```xml
<PackageReference Include="EasyOcrSharp" Version="1.0.0" />
```

The `EasyOcrSharp.Runtime` package is automatically installed as a dependency. You don't need to reference it directly.

## Prerequisites

- **.NET SDK 9.0 or later** - Required to build and run applications using EasyOcrSharp
- **Internet access on first run (models only)** - Required only to download language models on first use
- **NO Python installation needed** - Python 3.11 is pre-bundled in `EasyOcrSharp.Runtime`
- **NO pip, NO wheel downloads** - All Python dependencies are pre-bundled

> **Important:** 
> - Python 3.11 and all dependencies (`easyocr`, `torch`, `pillow`, `numpy`, etc.) are **pre-bundled** in `EasyOcrSharp.Runtime`
> - **Only language models** are downloaded on-demand on first use
> - Models are cached permanently after first download
> - After models are cached, **full offline operation** is possible
> - First run is fast - only model downloads occur (typically 10-50 MB per language)

## Supported Languages

EasyOcrSharp supports **80+ languages** through EasyOCR. Here's the complete list:

| Latin Scripts | Asian Scripts | Indic Scripts | Middle Eastern | Cyrillic Scripts | Other Scripts |
|---------------|--------------|---------------|---------------|------------------|---------------|
| English (en) | Chinese Simplified (ch_sim, zh_sim) | Hindi (hi) | Arabic (ar) | Russian (ru) | Greek (el) |
| Spanish (es) | Chinese Traditional (ch_tra, zh_tra) | Bengali (bn) | Persian/Farsi (fa) | Serbian Cyrillic (sr) | Amharic (am) |
| French (fr) | Japanese (ja) | Telugu (te) | Uyghur (ug) | Kazakh (kk) | Javanese (jv) |
| German (de) | Korean (ko) | Tamil (ta) | Hebrew (he) | Azerbaijani (az) | Sundanese (su) |
| Italian (it) | Thai (th) | Marathi (mr) | Kurdish (ku) | Uzbek (uz) | |
| Portuguese (pt) | Burmese (my) | Gujarati (gu) | Pashto (ps) | Georgian (ka) | |
| Dutch (nl) | Khmer (km) | Kannada (kn) | | Armenian (hy) | |
| Polish (pl) | Lao (lo) | Malayalam (ml) | | | |
| Czech (cs) | Mongolian (mn) | Nepali (ne) | | | |
| Swedish (sv) | Tibetan (bo) | Punjabi (pa) | | | |
| Hungarian (hu) | | Sinhala (si) | | | |
| Finnish (fi) | | Assamese (as) | | | |
| Romanian (ro) | | Oriya (or) | | | |
| Norwegian (no) | | Urdu (ur) | | | |
| Danish (da) | | | | | |
| Croatian (hr) | | | | | |
| Slovak (sk) | | | | | |
| Slovenian (sl) | | | | | |
| Serbian Latin (sr_latn) | | | | | |
| Bulgarian (bg) | | | | | |
| Ukrainian (uk) | | | | | |
| Belarusian (be) | | | | | |
| Macedonian (mk) | | | | | |
| Albanian (sq) | | | | | |
| Estonian (et) | | | | | |
| Latvian (lv) | | | | | |
| Lithuanian (lt) | | | | | |
| Icelandic (is) | | | | | |
| Irish (ga) | | | | | |
| Maltese (mt) | | | | | |
| Afrikaans (af) | | | | | |
| Indonesian (id) | | | | | |
| Malay (ms) | | | | | |
| Tagalog (tl) | | | | | |
| Vietnamese (vi) | | | | | |
| Turkish (tr) | | | | | |
| Catalan (ca) | | | | | |
| Basque (eu) | | | | | |
| Galician (gl) | | | | | |

> **Note**: Language codes are case-insensitive. Use lowercase codes (e.g., `"en"`, `"hi"`, `"ar"`) for consistency.

For the most up-to-date list and language code details, see the [EasyOCR documentation](https://github.com/JaidedAI/EasyOCR#supported-languages).

## Quick Start

### Basic Usage

```csharp
using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Use default embedded Python runtime (from EasyOcrSharp.Runtime package)
// Models will be cached in LocalAppData\EasyOcrSharp\models by default
await using var ocr = new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());

// Or specify a custom path for model downloads
await using var ocr = new EasyOcrService(
    modelCachePath: @"D:\MyApp\Models",
    logger: loggerFactory.CreateLogger<EasyOcrService>());

// Extract text from an image with specified languages
// GPU is automatically detected and used if available
// Models are downloaded on-demand on first use
var result = await ocr.ExtractTextFromImage("sample.png", new[] { "en", "hi", "ar" });

Console.WriteLine(result.FullText);

foreach (var line in result.Lines)
{
    if (!line.BoundingBox.IsEmpty)
    {
        var bbox = line.BoundingBox;
        Console.WriteLine(
            $"{line.Text} (confidence {line.Confidence:P1}) " +
            $"bbox=([{bbox.MinX:F1}, {bbox.MinY:F1}] → [{bbox.MaxX:F1}, {bbox.MaxY:F1}])");
    }
    else
    {
        Console.WriteLine($"{line.Text} (confidence {line.Confidence:P1})");
    }
}
```

## Advanced Features

### Multilingual OCR

EasyOcrSharp automatically handles multilingual images by:
- **Grouping languages** for optimal compatibility (e.g., Arabic requires specific companions)
- **Processing language groups in parallel** for better performance
- **Merging and deduplicating results** from multiple language groups
- **Automatically including English** when using non-Latin scripts (Hindi, Arabic, Bengali, etc.)

```csharp
// Process image with multiple languages
// The library will automatically:
// 1. Group languages: ["ar", "fa", "ur", "ug", "en"] and ["hi", "en"]
// 2. Run OCR in parallel for each group
// 3. Merge and deduplicate results
// 4. Download models on-demand if not cached
var result = await ocr.ExtractTextFromImage("multilingual.png", new[] { "en", "hi", "ar" });
```

### Language Compatibility

Some languages have specific compatibility requirements:

- **Arabic (`ar`)**: Can only work with `["ar", "fa", "ur", "ug", "en"]`. Other languages will be processed in separate groups.
- **Thai (`th`)**: Requires English to be included.
- **Chinese/Japanese/Korean**: Require English (`ch_sim`, `ch_tra`, `zh_sim`, `zh_tra`, `ja`, `ko`).
- **Non-Latin scripts**: English is automatically added for better multilingual support (Hindi, Bengali, Telugu, etc.).

The library handles these automatically - you just specify the languages you want, and it groups them appropriately.

### Processing Image Streams

You can also process images from streams without saving to disk:

```csharp
await using var ocr = new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());

using var imageStream = File.OpenRead("sample.png");
var result = await ocr.ExtractTextFromImage(imageStream, new[] { "en", "hi" }, "sample.png");

Console.WriteLine(result.FullText);
```

### Result Structure

The `OcrResult` object contains:

- **FullText**: Concatenated text from all detected lines
- **Lines**: Collection of `OcrLine` objects with detailed information
- **Languages**: Languages used during recognition
- **Duration**: Time taken for OCR processing
- **UsedGpu**: Whether GPU acceleration was used

Each `OcrLine` includes:
- **Text**: Extracted text
- **Confidence**: Recognition confidence (0.0 to 1.0)
- **BoundingBox**: Rectangular bounding box coordinates
- **BoundingPolygon**: Four-point polygon coordinates

Example output format:

```
- Hello world (confidence: 98.5 %) bbox=([12.1, 34.7] → [220.4, 58.9])
```

JSON output example:

```json
{
  "fullText": "Hello world\nLorem ipsum",
  "lines": [
    {
      "text": "Hello world",
      "confidence": 0.985,
      "boundingPolygon": [
        { "x": 12.1, "y": 34.7 },
        { "x": 220.4, "y": 34.7 },
        { "x": 220.4, "y": 58.9 },
        { "x": 12.1, "y": 58.9 }
      ],
      "boundingBox": {
        "minX": 12.1,
        "minY": 34.7,
        "maxX": 220.4,
        "maxY": 58.9,
        "width": 208.3,
        "height": 24.2,
        "centerX": 116.25,
        "centerY": 46.8,
        "isEmpty": false
      }
    }
  ],
  "languages": ["en"],
  "duration": "2.45s",
  "usedGpu": false
}
```

## Configuration

### Python Runtime

EasyOcrSharp uses the pre-bundled Python runtime from `EasyOcrSharp.Runtime`:

- **No Python installation required!** 
- Python 3.11 is pre-bundled in `EasyOcrSharp.Runtime` package
- Used directly from NuGet cache (no copying needed)
- Works out of the box - just use the library
- All required packages are pre-installed: `easyocr`, `torch`, `torchvision`, `pillow`, `numpy`, `opencv-python-headless`

```csharp
// Uses pre-bundled runtime from NuGet cache automatically
await using var ocr = new EasyOcrService(logger: logger);
```

### Cache Location

- **Python Runtime**: Used **directly from NuGet cache** at `%USERPROFILE%\.nuget\packages\easyocrsharp.runtime\<version>\tools\python_runtime` (Windows) or equivalent on Linux/macOS. No copying required - the runtime is accessed directly from the installed package location.
- **Language Models**: Downloaded and cached by default in `%LOCALAPPDATA%\EasyOcrSharp\models` (Windows) or equivalent on Linux/macOS. You can specify a custom path using the `modelCachePath` parameter in the `EasyOcrService` constructor.
- Models are cached permanently after first download
- Language models are downloaded automatically on first use (may take a few minutes depending on network speed)

### Custom Model Cache Path

You can specify a custom directory for downloading and caching language models:

```csharp
// Use a custom path for model downloads
await using var ocr = new EasyOcrService(
    modelCachePath: @"D:\MyApp\Models",
    logger: loggerFactory.CreateLogger<EasyOcrService>());
```

**Benefits of custom model cache path:**
- Store models in a location with more disk space
- Share models across multiple applications
- Use a network drive for centralized model storage
- Better control over model storage location

If not specified, models are cached in `%LOCALAPPDATA%\EasyOcrSharp\models` (Windows) or the equivalent location on Linux/macOS.

## Logging

EasyOcrSharp uses `Microsoft.Extensions.Logging` for diagnostic output. Configure any logging provider (console, file, Serilog, NLog, etc.) to capture logs.

### Console Logging

```csharp
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var ocr = new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());
```

### File Logging

You can use third-party libraries like **Serilog** or **NLog** for file logging:

**Serilog Example:**
```csharp
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.File("easyocrsharp.log")
    .CreateLogger();

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSerilog();
});
var ocr = new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());
```

**NLog Example:**
```csharp
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddNLog();
});
var ocr = new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());
```

### Logging Levels

- **Information**: Initialization, language grouping, GPU detection, OCR completion, model downloads
- **Warning**: Language compatibility issues, GPU detection failures
- **Debug**: Detailed Python initialization, sys.path information

## Known Limitations

### Python 3.13 Incompatibility

Currently, `pythonnet 3.0.5` (used for Python interop) is incompatible with Python 3.13. Python 3.13 removed the `PyThreadState_GetUnchecked` API that `pythonnet` requires.

The pre-bundled runtime in `EasyOcrSharp.Runtime` uses Python 3.11, which is fully compatible.

## Example Application

An example console application is included in the repository under `test/`. To run it:

```bash
dotnet run --project test/Test.EasyOcrSharp.csproj
```

The application provides an interactive menu to:
- Run OCR on images with custom language selection
- View cache location
- Clear console

The application automatically:
- Sets UTF-8 console encoding for proper Unicode display (Hindi, Arabic, etc.)
- Configures JSON serialization to display Unicode characters correctly
- Shows language grouping information in logs

## Platform Support

EasyOcrSharp is cross-platform and supports:
- ✅ **Windows** (x64, ARM64)
- ✅ **Linux** (x64, ARM64)
- ✅ **macOS** (x64, ARM64)

## Performance Tips

1. **GPU Acceleration**: The library automatically detects and uses GPU if available. For best performance, ensure CUDA-compatible GPU drivers are installed.

2. **Language Selection**: Only specify languages you actually need. Processing fewer languages is faster.

3. **Model Caching**: Models are cached after first download. Subsequent runs are much faster.

4. **Parallel Processing**: The library automatically processes language groups in parallel for multilingual images.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## Building from Source

To build the library from source:

```bash
git clone https://github.com/easyocrsharp/EasyOcrSharp.git
cd EasyOcrSharp
dotnet build
```

To create NuGet packages:

```bash
dotnet pack src/EasyOcrSharp/EasyOcrSharp.csproj -c Release
dotnet pack src/EasyOcrSharp.Runtime/EasyOcrSharp.Runtime.csproj -c Release
```

The generated `.nupkg` files will be placed under `src/EasyOcrSharp/bin/Release/` and `src/EasyOcrSharp.Runtime/bin/Release/`.

**Note**: The `EasyOcrSharp.Runtime` package includes the complete Python 3.11 runtime with all required site-packages. Only minimal exclusions are applied (cache files, specific problematic test data paths, and vendor directories with long paths) to ensure all required modules (including `torch.testing`, `numpy._core.tests`, etc.) are included.

## License

Distributed under the MIT License. See [LICENSE](LICENSE) for details.

## Acknowledgments

- [EasyOCR](https://github.com/JaidedAI/EasyOCR) - The underlying OCR engine
- [pythonnet](https://github.com/pythonnet/pythonnet) - Python.NET interop library

## Support

- 📦 **NuGet Package**: [EasyOcrSharp on NuGet](https://www.nuget.org/packages/EasyOcrSharp)
- 🐛 **Issues**: [GitHub Issues](https://github.com/easyocrsharp/EasyOcrSharp/issues)
- 📖 **Documentation**: See this README and inline XML documentation

---

<div align="center">
  Made with ❤️ for the .NET community
</div>
