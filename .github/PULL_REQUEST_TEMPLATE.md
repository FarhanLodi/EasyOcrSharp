## What this changes

<!-- What it does and why. Link the issue if there is one: Fixes #123 -->

## How it was verified

<!-- Delete what does not apply. -->

- [ ] `dotnet build EasyOcrSharp.slnx -c Release` is clean — 0 errors, 0 warnings
- [ ] Unit tests pass: `dotnet test ... --filter "Category!=Integration"`
- [ ] Full suite pass (downloads models, ~5 min): `dotnet test test/EasyOcrSharp.Tests/EasyOcrSharp.Tests.csproj -c Release`
- [ ] Added or updated tests covering this change

> CI only runs the non-integration filter, so it will not catch an accuracy regression. If this
> touches preprocessing, detection, recognition, decoding or the structure pipeline, please run the
> full suite locally and say so.

## Compatibility

- [ ] No public type, signature or default changed
- [ ] This changes public API or behaviour — explained below

<!-- The package has thousands of installs. A break is a major-version decision: say what breaks,
     who it affects, and why an additive change would not work. -->

## Does OCR output change?

- [ ] No — output is identical
- [ ] Yes — before/after shown below on real input

<!-- Refactors on the inference path can shift results in ways the tests do not catch. If output
     moves at all, show it rather than assuming it is noise. -->

## Anything else

<!-- New dependency? Changed model checksum or download URL? Say so here — both get extra scrutiny.
     A changed checksum needs to say what produced the new file. -->
