# Security Policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| 3.0.x   | ✅ Yes    |
| 2.3.x   | ⚠️ Security fixes only |
| < 2.3   | ❌ No     |

## Reporting a vulnerability

**Please do not open a public issue.**

Report privately through
[GitHub Security Advisories](https://github.com/FarhanLodi/EasyOcrSharp/security/advisories/new).
That reaches the maintainers directly and stays private until a fix is available.

Please include the affected version, what an attacker can achieve, and — if you have one — a sample
input that reproduces it. A small file is far more useful than a description.

You can expect an acknowledgement within a few days. Once a fix ships, you will be credited in the
advisory unless you would rather not be.

## What this library does with untrusted input

Most of the attack surface is **the documents you feed it**. If you run OCR over files from
untrusted sources, these are the areas worth knowing about:

**Image decoding.** Decoding runs on [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp).
Malformed images are a decoder-level concern; a decompression-bomb guard reads image headers and
rejects oversized dimensions before allocating pixels, so a small file that claims enormous
dimensions is refused rather than exhausting memory.

**PDF parsing.** PDFs are rasterized through PDFium (`Docnet.Core`), a native library. Untrusted PDFs
are the highest-risk input here, because parsing happens in native code. Consider process isolation
or resource limits if your workload accepts arbitrary PDFs.

**Model downloads.** Models are fetched over HTTPS and verified against pinned SHA-256 hashes,
**fail-closed** — a file whose hash does not match is rejected and never loaded. A mirror configured
through `EASYOCRSHARP_MODEL_BASE_URL` or `EASYOCRSHARP_STRUCTURE_MODEL_BASE_URL` must still serve
byte-identical files, and non-`https://` URLs are rejected. For air-gapped deployments, pre-seed the
cache directory instead.

**ONNX model files.** If you point the library at your own model files, treat them as code: ONNX
Runtime executes the graph they contain. Only load models you trust.

**Barcode and searchable-PDF output.** Recognized text is written into output formats (hOCR, ALTO,
searchable PDF, JSON). If you render that output in a browser, escape it — text recovered from an
image is untrusted input to whatever consumes it.

## What is out of scope

* OCR being inaccurate, or recognizing text incorrectly. That is a bug, not a vulnerability — please
  open a normal issue.
* Vulnerabilities in ONNX Runtime, PDFium or other dependencies. Report those upstream; we will pick
  up fixed versions. Do tell us if we are pinning a version with a known advisory.
* Resource exhaustion from inputs you control yourself. OCR is inherently expensive; size limits are
  the caller's responsibility.
