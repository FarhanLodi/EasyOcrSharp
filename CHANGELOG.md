# Changelog

All notable changes to EasyOcrSharp are documented here.

## 2.2.2

Hardening, performance, accuracy, and thread-safety pass from a full technical review. **No public method
was renamed and no existing API was removed** — every change is additive or a safer default. A few
defaults change observable behaviour (noted below); set the new options back if you need the old behaviour.

### Security
- **Image decompression-bomb / pixel-flood guard.** Untrusted images are rejected from their header
  *before* the pixels are decoded when the decoded pixel count exceeds
  `EasyOcrServiceOptions.MaxImagePixels` (default **100 MP**; set `0` to disable) — throws
  `ImageTooLargeException`.
- **PDF size guards.** `PdfOcrOptions.MaxPages` (default **5000**) and `MaxPageMegapixels` (default **200**)
  reject oversized documents before rendering, preventing unbounded memory/CPU from a hostile PDF.
- **HTTPS-only model source.** A non-`https://` `BaseUrlOverride` / `EASYOCRSHARP_MODEL_BASE_URL` is now
  refused unless `ModelDownloadOptions.AllowInsecureModelSource = true`.
- **Fail-closed integrity.** A downloaded model with no known SHA256 checksum is rejected unless
  `ModelDownloadOptions.AllowUnverifiedModels = true`. Model file names are validated as a single path
  segment (anti-traversal).

### Fixed — correctness & thread-safety
- **Recognizer cache no longer poisoned by cancellation.** A per-caller `CancellationToken` is no longer
  captured into the shared model-load task, so one caller cancelling can't fail the language pack for
  every other caller. A genuinely failed load is now evicted and retried on the next call (was cached as
  a permanent failure).
- **Safe disposal under load.** `DisposeAsync` drains in-flight OCR operations before releasing the ONNX
  sessions, preventing a native use-after-free when a service is disposed while a request is in flight.
  Double-dispose is a no-op.

### Performance
- ONNX outputs are drained via the contiguous `Buffer.Span` instead of the per-element strided tensor
  indexer (recognizer and detector) — markedly faster output extraction.
- `PerspectiveWarp` copies only a region's bounding box, not the whole page, for each rotated box.
- On CPU, per-box recognizer sessions default to a single intra-op thread (the box-level `Parallel.For`
  supplies the parallelism), avoiding thread-pool oversubscription; the detector keeps full intra-op
  parallelism for its single large run.
- Per-box scratch buffers are pooled (`ArrayPool`).
- **`IEasyOcrService.WarmUp(languages)`** preloads the detector and recognizer packs so the first real
  request doesn't pay model-download + session-init latency.

### Accuracy
- **Reading order** is now column-aware and bands rows by a line-height-relative tolerance (was a fixed
  10 px) — fixes line ordering on large headings, high-DPI scans, and multi-column pages.
- **IoU NMS** de-duplicates overlapping detected boxes (`DetectionOptions.NmsIouThreshold`, default
  **0.6**; `0` disables).
- **Multi-language requests** bias toward the page's dominant script, so an over-confident wrong-script
  recognizer pack can't hijack individual boxes.
- Confirmed the greedy-decode confidence (`custom_mean`) matches EasyOCR's reference (averaged over
  non-blank timesteps) and pinned it with a regression test.

### Added — API (additive, non-breaking)
- Typed exceptions deriving `EasyOcrSharpException` (catch-all keeps working): `ModelDownloadException`,
  `ModelChecksumException`, `OfflineModelMissingException`, `PdfProcessingException`,
  `ImageTooLargeException`.
- `OcrResult.SourceWidth` / `SourceHeight` (the dimensions OCR ran on; handy for exporters).
- PDF file reads (`ExtractTextFromPdfAsync(path)`, `CreateSearchablePdfAsync(inputPath, …)`) are now fully
  async and honour the `CancellationToken`.

### Tests & CI
- New CI-safe unit tests: column/font-aware reading order, IoU NMS, the confidence-formula pin, region→
  image coordinate translation, disposal semantics, the image-size guard, and the hardened download path
  (checksum mismatch, retry/backoff, HTTP-range resume, HTTPS-only, fail-closed, file-name traversal).
- New **CER/WER accuracy harness** (`TextMetrics` + a fixture-driven, ground-truth-gated regression test).
- CI now also runs on **macOS** and adds an informational **Native AOT publish** smoke job.

### Behaviour changes to be aware of
- IoU NMS and the new reading order slightly change box de-duplication and line ordering versus 2.2.1.
- The new image/PDF size guards may reject very large *untrusted* inputs — raise `MaxImagePixels` /
  `MaxPages` / `MaxPageMegapixels` if you legitimately process larger inputs.
- A custom mirror over plain HTTP, or serving models without a registry checksum, now needs the explicit
  `AllowInsecureModelSource` / `AllowUnverifiedModels` opt-ins.

## 2.2.1

