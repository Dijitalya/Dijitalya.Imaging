# Changelog

## Unreleased

### Changed
- Strict decoding is now the default. Use `ImageDecodeOptions.Strict = false` explicitly for legacy
  best-effort recovery of damaged files. Existing method signatures are preserved.
- Added per-operation file, dimension, pixel, allocation, ICC metadata and JPEG scan limits.
- Added cooperative cancellation to synchronous decoding and image resizing.
- Bounded resize scratch memory, selected the cheaper first axis, pooled scratch buffers and
  vectorized weighted accumulation. Crop no longer creates a discarded oversized intermediate.
- Streamed PNG rows directly across IDAT segments and removed the full inflated-data allocation.
- Removed the extra full-input copy for seekable streams and the payload copy in WebP identification.

### Fixed
- Resize scratch-size integer overflow and excessive allocation for extreme aspect ratios.
- Crop coordinate overflow and invalid enum fallbacks.
- PNG incomplete/excess pixels, checksum failures and invalid critical chunk structure in strict mode.
- JPEG truncated entropy consumption, missing end markers and duplicate component identifiers.
- GIF incomplete LZW data, malformed sub-blocks and out-of-canvas frames in strict mode.
- BMP truncated rows and invalid RLE bounds in strict mode; wide bit-mask channel scaling overflow.
- Explicit initialization of partially populated WebP Huffman stack buffers.

### Validation
- Added independent reference fixtures, allocation-bound regressions, cancellation and limit tests.
- Added three-platform CI, executable Native AOT checks, reverse interoperability checks,
  scheduled extended mutation tests and a dependency-free benchmark/smoke tool.
- Runtime validation was not executed in the delivery environment; see VALIDATION.md.


All notable changes to this package are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - Unreleased

First public release.

### Added

- Decoding of JPEG (baseline, extended sequential and progressive; grayscale, YCbCr, RGB, CMYK, YCCK),
  PNG (every colour type and bit depth, Adam7, tRNS), GIF (first frame), BMP (Windows and OS/2) and
  WebP (lossy, lossless, alpha, first frame of an animation).
- Encoding of JPEG, PNG (automatically indexed when possible), WebP (lossy with lossless alpha), GIF and BMP.
- `ImageCodec.Identify`: format and size from the header alone.
- EXIF orientation and RGB ICC colour profiles read from JPEG, PNG and WebP; ICC profiles written to them.
- `ImageResizer`: bicubic resizing with premultiplied alpha, and fitting into a box (`Max`, `Crop`, `Pad`).
- `ImageTransforms`: `AutoOrient`, `Rotate`, `FlipHorizontal`, `FlipVertical`, `Crop`.
- `ImageLimits`: size limits checked before any allocation.
