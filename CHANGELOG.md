# Changelog

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
