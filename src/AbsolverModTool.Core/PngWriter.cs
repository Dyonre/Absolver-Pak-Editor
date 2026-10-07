using System.IO.Compression;
using System.Text;

namespace AbsolverModTool.Core;

/// <summary>Writes a plain 8-bit RGBA PNG from raw pixel bytes - just enough of the format to
/// round-trip <see cref="TextureDecoder"/>'s output to a file a human (or any other tool) can
/// open, without pulling in a platform-specific imaging library (this project's only other
/// dependency, UAssetAPI, is fully cross-platform - keeping it that way here too). No filtering
/// (every scanline uses filter type 0/None) and no interlacing - simplicity over file size, these
/// are small icon-sized images.</summary>
public static class PngWriter
{
    static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static void Write(string path, int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException($"expected {width * height * 4} bytes for {width}x{height} RGBA, got {rgba.Length}");
        using var fs = File.Create(path);
        fs.Write(Signature);
        WriteChunk(fs, "IHDR", BuildIhdr(width, height));
        WriteChunk(fs, "IDAT", BuildIdat(width, height, rgba));
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    static byte[] BuildIhdr(int width, int height)
    {
        var b = new byte[13];
        WriteBE(b, 0, width);
        WriteBE(b, 4, height);
        b[8] = 8;  // bit depth
        b[9] = 6;  // color type 6 = RGBA
        b[10] = 0; // compression method (only valid value)
        b[11] = 0; // filter method (only valid value)
        b[12] = 0; // interlace method: none
        return b;
    }

    static byte[] BuildIdat(int width, int height, byte[] rgba)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[width * 4 + 1]; // +1 for the per-scanline filter-type byte
            for (int y = 0; y < height; y++)
            {
                row[0] = 0; // filter type 0 = None
                Buffer.BlockCopy(rgba, y * width * 4, row, 1, width * 4);
                zlib.Write(row);
            }
        }
        return ms.ToArray();
    }

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        WriteBE(s, data.Length);
        s.Write(typeBytes);
        s.Write(data);
        WriteBE(s, unchecked((int)Crc32(typeBytes, data)));
    }

    static void WriteBE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    static void WriteBE(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        void Update(byte b)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        foreach (var b in type) Update(b);
        foreach (var b in data) Update(b);
        return ~crc;
    }
}
