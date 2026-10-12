using System.Text.Json;

namespace Dijitalya.Imaging.Tests;

public sealed class IndependentFixtureTests
{
    [Fact]
    public void Decode_MatchesIndependentReferencePixels()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(folder, "manifest.json")))!;
        Assert.True(fixtures.Length >= 15);
        foreach (var fixture in fixtures)
        {
            var image = ImageCodec.Decode(File.ReadAllBytes(Path.Combine(folder, fixture.File))).Image;
            var expected = File.ReadAllBytes(Path.Combine(folder, fixture.Expected));
            Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
            Assert.Equal(expected.Length, image.Pixels.Length);
            if (fixture.MeanAbsoluteError == 0)
                Assert.True(expected.AsSpan().SequenceEqual(image.Pixels), $"Pixel mismatch: {fixture.File}");
            else
            {
                long difference = 0;
                for (var index = 0; index < expected.Length; index++)
                    difference += Math.Abs(expected[index] - image.Pixels[index]);
                var mean = (double)difference / expected.Length;
                Assert.True(mean <= fixture.MeanAbsoluteError, $"{fixture.File}: mean error {mean:F4}");
            }
        }
    }

    public sealed class Fixture
    {
        public string File { get; set; } = "";
        public string Expected { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public double MeanAbsoluteError { get; set; }
    }
}