Patch release — robustness and clearer errors. **No API changes.**

### Fixed
- **Clear errors for bad PDFs.** Malformed, corrupt, truncated, empty, or password-protected PDFs
  passed to `ExtractTextFromPdfAsync` / `CreateSearchablePdfAsync` now raise a typed
  `EasyOcrSharpException` with an actionable message and the underlying cause preserved as
  `InnerException` — instead of leaking a raw PDFium/Docnet exception. PDF-rendering failures and
  genuine OCR failures are kept on separate paths so they're never mislabeled.

### Added
- **Robustness/edge-case test coverage** — malformed/corrupt/empty/truncated PDFs, blank / tiny /
  large images, non-PNG formats (BMP / TIFF / JPEG / WebP), the searchable-PDF text layer, and
  cancellation + concurrent OCR.

### Notes
- The searchable-PDF **invisible text layer** is Latin-1 (WinAnsi base-14 font); non-Latin glyphs are
  written as `?` there. The OCR result and the hOCR / ALTO / TSV / JSON exporters keep full Unicode —
  only the embedded PDF text layer is affected.

## 2.2.0

Production + EasyOCR-parity release. **All additive — every existing API (`ExtractTextFromImage`,
`OcrResult`, `OcrLine`, DI registration) is unchanged; new options default to the previous behaviour.**

### Added
- **Automatic GPU detection** — `ExecutionProvider` now defaults to `OcrExecutionProvider.Auto`, which
  probes ONNX Runtime (`OrtEnv.GetAvailableProviders`) at startup and uses the best accelerator the host
  actually has — CUDA on Windows / Linux, CoreML on macOS — falling back to CPU when none is installed.
  The choice follows whichever provider package is referenced (`EasyOcrSharp.Gpu` for CUDA); the base
  package stays CPU. If an accelerated session fails to initialize at the first model load, the engine
  downgrades to CPU and retries instead of throwing. `ocr.UseGpu` reports whether an accelerator was
  selected. Back-compatible: explicit providers and the legacy `useGpu: true` flag (forces CUDA) are
  unchanged.
- **GPU upgrade hint** — when `Auto` falls back to CPU but a GPU is physically installed, EasyOcrSharp
  detects it (reading the Windows display-adapter registry) and names the **exact** provider package to
  add — `EasyOcrSharp.Gpu` for NVIDIA. Logged once at startup and
  exposed as `EasyOcrService.GpuAccelerationHint` (null when a GPU is already in use, CPU was chosen
  explicitly, or no GPU was found). A NuGet package can't be added at runtime, so this is the closest
  thing to "auto-enable": the library tells the user precisely which one package to install.
