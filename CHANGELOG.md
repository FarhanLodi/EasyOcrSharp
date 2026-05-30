# Changelog

All notable changes to EasyOcrSharp are documented here.

## 2.1.0

Feature release. Faster, more flexible, and DI-friendly — no breaking changes to the
core `ExtractTextFromImage(path, languages)` call.

### Added
- **Concurrent recognition.** Detected regions are now recognized in parallel
  (`RecognitionOptions.MaxDegreeOfParallelism`, default = processor count). Large multi-line
  images are substantially faster on multi-core machines.
- **New input overloads:** `byte[]`, `ReadOnlyMemory<byte>`, and `Image<Rgb24>` in addition to
  file path and `Stream`.
- **`RecognitionOptions`** to tune a call:
  - `Grouping` — `Word`, `Line` (default), or `Paragraph`.
  - `MinConfidence` — drop low-confidence lines.
  - `AdjustContrast` — toggle the low-confidence contrast-retry pass.
  - `MaxDegreeOfParallelism`.
  - `Region` — restrict OCR to a rectangular sub-region (`OcrRegion.Pixels(...)` or
    `OcrRegion.Fraction(...)`); boxes are reported in original-image coordinates.
- **Perspective de-warping.** Rotated/slanted text boxes are rectified with a homography
  (port of EasyOCR's `four_point_transform`) instead of an axis-aligned crop. Axis-aligned
  boxes keep the previous fast path, so horizontal text is unchanged.
- **Paragraph mode** (`TextGrouping.Paragraph`) merges nearby lines into blocks.
- **Dependency-injection support:** `services.AddEasyOcrSharp(...)` registers
  `IEasyOcrService` as a singleton.
- **More languages:** Tamil (`ta`), Telugu (`te`), Kannada (`kn`), Traditional Chinese (`ch_tra`).
- **Full language coverage:** the registry's per-pack language lists now mirror EasyOCR's exactly,
  so all **86** EasyOCR-supported languages resolve to a recognizer (Greek and Hebrew remain
  unavailable — upstream has no model for them).
- **SHA256 verification** of every model download (added in 2.0.x, now covering all packs).
- **Automated test suite** (xUnit) and **GitHub Actions CI** (build + unit tests on Linux & Windows).

### Changed
- `ExtractTextFromImage` overloads gained an optional `RecognitionOptions options = null`
  parameter (before `CancellationToken`). Existing calls compile unchanged; callers that passed
  a `CancellationToken` positionally should switch to a named argument.
- Pinned `SixLabors.ImageSharp` to the 3.1.x line: ImageSharp 4.x requires a paid, build-time
  license that would be inherited by every consumer of this package.

## 2.0.0

Complete rewrite from a Python+PyTorch bundle to a native ONNX implementation.

- Replaced the ~1.5 GB embedded Python runtime with `Microsoft.ML.OnnxRuntime`; package is now ~3 MB.
- Models (CRAFT detector + per-script CRNN recognizers) download on demand from Hugging Face and
  are cached locally.
- AOT / single-file publish supported.
- 9 script families: Latin, Cyrillic, Arabic, Devanagari, Bengali, Chinese, Korean, Japanese, Thai.
- Optional `EasyOcrSharp.Gpu` package for CUDA acceleration.
- Public API (`EasyOcrService`, `OcrResult`, `OcrLine`, `OcrBoundingBox`) preserved from 1.x.

## Deferred / not planned

- **PDF input** — intentionally not bundled. Rasterizing PDFs needs a native engine (PDFium etc.),
  which conflicts with this package's "no native dependencies beyond ONNX Runtime" design. Decode
  PDF pages to images in your app (e.g. with a PDF library of your choice) and pass them via the
  `Image<Rgb24>`/`byte[]` overloads.
- **INT8/quantized models** — would shrink the ~210 MB generation-1 packs (Arabic, Devanagari,
  Bengali, Thai, Tamil, Traditional Chinese) but risks the accuracy parity with EasyOCR that this
  library targets. Revisit only with a measured accuracy gate.
- **Greek (`el`)** — upstream EasyOCR ships no Greek recognition model, so it cannot be exported.
