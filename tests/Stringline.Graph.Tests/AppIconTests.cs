using SkiaSharp;
using Stringline.Gateway.Tests;

namespace Stringline.Graph.Tests;

/// <summary>S18: the app icon is a well-formed Windows icon with every size, each a PNG of that size.</summary>
public sealed class AppIconTests
{
    [Fact]
    public void Each_size_is_a_png_of_that_size_with_a_clear_corner_and_a_solid_middle()
    {
        foreach (var size in AppIcon.Sizes)
        {
            using var bitmap = SKBitmap.Decode(AppIcon.Png(size));
            (bitmap.Width, bitmap.Height).Should().Be((size, size));
            bitmap.GetPixel(0, 0).Alpha.Should().BeLessThan(32, "the rounded corner is (nearly) transparent at {0} px", size);
            bitmap.GetPixel(size / 2, size / 2).Alpha.Should().Be(255, "the tile is opaque at {0} px", size);
        }
    }

    [Fact]
    public void The_ico_lists_every_size_with_its_png()
    {
        using var stream = new MemoryStream();
        AppIcon.WriteIco(stream);
        Read(stream.ToArray()).Should().Equal(AppIcon.Sizes);
    }

    [Fact]
    public void The_app_ships_an_icon_with_every_size()
    {
        // Made by `stringline icon src/Stringline.App/Assets/stringline.ico`; run it again after changing AppIcon.
        var path = Path.Combine(Fixtures.Path("."), "..", "..", "src", "Stringline.App", "Assets", "stringline.ico");
        Read(File.ReadAllBytes(path)).Should().Equal(AppIcon.Sizes);
    }

    /// <summary>The sizes in an .ico, checking the header and that each image is a PNG of its size.</summary>
    private static List<int> Read(byte[] ico)
    {
        using var reader = new BinaryReader(new MemoryStream(ico));
        (reader.ReadUInt16(), reader.ReadUInt16()).Should().Be(((ushort)0, (ushort)1));
        var count = reader.ReadUInt16();
        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            reader.ReadBytes(2);
            (reader.ReadUInt16(), reader.ReadUInt16()).Should().Be(((ushort)1, (ushort)32));
            var length = reader.ReadInt32();
            var offset = reader.ReadInt32();
            var size = width == 0 ? 256 : width;
            height.Should().Be(width);
            using var bitmap = SKBitmap.Decode(ico.AsSpan(offset, length));
            (bitmap.Width, bitmap.Height).Should().Be((size, size));
            sizes.Add(size);
        }

        return sizes;
    }
}
