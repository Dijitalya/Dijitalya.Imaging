# Independent codec fixtures

Generated with Pillow 12.3.0 and ImageMagick by `tools/generate_fixtures.py`.
Synthetic images are created by this repository; no external photographs are included.
The `.rgba` files contain independent expected pixels. JPEG comparisons allow a mean
absolute channel difference of 3 because rounding and chroma interpolation can differ.
Lossless comparisons are exact; invisible GIF palette colors are normalized to transparent black.
The grayscale16 fixture follows this library's documented
high-byte conversion. RLE fixtures are also cross-checked with ImageMagick.
These fixtures are regression coverage, not proof of complete format conformance.
