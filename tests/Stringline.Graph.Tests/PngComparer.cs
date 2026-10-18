using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Stringline.Graph.Tests;

/// <summary>
/// Compares PNG snapshots by pixel, with a tolerance. Skia draws lines identically everywhere, but text goes through
/// DirectWrite on Windows and FreeType on Linux, so glyph edges differ slightly even with the embedded font.
/// </summary>
internal static class PngComparer
{
    /// <summary>A pixel counts as different when a channel differs by more than this.</summary>
    public const int ChannelTolerance = 64;

    /// <summary>
    /// Share of pixels allowed to differ: 0.01%. Measured Windows against Linux, no pixel differs by more than
    /// <see cref="ChannelTolerance"/>; a moved line or headcode label differs by hundreds.
    /// </summary>
    public const double MaxDifferentShare = 0.0001;

    [ModuleInitializer]
    public static void Initialise()
    {
        VerifierSettings.RegisterStreamComparer("png", (received, verified, _) => Task.FromResult(Compare(received, verified)));
    }

    public static CompareResult Compare(Stream received, Stream verified)
    {
        // Decode copies: SKBitmap.Decode(Stream) closes the stream, and Verify still needs it after a mismatch.
        using var a = Decode(received);
        using var b = Decode(verified);
        if (a is null || b is null)
        {
            return CompareResult.NotEqual("Not a readable PNG.");
        }

        if (a.Width != b.Width || a.Height != b.Height)
        {
            return CompareResult.NotEqual($"Size {a.Width}×{a.Height}, expected {b.Width}×{b.Height}.");
        }

        var different = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) > ChannelTolerance || Math.Abs(p.Green - q.Green) > ChannelTolerance
                    || Math.Abs(p.Blue - q.Blue) > ChannelTolerance || Math.Abs(p.Alpha - q.Alpha) > ChannelTolerance)
                {
                    different++;
                }
            }
        }

        var share = (double)different / (a.Width * a.Height);
        return share <= MaxDifferentShare
            ? CompareResult.Equal
            : CompareResult.NotEqual($"{different} pixels ({share:P2}) differ; at most {MaxDifferentShare:P2} may.");
    }

    private static SKBitmap? Decode(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        using var data = SKData.CreateCopy(copy.ToArray());
        using var codec = SKCodec.Create(data);
        return codec is null ? null : SKBitmap.Decode(codec);
    }
}
