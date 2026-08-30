# Changelog

All notable changes to EasyOcrSharp are documented here.

## 3.1.0

Two bodies of work ship together here. A **production-operations pass** — the library can now be *run*,
not just called: you can alert on it, bound it, and prove it works before traffic arrives. And the
**document-structure engine fixes and production-readiness audit** that were prepared as 3.0.1 but never
published; they are released here instead of separately, so 3.0.1 does not exist as a public version.

A minor bump rather than a patch, because the operations work adds public API (three
`EasyOcrServiceOptions` knobs, two exception types, a hosted service, health-check options and the
`EasyOcrDiagnostics` tag constants). Everything is additive: no public type, member or default was removed
or changed incompatibly, every new guard is off by default, and a service constructed exactly as before
behaves exactly as before.

### Added — production operations

- **Metrics you can build an SLO on.** `easyocr.operations`, `easyocr.duration`, `easyocr.lines` and
  `easyocr.pages` are now tagged with `easyocr.operation`, `easyocr.outcome`, `easyocr.provider`,
  `easyocr.languages` and — on failures — `error.type`, so latency and error rate can be sliced by entry
  point, language and CPU-vs-GPU. The constants are public
  (`EasyOcrDiagnostics.TagNames` / `.Outcomes` / `.OperationNames`) so alert rules need no magic strings.
  New saturation instruments: `easyocr.queue.wait`, `easyocr.operations.active`,
  `easyocr.operations.queued`.
- **`MaxConcurrentOperations`, `QueueTimeout`, `OperationTimeout`** on `EasyOcrServiceOptions`, with new
  typed `OcrBusyException` (503-shaped: at the limit and the queue wait elapsed) and `OcrTimeoutException`
  (504-shaped: one operation blew its budget). Both derive `EasyOcrSharpException`, so existing catch-all
  handlers keep working. All three default to off. The gate is taken once per outermost operation; PDF,
  multi-frame and batch runs are gated per page instead, so a long document cannot hold a slot for its
  whole duration.
- **`AddEasyOcrWarmUp(languages)`** — an `IHostedService` that loads models off the startup path, so a host
  that cannot reach the model mirror still starts and reports state rather than crash-looping. Warm-up
  progress is published through the new `EasyOcrWarmUpState`.
- **Deep health check.** `EasyOcrHealthCheckOptions { DeepProbe, ProbeInterval, ProbeTimeout }` runs a tiny
  synthetic page through the real pipeline once, caches the verdict, and reports the execution provider
  that actually resolved. It never triggers a download, and readiness reports not-ready while warm-up is
  still running. The existing constructor and registration keep their exact signatures and behaviour.
- Metrics and spans on the previously **uninstrumented** PDF, searchable-PDF, multi-frame and batch paths.

### Fixed — production operations

- **Operations were only ever counted on the success path**, so a deployment that was failing every request
  reported *zero* operations rather than a 100% error rate — the one shape of failure a dashboard cannot
  see. Failures, cancellations, timeouts and shed load are now all recorded, and classified apart:
  shedding is the concurrency limit working as designed and must not page anyone as a defect, while a
  timeout points at one pathological input rather than broken code.
- **`EasyOcrDiagnostics` reported version `2.2.1`** while the package was 3.x, mislabelling every metric
  and span it emitted. A test now pins it to the csproj.
- **Timeout ceiling was set to the wrong constant, and only failed under load.**
  `SemaphoreSlim.WaitAsync(TimeSpan)` does not validate its argument up front: with a permit free it
  returns on the fast path and never arms a timer, so an over-long `QueueTimeout` survived every
  uncontended dev run and CI pass, then threw `ArgumentOutOfRangeException` out of the first request that
  actually had to queue — the back-pressure path failing precisely when back-pressure existed, handing
  callers an argument exception instead of the `OcrBusyException` they are meant to retry on. The real
  ceiling is `uint.MaxValue - 1` ms (~49.7 days), not `int.MaxValue` ms (~24.8 days); a clamp at the
  smaller value silently converted every configured wait between the two into "queue forever", turning a
  bounded shed into an unbounded queue. `OperationTimeout` had no upper clamp at all and reaches
  `CancelAfter`, which *does* validate eagerly — so an over-long budget threw on every operation, gated or
  not. Both knobs now share one measured ceiling and coerce beyond it to an infinite wait.


