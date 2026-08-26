# `EasyOcrSharp.Structure` — the document-structure engine

Everything behind `IEasyOcrService.AnalyzeDocumentAsync`: PaddleOCR's PP-StructureV3 pipeline —
document pre-processing (orientation, unwarp) → layout detection → per-region table / formula / seal
recognition → reading order → `StructureResult`. Text inside text-like regions is recognized by the
DB + SVTR engine under `Engine/`, which is separate from the CRAFT + CRNN engine the rest of the
library uses for plain OCR.

## Provenance

Ported from [PaddleOcrNet](https://github.com/FarhanLodi/PaddleOcrNet) (same author, MIT) in 3.0.0,
when that package stopped being a dependency. The port was verbatim: namespaces were rewritten and
duplicated model types were folded onto `EasyOcrSharp.Models`, but no pipeline logic was changed. It
was accepted only once a golden report of `AnalyzeDocumentAsync` over the fixture pages came back
byte-for-byte identical to the original engine's own source build.

**Fixes made upstream in PaddleOcrNet do not arrive here on their own.** If you change something in
this tree that is also a bug there, port it across by hand, and vice versa.

Types carrying the upstream `Paddle` prefix were renamed, since nothing in this package is
Paddle-branded. When diffing against upstream, map:

| Upstream (PaddleOcrNet) | Here |
|---|---|
| `PaddleStructureEngine` | `StructureEngine` |
| `PaddleOcrEngine` | `StructureTextEngine` |
| `PaddleOcrService` / `IPaddleOcrService` | `StructureService` / `IStructureService` |
| `PaddleOcrServiceOptions` | `StructureServiceOptions` |
| `PaddleEngineOptions` | `StructureEngineOptions` |
| `PaddleModelRegistry` | `StructureModelRegistry` |
| `PaddleOcrDiagnostics` | `StructureDiagnostics` |
| `PaddleOcrException` | `StructureEngineException` |
| `PaddleOcrJsonContext` | `StructureEngineJsonContext` |
| `IPaddleDetector` | `ITextDetector` |

`PaddleOCR` and `PaddleX` in comments still refer to the upstream *project* and are left alone, as are
the model repository name and cache directory (see below).

## Layout

| Path | What it is |
|---|---|
| `*.cs` | Public results (`StructureResult`, `StructureBlock`, `StructureBlockType`) and the pipeline coordinator |
| `Layout/`, `Table/`, `Formula/`, `Seal/` | Per-region recognizers |
| `Preprocess/`, `ReadingOrder/`, `Export/` | Page rectification, XY-cut ordering, Markdown/HTML/DOCX/XLSX output |
| `Engine/` | The DB + SVTR text engine, model download/cache, geometry helpers — all `internal` |

Only `StructureResult`, `StructureBlock` and `StructureBlockType` are public. Everything else is
`internal` on purpose: this tree carries its own `OcrResult`, `RecognitionOptions` and exception types
that would otherwise collide by name with EasyOcrSharp's own public ones.

## Models

Fetched on first use from the `PaddleOcrNet/PaddleOcrNet-models` HuggingFace repository, SHA-256
verified fail-closed, and cached in `%LOCALAPPDATA%/PaddleOcrNet/models`. The repository and cache
directory keep those names deliberately — same files, same source, so an upgrade re-downloads nothing.
Override with `EASYOCRSHARP_STRUCTURE_MODEL_BASE_URL` / `EASYOCRSHARP_STRUCTURE_CACHE` (the older
`PADDLEOCRNET_*` names still work).