- **Beam-search decoding** on `RecognitionOptions` (EasyOCR's `decoder`):
  - `Decoder` (`DecoderType.Greedy` | `BeamSearch` | `WordBeamSearch`) and `BeamWidth`.
  - `Dictionary` — lexicon for word beam search; constrains output to known words (falls back to plain
    beam search when empty). Decoding lives in a unit-tested `CtcDecoder`.
- **Per-box rotation** — `RotationInfo` (e.g. `[90, 180, 270]`): each detected box is also recognized at
  the listed angles and the highest-confidence reading wins (EasyOCR's `rotation_info`).
- **Recognize-from-boxes** — `RecognizeRegionsAsync(image, regions, languages, ...)` runs recognition on
  caller-supplied region polygons (or `DetectedRegion`s), skipping detection (EasyOCR's `recognize()`).
- **Batched inference** — `BatchSize` (EasyOCR's `batch_size`) feeds multiple boxes through the
  recognizer in one ONNX run; transparently falls back to per-box if the model can't batch.
- **Custom recognizers** — `EasyOcrServiceOptions.CustomRecognizers` registers locally exported CRNN
  ONNX models (with inline `Characters` or a `VocabPath`) for chosen language codes; they take
  precedence over the built-in pack and are loaded from disk, never downloaded (EasyOCR's
  custom `recog_network`).
- **Exposed grouping thresholds** — `RecognitionOptions.GroupingOptions` (`GroupingOptions`): EasyOCR's
  `slope_ths`, `ycenter_ths`, `height_ths`, `width_ths`, `add_margin`, and paragraph `x_ths` / `y_ths`.
- **Exposed contrast thresholds** — `ContrastThreshold` (`contrast_ths`) and `AdjustContrastTarget`
  (`adjust_contrast`) on `RecognitionOptions`.
- **Quantized recognizers** — `EasyOcrServiceOptions.Quantize` (EasyOCR's `quantize=True`) fetches the
  int8 `<pack>.int8.onnx` recognizer variants instead of the float ones. Produced by the new
  `tools/quantize_onnx.py`. Note: ONNX Runtime's CPU provider only int8-quantizes the matmul/linear
  layers (not the BiLSTM/conv), so savings are vocab-dependent — meaningful for large-vocabulary packs
  (CJK), small for the rest. The detector stays float (as in EasyOCR). Opt-in; float is the default.
- **PDF support** (built into the main `EasyOcrSharp` package — no separate package):
  - `ExtractTextFromPdfAsync(...)` — OCR a scanned PDF page-by-page (PDFium rasterization), returning
    per-page `OcrResult`s. Pages are processed one at a time to keep memory low.
  - `CreateSearchablePdfAsync(...)` — write a **searchable PDF**: the original pages with an invisible,
    selectable OCR text layer. Self-contained (base-14 Helvetica, no font files required).
  - `PdfOcrOptions` — DPI and JPEG quality with per-page `IProgress`.
- **Document exporters** on `OcrResult` (`EasyOcrSharp.Export`): `ToHocr()`, `ToAlto()` (ALTO XML v4),
  `ToTsv()` (Tesseract-style), and `ToJson()` (AOT-safe via a source-generated `EasyOcrJsonContext`).
- **Accuracy control** on `RecognitionOptions`:
  - `Allowlist` / `Blocklist` — restrict recognized characters (e.g. digits-only for amounts/IDs).
  - `Detection` (`DetectionOptions`) — exposes CRAFT thresholds (`TextThreshold`, `LinkThreshold`,
    `LowText`, `MagRatio`, `CanvasSize`, `MinSize`).
- **Detection-only API** — `DetectRegionsAsync(...)` returns located regions (`DetectedRegion`) without
  recognition, for layout analysis / redaction / field cropping.
- **Visualization** — `image.DrawAnnotations(result)` returns an annotated copy with region outlines
  (no extra dependency).
- **Observability** (`EasyOcrSharp.Diagnostics.EasyOcrDiagnostics`): OpenTelemetry-ready metrics
  (`Meter` "EasyOcrSharp": operations, duration, lines, model loads/bytes) and tracing (`ActivitySource`
  "EasyOcrSharp"), plus `AddEasyOcrHealthCheck(...)`.
- **Resilient model downloads** (`ModelDownloadOptions`): retry with exponential backoff, resumable
  (HTTP range) downloads, `IProgress<ModelDownloadProgress>`, custom `HttpClient`/proxy factory,
  per-mirror `BaseUrlOverride`, and a strict `Offline` mode that fails fast in air-gapped setups.
- **Thread tuning & explicit providers** (`EasyOcrServiceOptions`): pin `ExecutionProvider`
  explicitly (`Cpu`/`Cuda`/`CoreMl`) when you don't want `Auto`, plus `IntraOpNumThreads` /
  `InterOpNumThreads` to cap ONNX Runtime CPU use.
- **Batch API** — `service.ExtractTextFromImagesAsync(paths, languages, maxConcurrency)` streams
  `OcrBatchResult`s with bounded concurrency; per-image failures are captured, not thrown.
- **New constructor** `EasyOcrService(EasyOcrServiceOptions, ILogger?)` for the options above; the
  legacy `EasyOcrService(modelCachePath, logger, useGpu)` constructor is unchanged.

### Fixed
- **Crash on thin detection boxes.** Noisy scans produce ultra-thin/sliver regions whose width, once
  resized to the recognizer's 64px height, collapsed to 1–2px and made ONNX Runtime throw
  `Invalid input shape` (aborting the whole page). Narrow crops are now edge-padded to a safe minimum
  width (EasyOCR's `NormalizePAD` behaviour), and a single box that still fails inference is skipped
  rather than crashing the page. Surfaced by real multi-page scanned PDFs.

### Notes
- **`batch_size`** is wired (`BatchSize`) but the hosted recognizers export with batch fixed at 1
  (torch 1.10.x can't trace this BiLSTM with a dynamic batch axis), so it transparently falls back to
  per-box inference today. A dynamic-batch re-export needs a newer PyTorch and is deferred.
- **`detect_network='dbnet18'`** (alternative detector) is not implemented: it needs a DBNet ONNX
  export (compiling EasyOCR's deformable-conv op) plus a dedicated DBNet post-processor on the C# side.
  Deferred as a focused follow-up.

## 2.1.1

### Added
- **Scanned-document preprocessing** via `RecognitionOptions.Preprocessing`
  (`PreprocessingOptions`):
  - `Deskew` — auto-correct small skew angles (±15°) using a projection-profile estimate.
  - `DetectOrientation` — detect & correct 90°/180°/270° page rotation by scoring OCR at all four
    orientations.
  - `Binarize` — adaptive (local) thresholding for uneven lighting / faint print.
  - `Denoise` — light blur to suppress scanner speckle.
- **Automatic language detection** — set `RecognitionOptions.AutoDetectLanguage = true` (no language
  codes needed), or call `DetectLanguagesAsync(...)` directly. Samples the largest regions, scores
  candidate script packs by confidence, and uses the winner(s). Candidates default to a common set
  and are configurable via `AutoDetectCandidates`.

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
