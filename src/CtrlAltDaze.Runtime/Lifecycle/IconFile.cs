namespace CtrlAltDaze.Runtime.Lifecycle;

/// <summary>One square image of an .ico: straight-alpha ARGB (0xAARRGGBB), row-major, top-down.</summary>
public sealed record IconImage(int Size, uint[] Pixels);

/// <summary>
/// Writes an .ico with 32-bit BMP entries (alpha used, AND mask all zero). Explorer reads BMP entries at every size,
/// 256 too; PNG ones would be smaller but need a compressor the engine doesn't otherwise carry.
/// </summary>
public static class IconFile
{
    public static void Write(Stream stream, IReadOnlyList<IconImage> images)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
            throw new ArgumentException("An icon needs at least one image.", nameof(images));
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // icon
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var image in images)
        {
            if (image.Size is < 1 or > 256 || image.Pixels.Length != image.Size * image.Size)
                throw new ArgumentException($"A {image.Size} px image needs {image.Size * image.Size} pixels.", nameof(images));
            var bytes = EntryBytes(image.Size);
            writer.Write((byte)(image.Size == 256 ? 0 : image.Size));
            writer.Write((byte)(image.Size == 256 ? 0 : image.Size));
            writer.Write((byte)0); // no palette
            writer.Write((byte)0);
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write(bytes);
            writer.Write(offset);
            offset += bytes;
        }
        foreach (var image in images)
        {
            var size = image.Size;
            // BITMAPINFOHEADER; the height counts the XOR and the AND bitmap.
            writer.Write(40);
            writer.Write(size);
            writer.Write(2 * size);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0); // BI_RGB
            writer.Write(size * size * 4);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            for (var y = size - 1; y >= 0; y--) // bottom-up
            {
                for (var x = 0; x < size; x++)
                    writer.Write(image.Pixels[y * size + x]); // little-endian 0xAARRGGBB = B, G, R, A
            }
            writer.Write(new byte[MaskStride(size) * size]);
        }
    }

    private static int MaskStride(int size) => (size + 31) / 32 * 4;

    private static int EntryBytes(int size) => 40 + size * size * 4 + MaskStride(size) * size;
}
