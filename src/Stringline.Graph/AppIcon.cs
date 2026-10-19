using SkiaSharp;

namespace Stringline.Graph;

/// <summary>
/// The app's icon (S18), drawn rather than kept only as a binary so it can be changed and made again
/// (<c>stringline icon</c>): a small train graph on a rounded tile — station lines across, a stopping train climbing
/// with a dwell, a second following it and a fast train the other way. Small sizes leave out the station lines and the
/// fast train, and keep the lines at least two pixels wide, so the shape still reads at 16 px.
/// </summary>
public static class AppIcon
{
    /// <summary>The sizes in the <c>.ico</c>: the ones Windows asks for at 100–200% scaling, and 256 for Explorer.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static readonly SKColor Tile = new(0xF7, 0xF9, 0xFC);
    private static readonly SKColor Edge = new(0x2F, 0x3B, 0x4C);
    private static readonly SKColor Station = new(0xC5, 0xCF, 0xDC);
    private static readonly SKColor Stopping = new(0x15, 0x65, 0xC0);
    private static readonly SKColor Following = new(0x2E, 0x7D, 0x32);
    private static readonly SKColor Fast = new(0xC6, 0x28, 0x28);

    /// <summary>The icon at <paramref name="size"/> × <paramref name="size"/> pixels, as PNG bytes.</summary>
    public static byte[] Png(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 1024);
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException($"Cannot create a {size}×{size} surface.");
        Draw(surface.Canvas, size);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Writes a Windows icon with every size in <see cref="Sizes"/>, each stored as PNG (Windows Vista and later, WPF
    /// and the resource compiler all read these).
    /// </summary>
    public static void WriteIco(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var images = Sizes.Select(s => (Size: s, Png: Png(s))).ToList();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: icon
        writer.Write((ushort)images.Count);
        var offset = 6 + (16 * images.Count);
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); // no palette
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // colour planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images)
        {
            writer.Write(png);
        }
    }

    private static void Draw(SKCanvas canvas, int size)
    {
        canvas.Clear(SKColors.Transparent);
        // Drawn in a 256-unit square.
        var scale = size / 256f;
        canvas.Scale(scale);
        var small = size <= 24;
        // At least this many units wide, so a line is two pixels or more at any size.
        float Width(float units) => Math.Max(units, 2f / scale);

        var edgeWidth = Width(small ? 12 : 8);
        var inset = edgeWidth / 2 + (small ? 0 : 6);
        var tile = new SKRoundRect(new SKRect(inset, inset, 256 - inset, 256 - inset), small ? 36 : 44);
        using (var fill = new SKPaint { Color = Tile, IsAntialias = true })
        {
            canvas.DrawRoundRect(tile, fill);
        }

        canvas.Save();
        canvas.ClipRoundRect(tile, antialias: true);
        if (!small)
        {
            using var grid = new SKPaint { Color = Station, IsAntialias = true, StrokeWidth = Width(4), Style = SKPaintStyle.Stroke };
            foreach (var y in new[] { 72f, 128f, 184f })
            {
                canvas.DrawLine(0, y, 256, y, grid);
            }
        }

        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeWidth = Width(small ? 26 : 16),
        };

        // Time runs to the right; the stopping train climbs, dwells at the middle station and climbs on, with another
        // following it. Small sizes leave out the fast train the other way, which turns the picture into an X.
        line.Color = Following;
        if (small)
        {
            Polyline(canvas, line, (112, 236), (160, 150), (200, 150), (248, 64));
        }
        else
        {
            Polyline(canvas, line, (116, 222), (164, 128), (178, 128), (226, 34));
        }

        line.Color = Stopping;
        if (small)
        {
            Polyline(canvas, line, (8, 192), (56, 106), (96, 106), (144, 20));
        }
        else
        {
            Polyline(canvas, line, (36, 222), (84, 128), (110, 128), (158, 34));
            line.Color = Fast;
            canvas.DrawLine(40, 44, 216, 212, line);
        }

        canvas.Restore();

        using var edge = new SKPaint { Color = Edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = edgeWidth };
        canvas.DrawRoundRect(tile, edge);
    }

    private static void Polyline(SKCanvas canvas, SKPaint paint, params (float X, float Y)[] points)
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(points[0].X, points[0].Y);
        foreach (var (x, y) in points.Skip(1))
        {
            builder.LineTo(x, y);
        }

        using var path = builder.Detach();
        canvas.DrawPath(path, paint);
    }
}
