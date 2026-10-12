using System.Diagnostics;
using System.Globalization;
using Dijitalya.Imaging;
using Dijitalya.Imaging.Processing;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var source = CreatePicture(256, 192);

if (args.Length > 0 && args[0] == "--export")
{
    var directory = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "source.rgba"), source.Pixels);
    foreach (var format in Enum.GetValues<ImageFormat>())
        File.WriteAllBytes(Path.Combine(directory, "image" + ImageCodec.ExtensionOf(format)), ImageCodec.Encode(source, format, 90));
    return;
}

if (args.Length > 0 && args[0] == "--benchmark")
{
    var iterations = args.Length > 1 ? int.Parse(args[1]) : 5;
    if (iterations is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(iterations));
    var picture = CreatePicture(1024, 768);
    Console.Error.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine("operation,iterations,median_ms,p95_ms,mean_allocated_bytes");
    foreach (var format in Enum.GetValues<ImageFormat>())
    {
        var encoded = ImageCodec.Encode(picture, format, 80);
        Measure($"decode_{format}", iterations, () => ImageCodec.Decode(encoded));
        Measure($"encode_{format}", iterations, () => ImageCodec.Encode(picture, format, 80));
    }
    Measure("resize_thumbnail", iterations, () => ImageResizer.Resize(picture, 320, 240));
    Measure("crop_square", iterations, () => ImageResizer.ResizeToBox(picture, 256, 256, ResizeMode.Crop));
    var thin = CreatePicture(1, 8192);
    Measure("resize_thin", iterations, () => ImageResizer.Resize(thin, 8192, 1));
    return;
}

foreach (var format in Enum.GetValues<ImageFormat>())
{
    var encoded = ImageCodec.Encode(source, format, 90);
    var decoded = ImageCodec.Decode(encoded);
    if (decoded.Image.Width != source.Width || decoded.Image.Height != source.Height)
        throw new InvalidOperationException($"Incorrect dimensions for {format}.");
    if ((format is ImageFormat.Png or ImageFormat.Bmp) && !source.Pixels.AsSpan().SequenceEqual(decoded.Image.Pixels))
        throw new InvalidOperationException($"Lossless round-trip failed for {format}.");
    var resized = ImageResizer.ResizeToBox(decoded.Image, 64, 64, ResizeMode.Crop);
    if (resized.Width != 64 || resized.Height != 64) throw new InvalidOperationException("Crop failed.");
    Console.WriteLine($"PASS {format}");
}
Console.WriteLine("Smoke checks passed.");

static RasterImage CreatePicture(int width, int height)
{
    var image = new RasterImage(width, height);
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var offset = (y * width + x) * 4;
        image.Pixels[offset] = (byte)(x * 255 / Math.Max(1, width - 1));
        image.Pixels[offset + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
        image.Pixels[offset + 2] = (byte)((x + y) * 255 / Math.Max(1, width + height - 2));
        image.Pixels[offset + 3] = 255;
    }
    return image;
}

static void Measure(string name, int iterations, Func<object> operation)
{
    // Warm up JIT and shared pools before comparing runs. Input generation is outside the timing.
    for (var index = 0; index < 2; index++) GC.KeepAlive(operation());
    var samples = new double[iterations];
    long bytes = 0;
    for (var index = 0; index < iterations; index++)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var result = operation();
        samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        GC.KeepAlive(result);
    }
    Array.Sort(samples);
    var median = iterations % 2 == 0 ? (samples[iterations / 2 - 1] + samples[iterations / 2]) / 2 : samples[iterations / 2];
    Console.WriteLine($"{name},{iterations},{median:F3},{samples[(int)Math.Ceiling(iterations * 0.95) - 1]:F3},{bytes / iterations}");
}
