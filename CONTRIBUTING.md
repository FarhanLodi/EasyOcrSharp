# Contributing to EasyOcrSharp

Thanks for taking the time. This guide covers the things that are specific to this repository — the
parts that are easy to get wrong because they are not obvious from the code.

## Getting set up

You need the **.NET 10 SDK**. The library targets `net10.0` only; please don't add older target
frameworks.

```bash
git clone https://github.com/FarhanLodi/EasyOcrSharp
cd EasyOcrSharp
dotnet build EasyOcrSharp.slnx -c Release
```

The build must be clean. `TreatWarningsAsErrors` is on and the repository sits at **zero warnings** —
if your change introduces one, it fails.

## Running the tests

There are two suites, and the difference matters.

```bash
# Unit tests. No network, no models, runs in seconds. This is what CI gates on.
dotnet test test/EasyOcrSharp.Tests/EasyOcrSharp.Tests.csproj -c Release --filter "Category!=Integration"

# Everything, including tests that download models and run real inference.
dotnet test test/EasyOcrSharp.Tests/EasyOcrSharp.Tests.csproj -c Release
```

The full run takes about five minutes and **downloads roughly 1 GB of models on a cold cache** from
[the model repository](https://huggingface.co/EasyOcrSharp/EasyOcrSharp-models), cached in
`%LOCALAPPDATA%/EasyOcrSharp/models` (or the platform equivalent).

CI only runs the non-integration filter, so **an accuracy regression will not be caught by CI**. If
you touch anything on the inference path — preprocessing, the detector, the recognizer, the decoder,
or the structure pipeline — run the full suite locally and say so in the pull request.

Two tests skip unless you provide external assets (an encrypted-PDF fixture and a local TrOCR
export). Two skips is the expected result, not a problem.

## Things that will get a pull request sent back

**Breaking the public API.** The package has thousands of installs. Renaming or moving a public type,
changing a signature, or changing a default is a major-version decision, not a pull-request decision.
Add an overload instead of changing one. If you believe a break is genuinely necessary, open an issue
first and make the case.

**Changing model checksums without saying why.** Every model download is verified against a pinned
SHA-256 and fails closed. If a pin changes, the pull request must explain what produced the new file
and why the old one is wrong.

**Silently changing OCR output.** Small refactors on the inference path can shift results in ways
tests don't catch. If output changes, show before/after on real images.

## Code style

Match the file you are editing. The codebase has a consistent voice — explanatory comments that say
*why* rather than restating the code, XML docs on public members, and no dead code. A few specifics:

* Every public member needs an XML doc comment (missing ones are warnings, and warnings are errors).
* Comments should explain intent and constraints, not narrate. If a value is non-obvious — a
  threshold, a magic number, a fallback — say where it came from.
* Don't add a dependency without discussing it in an issue first. Keeping the graph small and
  permissively licensed is a deliberate goal of this project.

## The structure engine is a hand-maintained port

`src/EasyOcrSharp/Structure/` implements PaddleOCR's PP-StructureV3 pipeline. It was ported from
[PaddleOcrNet](https://github.com/FarhanLodi/PaddleOcrNet) and the two trees are kept in sync by
hand — **a fix in one does not reach the other automatically.** Upstream types carrying a `Paddle`
prefix were renamed here (`PaddleStructureEngine` → `StructureEngine`, `PaddleOcrService` →
`StructureService`, and so on), so diffs against upstream need that mapping in mind.

Everything under `Structure/Engine/` is `internal` on purpose: it carries its own `OcrResult`,
`RecognitionOptions` and exception types that would otherwise collide by name with the public ones in
`EasyOcrSharp.Models`.

## Reporting bugs

Include the EasyOcrSharp version, the .NET version, your OS, and — if at all possible — an image that
reproduces it. "OCR is wrong on my document" is very hard to act on without the document; a cropped
region that still shows the problem is ideal and avoids sharing anything sensitive.

## Security

Please don't open a public issue for a security problem. See [SECURITY.md](SECURITY.md).

## Licensing

By contributing you agree that your contributions are licensed under the MIT License, the same terms
that cover the rest of the project.
