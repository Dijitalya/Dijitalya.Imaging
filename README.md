# Dijitalya.Imaging

[![NuGet](https://img.shields.io/nuget/v/Dijitalya.Imaging.svg)](https://www.nuget.org/packages/Dijitalya.Imaging)

Image codecs and processing for .NET, in pure C#: no package dependencies, no native libraries, no
`System.Drawing`. It targets Windows, Linux, macOS and containers. CI is configured to test all three desktop
operating systems and publish and run a trimmed Native AOT smoke executable on each.

It is built for the job most web applications have: take a picture someone uploaded, check it,
turn it upright, make smaller copies of it and save them as JPEG, PNG or WebP.

## Installation

```
dotnet add package Dijitalya.Imaging
```

Requires .NET 10 or later.

## Formats

| Format | Read | Write |
|--------|------|-------|
| JPEG | Baseline, extended sequential and progressive; grayscale, YCbCr, RGB, CMYK and YCCK | Baseline; 4:2:0 below quality 91, 4:4:4 from 91 up |
| PNG | Every colour type and bit depth, Adam7 interlacing, tRNS transparency; the default image of an APNG | Lossless; automatically indexed when the picture has at most 256 colours |
| WebP | Lossy, lossless, alpha, the first frame of an animation | Lossy, with transparency kept losslessly |
| GIF | 87a and 89a, the first frame, transparency, interlacing | A still picture; more than 256 colours are reduced by median cut with Floyd-Steinberg dithering |
| BMP | Windows and OS/2 headers, 1 to 32 bits per pixel, RLE4, RLE8 and bit fields | 24-bit, or 32-bit with alpha when the picture has transparency |

WebP regression tests compare selected decoded pictures with libwebp reference pixel hashes.
This is evidence for those fixtures, not a guarantee for every possible WebP file.

## Usage

### Decode, turn upright, resize and save

```csharp
using Dijitalya.Imaging;
using Dijitalya.Imaging.Processing;

await using var upload = File.OpenRead("photo.jpg");

// The format is recognised from the content, never from the file name.
var decoded = await ImageCodec.DecodeAsync(upload);

// Phones store photos sideways and record the turn in EXIF; apply it to the pixels.
var upright = ImageTransforms.AutoOrient(decoded.Image, decoded.Orientation);

// At most 1600 x 1600, proportions kept. Smaller pictures are left as they are.
var resized = upright.Width > 1600 || upright.Height > 1600
    ? ImageResizer.ResizeToBox(upright, 1600, 1600, ResizeMode.Max)
    : upright;

// Passing the ICC profile on keeps the colours of wide-gamut photos (Display P3, Adobe RGB).
var webp = ImageCodec.Encode(resized, ImageFormat.Webp, quality: 80, iccProfile: decoded.IccProfile);

await File.WriteAllBytesAsync("photo" + ImageCodec.ExtensionOf(ImageFormat.Webp), webp);
```

### Check an upload without decoding it

```csharp
var bytes = await File.ReadAllBytesAsync("upload.bin");

if (ImageCodec.Identify(bytes) is not { } info)
{
    // Not a picture in a supported format, or its header is damaged.
    return;
}

Console.WriteLine($"{info.Format}, {info.Width} x {info.Height}, {ImageCodec.ContentTypeOf(info.Format)}");
```

### Thumbnails and other operations

```csharp
// Exactly 300 x 300: fills the square and cuts off the overflow, keeping the centre.
var square = ImageResizer.ResizeToBox(image, 300, 300, ResizeMode.Crop);

// Exactly 300 x 200: fits inside and fills the rest with transparency.
var padded = ImageResizer.ResizeToBox(image, 300, 200, ResizeMode.Pad);

// Exactly the size given, proportions not kept.
var stretched = ImageResizer.Resize(image, 640, 480);

var turned = ImageTransforms.Rotate(image, 90);
var mirrored = ImageTransforms.FlipHorizontal(image);
var cropped = ImageTransforms.Crop(image, x: 10, y: 10, width: 200, height: 100);
```

### Working with pixels

`RasterImage` holds the pixels as a byte array, four bytes per pixel in R, G, B, A order, rows from top
to bottom, alpha not premultiplied.

```csharp
var image = new RasterImage(64, 64); // fully transparent

for (var i = 0; i < image.Pixels.Length; i += 4)
{
    image.Pixels[i] = 255;     // red
    image.Pixels[i + 3] = 255; // opaque
}

var png = ImageCodec.Encode(image, ImageFormat.Png);
```

## Untrusted input

Decoding is strict by default. Strict mode rejects incomplete pixel data and applies format-specific
structural checks, including PNG chunk CRCs, chunk order, end markers and the zlib checksum; JPEG
entropy exhaustion and missing end markers; GIF incomplete LZW data; and BMP truncated rows or
invalid run lengths. It is not a general-purpose validator for every optional feature in every format.
Animated formats still return only the documented first/default picture.

Use per-operation limits appropriate to your service:

```csharp
var options = new ImageDecodeOptions
{
    MaxFileSize = 20 * 1024 * 1024,
    MaxPixelCount = 12_000_000,
    MaxDimension = 8192,
    MaxAllocationBytes = 192L * 1024 * 1024,
    MaxMetadataBytes = 1024 * 1024,
    MaxJpegScans = 256,
    Strict = true,
};

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var decoded = await ImageCodec.DecodeAsync(upload, options, timeout.Token);
var thumbnail = ImageResizer.ResizeToBox(decoded.Image, 300, 300, ResizeMode.Crop, timeout.Token);
```

The options overload of `DecodeAsync` requires the third cancellation argument; pass
`CancellationToken.None` when cancellation is not needed. This keeps the existing
`DecodeAsync(stream, default)` call unambiguous. Existing overloads remain available.

- The global ceilings are 32768 pixels per side, 64 * 1024 * 1024 pixels, and 272 MiB of file content.
  Callers can set lower input and image limits. Both byte-array and stream APIs enforce the file limit.
- The default allocation budget is 512 MiB. It accounts conservatively for input and decoder array
  buffers, including buffers no longer needed. For non-seekable streams it also accounts for the
  retained input-buffer capacity. It is not a process working-set limit: small objects, object headers,
  pool retention and runtime/decompressor overhead are not included. Bound request concurrency too.
- ICC profiles have a separate 16 MiB default ceiling. JPEG scan counts are limited independently.
- Cancellation is cooperative during input reads and decoder processing. It is not a hard deadline;
  one bounded loop/native decompressor call can complete before the next checkpoint. Encoding has
  no cancellation overload. Async decoding performs CPU work synchronously after reading the input.
- Content failures use `UnknownImageFormatException` or `InvalidImageContentException`. Cancellation,
  invalid API arguments, stream I/O failures and process-wide memory exhaustion remain distinct.
- PNG pixel rows are inflated directly from the input IDAT segments. No full inflated pixel-data copy
  or concatenated IDAT copy is allocated. Seekable streams use one exact-length input buffer.
- `Identify` reads dimensions only; it does not validate the full file or approve an upload.

For an explicit best-effort recovery workflow, use `new ImageDecodeOptions { Strict = false }`.
This permits some incomplete pictures to decode; it does not disable size, allocation or metadata
limits, and not every damaged file can be recovered. Strict defaults can reject damaged files that
older versions accepted; select recovery mode deliberately if that behavior is required.

Encoding does not copy source EXIF, GPS, camera details or comments. An ICC profile is written only
when explicitly passed. A profile is preserved, not used to perform a color-space conversion.

## Processing and performance

Resizing uses a separable bicubic filter with premultiplied alpha, selects the first axis from the
image dimensions and bounds the float row cache to 16 MiB. Scratch arrays are rented and cleared
before returning to the shared pool. Vector operations accelerate weighted accumulation when
supported by the runtime. Different pass orders or hardware can introduce small rounding differences.

Crop samples the center window of the virtual resized picture directly, so discarded pixels do not
require an intermediate image. Output pixels and sampling maps are additional to the row-cache bound.
Resize cancellation overloads preserve the original source image; callers must not mutate its public
pixel array concurrently with any operation reading it.

Performance improvements are architectural changes until measured on your workload. Run the included
benchmark on the same machine, runtime, build configuration and inputs for both versions:

```sh
dotnet run --project tools/Imaging.Validation --configuration Release -- --benchmark 10
```

It reports median/p95 time and per-thread managed allocated bytes after warm-up. It does not measure
peak resident memory or establish a speedup against another library. Record those separately for
production capacity planning. No measured speedup is claimed in this source update.

## Building from source

```
dotnet build
dotnet test
dotnet pack src/Dijitalya.Imaging --configuration Release --output artifacts
```

The SDK version is pinned in `global.json`. Run the complete local build/test/smoke/benchmark/package
sequence with `pwsh -File tools/verify.ps1` on Windows or `bash tools/verify.sh` on Unix.

Independent fixtures live in `tests/Dijitalya.Imaging.Tests/Fixtures`; regeneration uses Pillow and
ImageMagick via `python tools/generate_fixtures.py`. These tools are not dependencies of the library.
CI also opens this library's encoded output with Pillow to check interoperability in the other direction.

The weekly mutation workflow uses 10,000 mutations per format, bounded decoder limits, a test-host
hang timeout and a job timeout. These are deterministic mutation tests, not exhaustive or
coverage-guided fuzzing. Native AOT, operating-system and benchmark checks must actually pass before
a release is considered validated. See `VALIDATION.md` for the status of this delivery.

## Releasing

Releases are published to nuget.org by the CI workflow when a version tag is pushed:

```
git tag v1.0.1
git push origin v1.0.1
```

The tag gives the package version (`v1.0.1` publishes 1.0.1). Builds without a tag produce prerelease
packages such as `1.0.0-ci.42`, kept as workflow artifacts and not published.

Publishing uses nuget.org Trusted Publishing, so no API key is stored in the repository. One-time setup:

1. On nuget.org, open **Trusted Publishing** under the account menu and add a policy for this repository:
   the repository owner and name, workflow file `ci.yml` and environment `nuget`.
2. In the GitHub repository settings, add the secret `NUGET_USER` with the nuget.org profile name (not
   the e-mail address) of the account that owns the package.
3. Optionally, add required reviewers to the `nuget` environment so that every release waits for an
   approval.

Record each release in `CHANGELOG.md`.

## License

Dijitalya.Imaging is licensed under the MIT License. The WebP codecs are derived from libwebp, which is
licensed under the BSD 3-Clause License; its notice is in `THIRD-PARTY-NOTICES.md`, which is also
included in the package.
