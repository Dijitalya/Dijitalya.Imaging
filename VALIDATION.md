# Validation status

This is a source update, not a certified release or a claim of defect-free operation.

## Completed in the delivery environment

- Reviewed changed decoder, image-processing, stream-buffering and limit paths.
- Generated 15 independent image fixtures with Pillow/ImageMagick and checked expected RGBA sizes.
- Cross-checked the independently constructed RLE4/RLE8 BMP fixtures with ImageMagick.
- Checked 100 randomized crop-window equivalence and bounded-cache indexing cases using an independent
  Python numerical model. These checks did not execute the C# implementation.
- Parsed project XML, workflow YAML and JSON configuration.
- Checked basic C# lexical delimiter balance, Python syntax, shell syntax and diff whitespace.
- Scanned project source and documentation for Turkish-specific characters; all new code, comments,
  messages and documentation are written in English.

## Not executed in the delivery environment

The environment has no .NET SDK/runtime. An SDK download was blocked by its network policy.
Consequently, C# compilation, xUnit execution, Native AOT publication/execution, the reverse
interoperability checks and runtime performance measurements have NOT been performed here.
Static and numerical checks do not replace those release gates. No measured speedup is claimed.

## Required release gates

1. Install the SDK selected by `global.json` and run `pwsh -File tools/verify.ps1` on Windows
   or `bash tools/verify.sh` on Unix. Review the test results and benchmark CSV in `artifacts/validation`.
2. Require all three CI operating-system jobs, including Native AOT smoke checks, to pass.
3. Require the independent fixture tests and Pillow reverse-interoperability checks to pass.
4. Run the extended mutation workflow; investigate any failing input or hang and retain it as a regression.
5. Compare benchmarks with the previous revision on the same machine/runtime, and measure peak RSS
   and concurrent-request behavior on representative production images before making capacity claims.

## Compatibility and operational notes

- Existing public method signatures remain. Strict decoding is now the default and may reject damaged
  inputs that older versions recovered. Opt into `Strict = false` only for deliberate recovery.
- Allocation limits are conservative accounting for input and decoder buffers, not a hard process-memory cap.
- Cooperative cancellation is supported during decoding and resizing. Encoding is still synchronous
  without a cancellation overload. Limit service concurrency independently.
- Different resize pass orders and SIMD implementations may produce small rounding differences.
- Fixtures and mutation tests cover selected cases; they do not prove exhaustive format conformance.

## Archive preservation

The original project layout and unrelated files are retained. Original `bin` and `obj` artifacts,
where present in the uploaded archive, are retained unchanged for archive fidelity. They are NOT
builds of this source update. Run the verification script to restore and rebuild before use; do
not use retained binaries or a `--no-build` command as evidence that this update has been compiled.
