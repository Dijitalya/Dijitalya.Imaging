# Dijitalya.Imaging

[![NuGet](https://img.shields.io/nuget/v/Dijitalya.Imaging.svg)](https://www.nuget.org/packages/Dijitalya.Imaging)

Image codecs and processing for .NET, in pure C#: no package dependencies, no native libraries, no
`System.Drawing`. It runs the same on Windows, Linux, macOS and in containers, and is compatible with
trimming and Native AOT.

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

The decoded WebP pixels are identical to libwebp's.

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

The decoders are written to read files from strangers:

- Every length, offset and table index in a file is checked before it is used.
- The size a file claims is compared with `ImageLimits` (at most 32768 pixels a side and 64 megapixels)
  before any memory is allocated for it, so a small file claiming an enormous picture is refused.
  `DecodeAsync` also refuses streams longer than `ImageLimits.MaxFileSize`.
- `DecodeAsync` checks the first bytes before reading the rest, so a file that is not a picture is turned
  away without being loaded.
- Whatever is wrong with a file, the caller gets one of two exceptions:
  `UnknownImageFormatException` (not a picture in a supported format) or `InvalidImageContentException`
  (damaged, unsupported or too large).

The test suite feeds every decoder hundreds of damaged files (flipped bytes, cut-off data, inserted
garbage) and checks that each one fails with one of these two exceptions.

Writing a picture never copies EXIF or other metadata from its source, so re-encoding an upload removes
its GPS location, camera details and comments. Only an ICC colour profile is written, and only when
it is passed in.

## Building from source

```
dotnet build
dotnet test
dotnet pack src/Dijitalya.Imaging --configuration Release --output artifacts
```

The SDK version is pinned in `global.json`.

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
