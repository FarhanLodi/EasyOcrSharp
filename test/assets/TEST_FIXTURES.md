# Test fixtures guide

Integration tests look for sample documents in `test/assets/pdf/` and `test/assets/images/`. Files are
matched by **keyword in the filename** (case-insensitive), so descriptive names are fine. A missing
fixture **skips** its test (never fails), so the suite stays green.

## Current PDF fixtures (`test/assets/pdf/`)

| File | Scenario it verifies | Test |
|---|---|---|
| `README.pdf` | long multi-section document | `ComplexDocumentTests` |
| `invoice_778899.pdf` | table layout + digit allowlist (`INVOICE`, `778899`, `4821`) | `ComplexDocumentTests` |
| `Quantum_Harvest_Magazine_Article.pdf` | multi-column layout | `ComplexDocumentTests` |
| `bilingual_welcome.pdf` | mixed scripts en+ru (`WELCOME`, Cyrillic `Б`/`Ж`) | `ComplexDocumentTests` |
| `Monthly_Report_With_Image.pdf` | text next to an embedded image (`MONTHLY`, `FIGURE`) | `PdfWithImagesTests` |
| `PrinceCatalogue.pdf` | **multi-page catalogue with many product images** (`FURNITURE`, `LOREM`, `299`) | `PdfWithImagesTests` |
| `PublicWaterMassMailing.pdf` | **real 8-page noisy government scan** (`SAMPLE`, `PUBLIC`) | `ScannedPdfTests` |

Scanned image-only and image+text PDFs are also tested **without any file** — `ScannedPdfTests` and
`PdfWithImagesTests` synthesize them in-process from `sample.png`.

## Robustness / edge-case suites (no fixture needed)

These run automatically and need no external files:

| Suite | What it guards |
|---|---|
| `PdfRobustnessTests` | corrupt / not-a-PDF / truncated / empty / null PDFs fail with a typed `EasyOcrSharpException` (never a raw Docnet/PDFium exception). Drop an `encrypted_*.pdf` to also exercise the encrypted path. |
| `ImageEdgeCaseTests` | blank, solid-black, 1×1 / degenerate, and large-upscaled images don't crash; non-PNG formats (BMP/TIFF/JPEG/WebP) decode and read. |
| `SearchablePdfUnicodeTests` | the data model + JSON keep full Unicode; the searchable-PDF **invisible text layer is Latin-1 only** — non-Latin glyphs become `?` there (documented limitation). |
| `CancellationConcurrencyTests` | `CancellationToken` is honored (pre-canceled + mid-document); the service is correct under concurrent image and PDF calls. |

## Generation prompts (if you want to regenerate)
- **invoice** → "One-page invoice PDF. Heading **INVOICE**, line **INVOICE NUMBER 778899**, a 4-row
  line-item table, bold **GRAND TOTAL 4821**."
- **two-column** → "One-page two-column article PDF, centered bold title **QUANTUM HARVEST**, a few
  paragraphs per column."
- **bilingual** → "One-page bilingual PDF: English **WELCOME GUESTS** on top, Russian
  **ДОБРО ПОЖАЛОВАТЬ** below."
- **report+image** → "One-page report PDF: heading **MONTHLY REPORT**, a paragraph, an embedded
  photo/chart, and under it a caption **FIGURE 12 SALES**."

## Notes / known OCR behaviours (not bugs)
- **Multi-column reading order:** side-by-side columns on the same horizontal band are read left→right
  (interleaved), not column-by-column. Content is captured; cross-column order isn't separated.
- **Latin/Cyrillic homoglyphs:** with both `en` and `ru` packs active, identical glyphs (О/0/O, Р/P,
  А/A, В/B, Т/T) are ambiguous. Use a single language pack when the script is known.