Three bugs reported against [PaddleOcrNet](https://github.com/FarhanLodi/PaddleOcrNet/issues) — whose
PP-StructureV3 engine 3.0.0 brought in-tree — apply verbatim to the copy of that engine inside this
package. All three are fixed here, along with the layout work the third one turned up.

A full production-readiness audit followed, covering Unicode and text handling, image decoding and
geometry, concurrency and native resource lifetime, the document-structure subsystem, PDF input and output,
and the public API, CLI and packaging surface. Its results are in the **production-hardening pass** section
below and reach the plain-text OCR path too — most consequentially, images with transparency decoded to a
black page and EXIF-rotated phone photos were read sideways, both returning empty or near-empty text with
no error.

### Fixed — document-structure engine

- **Every document-analysis language pack decoded one character class off**
  ([PaddleOcrNet#1](https://github.com/FarhanLodi/PaddleOcrNet/issues/1)). The per-script PP-OCRv5
  dictionaries (`cyrillic_dict.txt`, `latin_dict.txt`, `arabic_dict.txt`, `devanagari_dict.txt`,
  `korean_dict.txt`, `japan_dict.txt`, `th_dict.txt`, `ppocrv5_el_dict.txt`, `te_dict.txt`, `ta_dict.txt`,
  `ppocrv5_eslav_dict.txt`) begin with an **empty first line** — that line is the CTC blank slot itself,
  and the class the file leaves off is the trailing space. `CharacterDictionary` saw only that the file
  was one class short of the network and prepended a *second* blank, shifting every class by one:
  `ИСТОРИЯ РОССИЙСКОГО ГОСУДАРСТВА` came out as `ЗРСНПЗЮꚟПНРРЗИРЙНВНꚟВНРТГЏПРСБЏ`. `BuildVocab` now
  checks whether the first line is empty and builds `["blank"] + lines[1..] + [" "]` in that case,
  restoring the exact class alignment the models were trained with. The dictionary *files* were correct
  all along and are unchanged, so nothing re-downloads. Affected: Russian and every other Cyrillic
  language, the Latin-script languages routed to the `latin` pack (fr, de, es, it, pt, …), Arabic,
  Devanagari, Korean, Japanese, Thai, Greek, Telugu, Tamil and East Slavic. The default
  Chinese/English/Japanese recognizers on `ppocrv5_dict.txt` were never affected — that file ships as the
  complete class list and took a different branch.
- **Non-ASCII text is no longer escaped in JSON output**
  ([PaddleOcrNet#2](https://github.com/FarhanLodi/PaddleOcrNet/issues/2)). `OcrResult.ToJson()`,
  `StructureResult.ToJson()` and the CLI's `--format json` inherited the System.Text.Json default
  encoder, which escapes everything outside Basic Latin — so Cyrillic, Greek, Arabic, Hebrew, CJK,
  Devanagari … reached the caller as `\uXXXX` sequences: valid JSON, but unreadable in Notepad and
  friends. All three now serialize through `EasyOcrJson.Encoder`
  (`JavaScriptEncoder.Create(UnicodeRanges.All)`) and emit those scripts verbatim. HTML-sensitive
  characters (`< > & ' +`) are still escaped — deliberately not `UnsafeRelaxedJsonEscaping`, since blocks
  carry `TableHtml` that may be embedded in a page. The decoded text is unchanged either way; only
  readability differs. Note the scope: `UnicodeRanges.All` is the Basic Multilingual Plane, so
  supplementary-plane scalars (emoji, CJK Ext-B at U+20000+) remain escaped as surrogate pairs; pass
  `UnsafeRelaxedJsonEscaping` through the new options overload if you need those verbatim too.

### Added — text handling, structure and JSON

- **Right-to-left reading order.** Arabic, Persian, Urdu, Uyghur and Hebrew pages were assembled
  left-to-right: every line was recognized correctly, but the *sequence* was wrong. A two-column Arabic
  page started on the left (last) column, and a form row whose label sits to the right of its value
  concatenated as `value label`. All five ordering sites now take a direction —
  `EasyOcrService.SortLinesByReadingOrder` and its `StructureService` twin, both `ParagraphGrouper`s,
  `StructureTextEngine`'s `sorted_boxes` port, and `XyCutOrderer` — reading the right-most column first and
  breaking row ties on the right-most edge. Keyed on `MaxX` rather than `MinX`, so a short fragment sitting
  to the right of a longer one still leads.
  - `RecognitionOptions.ReadingDirection` and `DocumentAnalysisOptions.ReadingDirection` select it;
    both default to `TextReadingDirection.Auto`, which reads right-to-left when **every** requested
    language is right-to-left. A mixed request such as `["ar", "en"]` stays left-to-right — such a page is
    usually Latin-majority, and guessing wrong there is worse than leaving the established order alone.
    `TextReadingDirection.RightToLeft` forces it for a bilingual page that really does flow right-to-left.
  - `StructureOptions.RightToLeft` is the structure engine's equivalent. When the layout model emits its
    own reading-order index and that is in use, the model's prediction still wins — it was trained on the
    document's real order and already accounts for direction.
  - Left-to-right output is byte-for-byte unchanged: `Auto` resolves to false for every non-RTL language,
    which is the pre-existing code path verbatim.
  - Scope: this orders *boxes*. It is not the Unicode Bidirectional Algorithm, and does not need to be —
    the recognizer already emits each line as a logical-order string, so bidi remains the renderer's job
    and this library's output is correct input for one.

- **`ToJson(JsonSerializerOptions)` overloads** on `OcrResult` and `StructureResult`, for callers who
  want a different encoder, indentation or naming policy. The options are copied onto the
  source-generated context, so the overload stays trim / Native-AOT safe and leaves the caller's options
  instance mutable and reusable. Passing `Encoder = JavaScriptEncoder.Default` restores the pre-3.1.0
  escaping.
- **`EasyOcrSharp.Models.EasyOcrJson.Encoder`**, the exporters' default `JavaScriptEncoder`, exposed so
  callers can reuse it in their own `JsonSerializerOptions`.
- **`DocumentAnalysisOptions.LayoutScoreThreshold`** — the layout-detection confidence floor is now
  configurable ([PaddleOcrNet#3](https://github.com/FarhanLodi/PaddleOcrNet/issues/3)). It was a
  `private const float ScoreThreshold = 0.5f` inside `RtDetrLayoutDetector` and `PicoDetLayoutDetector`,
  so callers could not keep the regions the detector was unsure about (or discard the marginal ones).
  Default is unchanged at `0.5` — the value PaddleX ships in `inference.yml` (`draw_threshold`) for both
  PP-DocLayoutV3 and PP-DocLayout-S/M — and the comparison stays exclusive (`score > threshold`), matching
  PaddleX. `DocumentAnalysisOptions.DefaultLayoutScoreThreshold` exposes the default as a constant.
- **Layout post-processing**, which investigating that report turned up. The detectors emit a fixed top-k
  of candidates with no NMS, so the same area of a page is routinely proposed several times under
  different labels and every candidate reached the caller. A new clean-up chain collapses overlapping
  regions, drops sub-6px slivers, `reference` markers whose text the neighbouring `reference_content`
  block already carries, and whole-page `image` false positives:
  - `FilterOverlappingRegions` (default **true**) — the duplicate collapse. Barely visible at the default
    threshold, since duplicates rarely score that high, and increasingly important as you lower it.
  - `LayoutNms` (default false) — additional non-maximum suppression, 0.6 IoU within a class and 0.98
    across classes.
  - `LayoutUnclipRatio` (default none) — grow each region about its centre before recognition, for tight
    boxes that clip glyphs out of the table/formula crops.
  - `LayoutMergeMode` (default `None`) — keep the enclosing block, the inner blocks, or both, for nested
    regions.
- **Reading order predicted by the model.** PP-DocLayoutV3 emits a reading-order index alongside every
  box that was previously discarded in favour of re-deriving the order geometrically. It now orders the
  blocks, with XY-cut still the fallback for models that emit no such column;
  `DocumentAnalysisOptions.ReadingOrder` selects between them (`Auto` by default, `XyCut` to force the
  geometric orderer).

### Fixed — production-hardening pass

A multi-agent audit of the whole library (Unicode/text, image handling, concurrency and resource lifetime,
document structure, PDF, and the public API/CLI surface) turned up the following. Everything here is a
behaviour fix; no public type, member or default was removed or changed incompatibly.

**Images — silently wrong or empty results**

- **A transparent background decoded to solid black.** RGBA→RGB conversion *discarded* alpha instead of
  compositing it, so a PNG with a transparent background (a logo, an exported diagram, a screenshot) became
  dark glyphs on a black page and OCR returned empty text with no error. Every decode now composites over
  white, and partial alpha is blended rather than taken at full strength. Affects OCR, barcode scanning and
  redaction alike.
- **EXIF orientation was never applied.** A phone photo of a receipt is normally stored landscape with an
  `Orientation` tag of 6; every viewer shows it upright, but the detector received the sideways buffer and
  found almost nothing. Decoding now honours the tag (a no-op when there is no EXIF profile, and lossless
  for the 90° cases). Multi-frame images keep every frame through both steps.
- **Extreme aspect ratios crashed the CRAFT detector.** An image narrower than 1/2560 of its length — a
  10000×3 receipt strip, a 2561×1 rule — truncated a tensor dimension to zero and ONNX Runtime threw
  `Invalid input shape: {0,2560}` out of the whole OCR call. Both edges are floored at 1px and the
  pad-to-32 rounding no longer maps 0 to 0. `CanvasSize` and `MagRatio` are validated too.
- **Detection polygons came out counter-clockwise.** The shoelace winding test had an inverted sign for
  image (y-down) coordinates, so CRAFT line polygons were emitted TL→BL→BR→TR — contradicting the documented
  order, `OcrWord.BoundingPolygon`, and the clockwise polygons the structure engine produces *within the same
  result*. Consumers taking `polygon[0]→polygon[1]` as the text direction got the descender direction.
- **Quads at ~45° collapsed two corners.** The sum/difference corner heuristic is degenerate along a ±45°
  edge, so a diagonal watermark or stamp rectified as a transposed sliver with non-finite sample
  coordinates, or was dropped outright. Replaced with the tie-free x-sort ordering already used by the DB
  post-processor, plus a non-finite sample guard.
- **A single wide crop could cost ~800 MB.** The structure recognizer resized crops to an unbounded width;
  a 4000×8 crop (a table border picked up as a text line) produced a 55M-float output. Capped at 4096px, as
  PaddleOCR and the sibling CRNN engine already do, and the output tensor is no longer copied.
- Deskew no longer rotates a blank page by −15°, and stays inside its documented ±15° range.

**Concurrency and resource lifetime**

- **Layout/table recognizers could be disposed while another thread was using them.** The caches replaced
  their single cached instance when a concurrent call asked for a different model, freeing an
  `InferenceSession` mid-`Run` — an `AccessViolationException` that kills the process, not a catchable
  exception. Both caches are now keyed by model.
- **Character allow/block lists leaked between concurrent calls.** `SvtrRecognizer` stored the per-call
  filter on the shared, cached instance, so one request's digits-only allowlist could be dropped or applied
  to an unrelated request. The filter is now a parameter, never instance state.
- **Streaming disposed the page image out from under in-flight recognition.** Abandoning a stream early
  (`break` in the consumer's loop) left up to `MaxDegreeOfParallelism` tasks reading pixel memory that was
  already returned to the pool. Abandoned work is now awaited before the image is released.
- **`DisposeAsync` never released the handwriting models**, leaking a TrOCR encoder/decoder pair — hundreds
  of MB of native arena — per service. `UnloadHandwritingModels()` is also safe to call concurrently now:
  it waits for in-flight handwriting calls instead of freeing sessions under them.
- Streaming's document pre-processing runs inside the dispose gate, so shutdown can no longer free the
  orientation/unwarp sessions mid-run.
- The batch API no longer disposes its concurrency gate under running tasks on cancellation, and no longer
  orphans its pump when the consumer stops early (which kept OCRing every remaining file into a channel
  nobody was draining).
- **Concurrent model downloads no longer fail hard.** Two processes sharing a cache raced on the final
  `File.Move`; the loser classified the `IOException` as transient, repeated the entire download, failed
  again, and eventually threw — with a valid, verified copy sitting at the destination. Cold start behind a
  load balancer reproduced it reliably.
- Structure-engine image loading goes through the same decode-limit guard as the rest of the library, so the
  decompression-bomb ceiling is no longer header-only there.

**PDF**

- **Page alpha was discarded rather than composited.** `NaiveTransparencyRemover` only rewrites *fully*
  transparent pixels, and the RGB conversion then dropped the channel — so anti-aliased glyph edges
  hard-thresholded to black and, worse, a 20%-opacity watermark rasterized at 100% and OCR read the
  watermark over the real content. Now composited properly over white.
- **WinAnsi characters were destroyed in the standard text layer.** The layer declares `/WinAnsiEncoding`
  but the builder tested for Latin-1, so `’ – — … € ‘ “ ”` — the characters OCR produces most often on
  ordinary English scans — became `?`. `don’t` and `€1,234` were unsearchable, and under the default `Auto`
  mode one curly apostrophe dragged the whole document onto the embedded-font path (a recursive font scan
  and a multi-megabyte `/FontFile2`) for nothing.
- **`/ToUnicode` collisions resolved in the wrong direction.** Where two code points share one glyph — every
  glyph in a symbol font, plus U+00A0/U+0020, U+2126/U+03A9 and the CJK compatibility ideographs — the
  first-seen code point won. A private-use alias no longer displaces the real character.
- A PDF whose page tree is empty is now rejected with a clear error instead of producing a structurally
  invalid `/Count 0 /Kids []` document reported as success.
- The output PDF is written via a temp file and renamed, so a cancellation or a full disk no longer leaves a
  truncated file at the user's path — destroying whatever was there before.
- A page too wide for the JPEG format now raises `PdfProcessingException` naming the DPI to lower, rather
  than an untyped `NotSupportedException` from inside the page handler.

**Document structure**

- Every `reference` region was deleted unconditionally by the default-on overlap filter, on the assumption
  that a neighbouring `reference_content` carried its text. Nothing verified that — and the 23-class PicoDet
  vocabulary has no `reference_content` class at all, so on those models *every* reference region was lost.
  A marker is now dropped only when a `reference_content` region actually overlaps it.
- `LayoutMergeMode.Large` dropped **both** members of a duplicate pair (containment was symmetric for
  near-identical boxes), and the formula protection compared against a label the shipped PP-DocLayoutV3
  model never emits.
- Tables exported as an empty `<table></table>` in the HTML exporter, which validated the recognizer's
  `<html><body>` envelope as a table fragment and always failed.
- The detection-count output is now matched by name and shape, not by CLR type alone — PP-DocLayoutV3's
  instance mask is `int32` too, and selecting it yielded zero layout regions with no error and no log. That
  mask (~48 MB per page) is no longer fetched at all.
- OOXML and XHTML/hOCR/ALTO writers strip the control characters XML 1.0 forbids outright. No escape exists
  for them, and one reaching `word/document.xml` produces a .docx Word refuses to open.
- `colspan`/`rowspan` are clamped in the OOXML table parser, matching the public `TableHtmlParser`; an
  unbounded span was an immediate `OutOfMemoryException`.
- The image-aware DOCX/HTML exporters now fall back to a text placeholder when the supplied image does not
  match the analyzed page, instead of cropping an unrelated region — reachable whenever document orientation
  or unwarp is enabled, since block bounds are in the corrected page's coordinate space.

**Text and API**

- **`FullText` is LF-separated on every platform.** It used `Environment.NewLine` while paragraph grouping
  joined with `\n` and the multi-frame/PDF aggregates with `\n\n`, so a single string carried two different
  line endings on Windows — and `CharacterErrorRate` gained one spurious insertion per line break when
  measured against LF ground truth.
- Public `AnalyzeDocumentAsync` overloads validate their arguments before constructing the analyzer, instead
  of failing deep inside the engine after allocating sessions and possibly downloading models.
- `AddEasyOcrSharp` is idempotent. A library and its host app both calling it used to register two OCR
  singletons while the health check kept reading the first options object — a readiness probe reporting on a
  different cache path and language set than the service actually used.
- **`ModelDownloadOptions.MaxDownloadBytes`** (new, default 512 MB) caps a single model download. The
  checksum is only verified once the bytes are on disk, so an unbounded stream from a misconfigured
  `BaseUrlOverride` could fill the cache disk before verification ran.
- CLI: `scan report.pdf -o report.hocr.html` writes a **file**, not a directory of that name — the command's
  own documented example was broken, because any PDF input forced directory mode regardless of page count.
  Directory mode now follows the destination. A bad `--dpi` exits 2 (the documented usage code) instead of 1,
  is validated even when no PDF is among the inputs, and each failure is reported once rather than twice.
- Corrected two XML docs that stated the opposite of the code: the structure model checksum path is
  fail-**closed** and its checksum table is fully populated. Ported the download-progress double-count guard
  into the structure engine's copy of the download manager.

### Notes

- **`EasyOcrJson.Encoder` covers the Basic Multilingual Plane.** `UnicodeRanges.All` is the BMP, so
  supplementary-plane scalars (emoji, CJK Ext-B at U+20000+) are still written as surrogate escape pairs.
  That is valid JSON which deserializes to the identical string; pass
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` through the new options overload if you need them verbatim
  and are not embedding the JSON in a page. Documented rather than changed, since allowing astral scalars
  requires an `unsafe` encoder subclass.
- **Channel order in the structure engine is unverified.** Its seven tensor builders write RGB planes while
  PaddleOCR's configs decode BGR. Greyscale and black-on-white scans are unaffected (R == G == B), so this
  would only show on colour input — a red stamp, a coloured header. Changing it blind risks a large accuracy
  regression in the other direction, so it is left as-is pending an A/B against colour pages.
- **Searchable-PDF generation buffers the whole document in memory.** An ~800-page colour scan at 300 DPI
  approaches the 2 GB `MemoryStream` ceiling. `MaxPages` (default 5000) therefore bounds pages, not memory;
  streaming the build to the destination is a larger change left for a follow-up.
- The second half of PaddleOcrNet#3 — layout scores clustering near 0.5 where the original Paddle
  inference reports 0.9+ — was **not reproducible**. Checked against the original Paddle inference model
  on the same pages, the scores are identical, so nothing was changed there.

## 3.0.0

**Two dependencies leave: imaging moves to [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp),
and the document-structure engine moves in-tree.** Between them EasyOcrSharp no longer pulls a
split-licensed imaging library at any depth of the graph. No method name, parameter, default or result
shape changed; both are breaking only because types on the public API now come from different
namespaces — `Image<Rgb24>` and friends from EasyImageSharp, and the `AnalyzeDocumentAsync` result
types from `EasyOcrSharp.Structure`.

### Why
The previous imaging library ships under a split licence: free for some uses, chargeable for commercial
ones above a revenue threshold, and from its 4.x line it enforces a build-time licence key that every
downstream consumer inherits. A general-purpose OCR library cannot hand its users that obligation, and
staying pinned to the last freely-licensed 3.1.x line was a dead end — no fixes, no new formats.
EasyImageSharp is MIT, fully managed, AOT- and trimming-friendly, needs no licence key, and is
maintained by the same author as this library.

### Migration
Migration is one find-and-replace across your `using` directives: every `SixLabors.ImageSharp` namespace
— the root plus `.PixelFormats`, `.Processing` and `.Formats.*` — becomes the matching `EasyImageSharp`
one, and nothing else in your code changes.

```csharp
using EasyImageSharp;                 // Image, Image.Load, Color, Rectangle, Size, Point
using EasyImageSharp.PixelFormats;    // Rgb24, Rgba32, Bgr24, Bgra32, L8
using EasyImageSharp.Processing;      // Mutate/Clone: Resize, Rotate, Crop, Grayscale, ...
using EasyImageSharp.Formats.Jpeg;    // JpegEncoder and the other codec options
```

The public surface that carries these types: `IEasyOcrService` / `EasyOcrService`
(`ExtractTextFromImage`, `ExtractTextFromRegionAsync`, `RecognizeFromBoxesAsync`,
`DetectRegionsAsync`, `RecognizeHandwritingAsync`, `AnalyzeDocumentAsync`, the streaming and
multi-frame overloads), `RedactionResult.Image`, `RedactionOptions.FillColor`,
`OcrVisualizationExtensions.DrawAnnotations`, `BarcodeScanner`, and the `Image<Rgb24>` page handlers of
the PDF APIs.

Two API differences you may hit if you also used the old library directly:
- `ImageInfo` exposes `FrameCount` rather than a frame-metadata collection to count.
- WebP is **decode**-only, so there is no WebP encoder. Reading `.webp` input is unaffected.

### Changed
- All decoding, encoding, resizing, rotation, cropping, greyscaling, thresholding, blurring and
  compositing now run on EasyImageSharp. Input format coverage is unchanged or wider: PNG, JPEG
  (baseline, progressive and CMYK), WebP, GIF, BMP, TIFF (including CCITT G3/G4 and JPEG-in-TIFF),
  TGA, Netpbm, QOI and ICO.
- Deskew preprocessing (`PreprocessingOptions.Deskew`) now calls EasyImageSharp's projection-profile
  deskew instead of a hand-rolled copy of the same idea. Same estimator — the rotation that sharpens
  the horizontal projection of the ink — but it scores candidate angles on the ink coordinates instead
  of rotating the whole page once per candidate, refines to 0.1 degrees instead of 0.2, and skips the
  rotation altogether when the best angle is not meaningfully better than no rotation. Straightening
  is equivalent and markedly faster; a straight page is now left untouched rather than resampled.
- Arbitrary-angle rotation resamples bilinearly. Right-angle rotations, crops and the axis-aligned
  region path stay pixel-exact, as before.

### Added
- `ImagingContractTests` pins the imaging behaviour the OCR pipeline depends on: clockwise
  canvas-expanding rotation with a transparent-black fill (which `PatchTransform` and the page
  orientation sweep invert analytically), aspect-preserving `Resize(w, 0)`, greyscale written to all
  three colour channels, and header-only `Identify`. If any of those ever move, these fail before
  accuracy does.

### The structure engine is now part of this package
`AnalyzeDocumentAsync` used to delegate to the third-party `PaddleOcrNet` package, which carried the old
split-licensed imaging library as its own dependency — so it landed in your output folder even though
nothing in EasyOcrSharp called it. That package is gone: the PP-StructureV3 pipeline (layout detection,
table/formula/seal recognition, reading order, and the DB + SVTR text engine it runs on) now lives in
this repository under `EasyOcrSharp.Structure`, built against EasyImageSharp like everything else.

**Migration.** One `using`, and only if you call `AnalyzeDocumentAsync`:

```csharp
- using PaddleOcrNet.Structure;
+ using EasyOcrSharp.Structure;
```

`StructureResult`, `StructureBlock` and `StructureBlockType` keep their names, members and behaviour.
Everything else the old package exposed was engine internals and is now `internal`, which also removes a
second public `OcrResult` / `RecognitionOptions` / `ImageTooLargeException` from the graph — those names
now resolve unambiguously to EasyOcrSharp's own. `StructureBlock.Lines` is now
`IReadOnlyList<EasyOcrSharp.Models.OcrLine>`: the same line type the rest of the API returns, rather than
a look-alike from another assembly.

**Behaviour.** Verified against a golden report of `AnalyzeDocumentAsync` over six fixture pages: block
counts, block types, bounding boxes and reading-order indices are identical, and the ported engine's
output is byte-for-byte identical to running the original engine's own source. Recognized text shifts on
a handful of degraded, low-confidence regions — that is the imaging change above, not the move, and it
is what the same models produce on either code path.

**Models and cache moved.** The structure models are now served from
`huggingface.co/EasyOcrSharp/EasyOcrSharp-models`, the same repository as the OCR models, and are cached
alongside them in `%LOCALAPPDATA%/EasyOcrSharp/models` instead of a separate `PaddleOcrNet` directory.
The model files themselves are byte-identical — only where they are fetched from and stored changed.

Two consequences. Structure models **re-download once** on first use after upgrading; the old
`%LOCALAPPDATA%/PaddleOcrNet` directory is no longer read and can be deleted. And `PP-LCNet_x1_0_doc_ori.onnx`
and `UVDoc.onnx`, which both the document-preprocessing and structure paths use, are now stored once
rather than once per cache.

Mirror and cache overrides answer to `EASYOCRSHARP_STRUCTURE_MODEL_BASE_URL` and
`EASYOCRSHARP_STRUCTURE_CACHE`; the previous `PADDLEOCRNET_*` names are still honoured as a fallback, but
a mirror of the old repository needs the new file set — `AnalyzeDocumentAsync` with seal recognition now
resolves models that were never published before.

**New dependency.** `Clipper2` (polygon offsetting for detection post-processing), previously pulled in
transitively by the removed package.

**Faster.** `AnalyzeDocumentAsync(Image<Rgb24>, ...)` no longer re-encodes the page to PNG in memory and
decodes it again on the other side. That round-trip existed only to bridge two imaging stacks; the engine
now shares this library's pixel types, so a decoded image is passed straight through.

### Dependency
`EasyImageSharp` is pinned at 1.0.1 — a stable release, so the package carries no prerelease dependency.
It is a first-class dependency rather than an implementation detail, because its pixel types are on this
library's public API; it is bumped deliberately, alongside a note here, rather than floated.

### Companion package
`EasyOcrSharp.Gpu` 3.0.0 ships alongside this release and resolves `EasyOcrSharp` 3.0.0. It now
references the core project directly rather than pinning a published package version, so the two are
built and released together instead of the GPU metapackage trailing the core by a version — which is how
its 2.x releases ended up pinned to `EasyOcrSharp` 2.3.2, and therefore to the old imaging dependency.
Upgrade both together.

## 2.3.0

**Thirteen new capabilities: true word/character geometry, Unicode searchable PDF, handwriting, barcodes,
redaction, field extraction, post-OCR correction, accuracy metrics, streaming, multi-frame TIFF, table
export, a `dotnet tool` CLI, and an ASP.NET Core sample.** Everything is additive and opt-in: no public
method was renamed or removed, and no existing default changed — a service configured exactly as before
behaves exactly as before, byte for byte.

### Added — recognition detail
- **Real word- and character-level geometry** derived from the recognizer's CTC alignment rather than
  estimated from character counts. `RecognitionOptions.WordLevelDetail` (`None` | `Words` |
  `Characters`, **default `None`**) populates `OcrLine.Words` and `OcrLine.Characters` with `OcrWord` /
  `OcrChar` records carrying their own polygon, box and confidence. Rotated and skewed lines produce
  genuinely rotated character quads, not axis-aligned approximations.
- **hOCR, ALTO and TSV now emit true word boxes** when word detail is present, falling back to the
  previous proportional-width approximation when it is not. Output is byte-identical to 2.2.4 whenever
  `Words` is empty, which is the default.

### Added — documents
- **Unicode searchable PDF.** The invisible text layer now switches to an embedded Type0 /
  CIDFontType2 font with `Identity-H` encoding, a subsetted `FontFile2` and a `ToUnicode` CMap, so
  Chinese, Japanese, Korean, Arabic, Devanagari, Thai and Greek PDFs are genuinely searchable and
  copy-pasteable. Previously anything outside Latin-1 was written as `?`.
  - `PdfOcrOptions.TextLayerFont` (`Auto` | `Never` | `Always`) and `PdfOcrOptions.TextLayerFontPath`.
  - **No font is bundled** — a CJK font is tens of megabytes. Supply one with `TextLayerFontPath`, or
    let the built-in probe find an installed system font. When no suitable font exists the output
    falls back to the previous Helvetica/WinAnsi layer instead of failing.
  - `PdfOcrResult.TextLayerFontStatus` (`Standard` | `Embedded` | `Unavailable`) reports what actually
    happened, so a pipeline can detect a document that silently lost searchability.
- **Multi-frame TIFF and image sequences.** `ExtractTextFromFramesAsync` returns a
  `MultiFrameOcrResult` (per-frame `FrameOcrResult` with its frame index), and
  `StreamTextFromFramesAsync` yields each frame as it completes. The `MaxImagePixels` guard applies
  per frame, `MaxFrames` bounds the document, and the caller's image is never disposed or mutated.
- **Structure tables as data.** `TableHtmlParser` turns the HTML tables recovered by
  `AnalyzeDocumentAsync` into a rectangular `TableGrid`, and `StructureExportExtensions` adds
  `Tables()`, `ToRows()`, `ToGrid()`, `ToDataTable()`, `ToDataTables()` and RFC 4180 `ToCsv()`.
  `rowspan`/`colspan` are expanded into repeated values; entities are decoded and malformed markup is
  tolerated rather than thrown on.

### Added — new recognition modes
- **Handwriting recognition via TrOCR** — a ViT encoder + autoregressive decoder running on ONNX
  Runtime, with greedy and beam decoding, a byte-level BPE tokenizer, and `MaxTokens` runaway
  protection. Configured through `EasyOcrServiceOptions.Handwriting` (`HandwritingOptions`, null =
  off) and used via `RecognizeHandwritingAsync`. The models are **hosted alongside the printed-text
  packs and download on first use**, checksum-verified like the rest — `HandwritingOptions.Default` is
  all that is needed. `Quantize` (default `true`) selects the int8 weights (~520 MB) over full
  precision (~1.5 GB). Setting `EncoderModelPath` / `DecoderModelPath` / `TokenizerPath` uses your own
  export instead and downloads nothing; `tools/export_trocr_onnx.py` produces a compatible set from any
  HuggingFace TrOCR checkpoint. Weights are an ONNX export of the MIT-licensed
  `microsoft/trocr-base-handwritten`.
- **Barcode and QR reading** (ZXing.Net) via `BarcodeScanner.ReadBarcodesAsync` and matching
  `IEasyOcrService` extensions, plus a combined text-and-barcode pass. `BarcodeOptions` controls
  formats, `TryHarder`, `MultipleCodes`, `TryInverted`, `AutoRotate` and a region restriction. ZXing
  types never appear in the public surface — `BarcodeFormat` is the library's own enum.

### Added — post-processing
- **Redaction.** `RedactAsync` / `RedactPdfAsync` find text by regex, keyword or predicate and
  permanently paint over it — the pixels are destroyed, not merely covered by an annotation — filling
  the rotated quad rather than an axis-aligned box. `RedactionStyle` offers `FilledBox`, `Blur` and
  `Pixelate`; `RedactionScope` redacts a whole line or only the matched words. `RedactionPatterns`
  ships validated presets: `Email`, `Phone`, `CreditCard` (**Luhn-checked**), `Iban` (**mod-97**),
  `UsSocialSecurityNumber` and `LongDigitRun`.
- **Post-OCR correction.** `OcrResult.Correct(...)` applies SymSpell-style lexicon correction weighted
  by common OCR confusions (0/O, 1/l/I, 5/S, 8/B, rn/m). Crucially it is gated on
  `MinConfidenceToCorrect`, so confident text is never rewritten. `FieldNormalizers` validate and,
  where a checksum permits, repair dates, currency, IBANs and ICAO 9303 MRZ lines.
- **Anchor-based field extraction.** `ExtractFields` / `ExtractFieldValues` locate a label and read its
  value from the surrounding geometry — `Right`, `Below`, `Left`, `Above`, `SameLine` — with
  resolution-independent distances, fuzzy anchor matching that survives OCR damage, and a geometry-free
  `"Total: 42.00"` fallback. `FieldPresets` covers invoice and receipt fields.
- **Accuracy metrics.** `EasyOcrSharp.Evaluation.TextAccuracyMetrics` provides `CharacterErrorRate`,
  `WordErrorRate` and a full `Compare` breakdown, over raw strings or an `OcrResult`, with opt-in
  normalization and a configurable `CharacterUnit`.

### Added — integration surfaces
- **Streaming results.** `IEasyOcrService.ExtractTextStreamAsync` returns `IAsyncEnumerable<OcrLine>`,
  yielding lines as regions finish instead of buffering the whole page. Declared with a
  default interface implementation, so existing mocks and custom implementations keep compiling.
- **`easyocrsharp` CLI** (`src/EasyOcrSharp.Cli`, packaged as a `dotnet tool`): `scan` (images, PDFs,
  globs and folders, to text/JSON/hOCR/ALTO/TSV), `pdf` (searchable PDF), `models pull|list|path` for
  air-gapped deployment, and `info`. No third-party argument-parsing dependency.
- **ASP.NET Core sample + Dockerfile** under `samples/EasyOcrSharp.WebApi`: `POST /ocr`,
  `POST /ocr/pdf`, `GET /health` and a browser upload page, with bounded concurrency, upload limits,
  problem-details errors and a model-cache volume.

### Fixed
- **PDF rasterization is now thread-safe.** Docnet exposes PDFium through a process-wide
  `DocLib.Instance` singleton and PDFium is not thread-safe, but the rasterizer called into it without
  any synchronization — so OCR-ing two PDFs at once (a batch, or one web request per document) could
  return a corrupted page or tear down the native library. Every PDFium call is now serialized behind
  a process-wide gate. The OCR itself stays outside the gate, so documents still overlap and
  throughput is essentially unchanged.

### Dependencies
- Added **ZXing.Net** (barcode and QR decoding).

## 2.2.4

**Document-structure analysis (layout, tables, formulas, seals) + document sharpen/orientation/unwarp
preprocessing.** Everything is additive and opt-in: no public method changed, no existing default
changed — a service configured exactly as before behaves exactly as before.

### Added — document structure & tables (`AnalyzeDocumentAsync`)
- **`IEasyOcrService.AnalyzeDocumentAsync`** (file / `Stream` / `byte[]` / `ReadOnlyMemory<byte>` /
  `Image<Rgb24>` overloads, default-implemented on the interface so custom implementations and mocks
  keep compiling): full PP-StructureV3 document analysis — layout regions, **tables recovered as
  HTML**, formulas as LaTeX, seal/stamp text, and reading order — returning a `StructureResult` with
  typed blocks plus `ToMarkdown()` / `ToJson()` exporters.
- **`DocumentAnalysisOptions`**: `DocumentOrientation` / `DocumentUnwarp` page correction,
  `RecognizeTables` / `RecognizeFormulas` / `RecognizeSeals` toggles (default on), `TableModel`
  (`DocumentTableModel.SlanetPlus` default, `SlaNeXt` for higher accuracy), and `Languages`
  (PaddleOCR codes; the default pack covers Chinese + English + Japanese).
- Powered by the **`PaddleOcrNet`** package (new dependency): the analyzer is created lazily on the
  first `AnalyzeDocumentAsync` call, shares the service's execution provider, thread limits, cache
  path (when set), pixel-flood guard and download-resilience settings, and is disposed with the
  service. Plain OCR calls never load any structure model.

### Added — document preprocessing
- **`PreprocessingOptions.Sharpen`** + **`SharpenAmount`** (default 1.0): unsharp-mask sharpening for
  soft, low-DPI or slightly out-of-focus scans. Runs after denoise/deskew, before binarize.
- **`PreprocessingOptions.DocumentOrientation`**: corrects whole-page 90°/180°/270° rotation with the
  PP-LCNet document-orientation classifier — a single tiny model pass instead of
  `DetectOrientation`'s 4× OCR sweep.
- **`PreprocessingOptions.DocumentUnwarp`**: dewarps curved/folded pages (photographed book pages,
  creased receipts) with the UVDoc model before OCR.
- Both document models download lazily on first use with fail-closed SHA256 verification and are
  never touched otherwise.

### Fixed
- **`DetectOrientation` coordinate space.** When the orientation sweep chose a 90°/180°/270° rotation,
  bounding boxes were returned in the *rotated* frame while `SourceWidth`/`SourceHeight` reported the
  original (un-rotated) dimensions — so a consumer normalizing box coordinates by the reported size
  mis-placed them on non-square pages. Boxes (and their polygons) are now mapped back onto the original
  image, keeping them in the same coordinate space as `SourceWidth`/`SourceHeight` and matching EasyOCR,
  which rotates coordinates back after its orientation sweep.

### Dependencies
- Added `PaddleOcrNet` 1.0.0 (document-structure engine behind `AnalyzeDocumentAsync`).

## 2.2.3

Hardening, performance, accuracy, and thread-safety pass from a full technical review. **No public method
was renamed and no existing API was removed** — every change is additive or a safer default. A few
defaults change observable behaviour (noted below); set the new options back if you need the old behaviour.

### Dependencies
- ONNX Runtime `1.26.0` → **`1.27.0`**.
- `Microsoft.Extensions.*` (DI Abstractions, Diagnostics.HealthChecks, Logging.Abstractions) `10.0.8` → **`10.0.9`**.

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
- Pinned the imaging dependency to its last freely-licensed line: its 4.x releases require a paid,
  build-time licence that would be inherited by every consumer of this package. (Superseded in 3.0.0,
  which drops that dependency entirely.)

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
