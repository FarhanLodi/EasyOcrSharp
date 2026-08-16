# EasyOcrSharp — ASP.NET Core sample

A minimal-API OCR service built on [EasyOcrSharp](../../README.md). It is deliberately closer to
production than to a toy: bounded concurrency, upload limits, the library's own decompression-bomb and
PDF page guards, problem-details errors, a real health check, and a Dockerfile with the native
prerequisites already sorted out.

## Run it locally

```bash
dotnet run --project samples/EasyOcrSharp.WebApi
# then open http://localhost:5000
```

The first request downloads the recognizer models (a few hundred MB depending on language) into the
model cache. Set `EasyOcr:WarmUpOnStart=true` to do that in the background at startup instead, so the
first real request is fast.

## Endpoints

| Method | Path | Notes |
|---|---|---|
| `GET`  | `/` | A tiny HTML page with a file picker that posts to `/ocr`. |
| `POST` | `/ocr` | `multipart/form-data` with a `file` part. Query: `lang=en,fr`, `format=json\|hocr\|alto\|tsv\|text`. |
| `POST` | `/ocr/pdf` | Upload a scanned PDF, get a **searchable** PDF back. Query: `lang=`, `dpi=`. |
| `GET`  | `/health` | Reports whether the model cache is present and ready. |

```bash
# plain text
curl -X POST "http://localhost:5000/ocr?lang=en&format=text" -F "file=@receipt.png"

# hOCR, for a document pipeline
curl -X POST "http://localhost:5000/ocr?lang=en&format=hocr" -F "file=@page.png" -o page.hocr

# make a scanned PDF searchable
curl -X POST "http://localhost:5000/ocr/pdf?lang=en&dpi=200" -F "file=@scan.pdf" -o searchable.pdf

curl http://localhost:5000/health
```

`/ocr` sets `X-Ocr-Lines` and `X-Ocr-Duration-Ms`; `/ocr/pdf` sets `X-Ocr-Pages` and
`X-Ocr-Font-Status` — the last tells you whether the invisible text layer embedded a Unicode font or
fell back to Latin-1 because no suitable font was installed.

## Docker

Build from the **repository root** so the library sources are in the build context:

```bash
docker build -f samples/EasyOcrSharp.WebApi/Dockerfile -t easyocrsharp-webapi .
docker run --rm -p 8080:8080 -v easyocr-models:/models easyocrsharp-webapi
```

The `/models` volume keeps the downloaded models across container restarts. For an air-gapped
deployment, populate that volume ahead of time (`easyocrsharp models pull en,fr` writes to the same
cache layout) and start the container with `-e EasyOcr__Offline=true` so a missing model is a loud
error rather than a silent egress attempt.

## Configuration

Everything lives under the `EasyOcr` configuration section — environment variables use `__` as the
separator, e.g. `EasyOcr__MaxConcurrentOcr=4`. See [`WebApiOptions.cs`](WebApiOptions.cs) for the full
set with documentation; the ones worth knowing:

| Key | Default | Purpose |
|---|---|---|
| `AllowedLanguages` | `en,fr,de,es,it,pt,nl` | Whitelist — an unbounded `?lang=` is a DoS lever. |
| `MaxConcurrentOcr` | half the CPU count | Admission control; excess requests queue then 503. |
| `QueueTimeoutSeconds` | `15` | How long a request waits for a slot before being shed. |
| `MaxUploadBytes` | 25 MB | Enforced by Kestrel *and* the multipart reader. |
| `MaxImagePixels` | 40 MP | Decompression-bomb guard, checked from the image header. |
| `MaxPdfPages` / `MaxPdfPageMegapixels` | `50` / `40` | Bounds on an uploaded PDF. |
| `Offline` | `false` | Never download; fail loudly if a model is missing. |
| `WarmUpOnStart` | `false` | Preload models in the background at startup. |
| `LogTelemetry` | `false` | Log the library's metrics/spans through `ILogger`. |

## Telemetry

The library publishes metrics and activities under `EasyOcrDiagnostics.MeterName` /
`ActivitySourceName`. `LogTelemetry=true` prints them through `ILogger` using only
`System.Diagnostics`, so the sample can demonstrate the instrumentation without dragging a telemetry
backend into the restore graph. `Program.cs` contains the commented OpenTelemetry registration to use
instead in a real deployment.
