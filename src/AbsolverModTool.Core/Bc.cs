namespace AbsolverModTool.Core;

/// <summary>Standard BC1 (DXT1) / BC3 (DXT5) block decompression - the well-documented public
/// format (Microsoft's D3D BC spec / the same layout every BCn decoder implements), not something
/// reverse-engineered like <see cref="TextureDecoder"/>'s header layout had to be. Added 2026-09-12
/// after confirming equipment/dye item icons (unlike attack pictos and simple item pictos, which
/// are uncompressed) commonly use inline DXT5 - e.g. a 256x256 belt icon,
/// <c>UI/Items/Gears/AnlekPaladinSet/T_Pictos_M_AnlekPaladinSet_Belt_01</c>. Decode-only, no
/// encoding; no BC4/BC5/BC7 (not needed for icon rendering - those show up on 3D-model normal/
/// specular maps, which also need out-of-line <c>.ubulk</c> reading this project doesn't do).</summary>
public static class Bc
{
    /// <summary>Decompresses a BC1/BC3 block stream (mip 0 only, tightly packed, no padding
    /// between blocks) into RGBA8 pixels, row-major, standard R-G-B-A byte order.</summary>
    public static byte[] Decompress(byte[] data, int offset, int width, int height, bool hasAlpha)
    {
        var rgba = new byte[width * height * 4];
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int pos = offset;

        var alphas = new byte[16];
        var colors = new (byte R, byte G, byte B)[4];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                if (hasAlpha)
                {
                    DecodeAlphaBlock(data, pos, alphas);
                    pos += 8;
                }
                else Array.Fill(alphas, (byte)255);

                int colorBlockPos = pos;
                DecodeColorBlock(data, colorBlockPos, colors);
                pos += 8;

                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= height) continue;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= width) continue;
                        int pixelIndex = py * 4 + px;
                        var c = colors[ColorIndexAt(data, colorBlockPos, pixelIndex)];
                        int dst = (y * width + x) * 4;
                        rgba[dst] = c.R; rgba[dst + 1] = c.G; rgba[dst + 2] = c.B; rgba[dst + 3] = alphas[pixelIndex];
                    }
                }
            }
        }
        return rgba;
    }

    static void DecodeAlphaBlock(byte[] data, int pos, byte[] outAlphas)
    {
        byte a0 = data[pos], a1 = data[pos + 1];
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)data[pos + 2 + i] << (8 * i);

        var table = new byte[8];
        table[0] = a0; table[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i <= 6; i++) table[1 + i] = (byte)(((7 - i) * a0 + i * a1) / 7);
        }
        else
        {
            for (int i = 1; i <= 4; i++) table[1 + i] = (byte)(((5 - i) * a0 + i * a1) / 5);
            table[6] = 0;
            table[7] = 255;
        }

        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((bits >> (3 * i)) & 0x7);
            outAlphas[i] = table[idx];
        }
    }

    static void DecodeColorBlock(byte[] data, int pos, (byte R, byte G, byte B)[] outColors)
    {
        ushort c0 = (ushort)(data[pos] | (data[pos + 1] << 8));
        ushort c1 = (ushort)(data[pos + 2] | (data[pos + 3] << 8));
        var (r0, g0, b0) = Unpack565(c0);
        var (r1, g1, b1) = Unpack565(c1);

        outColors[0] = (r0, g0, b0);
        outColors[1] = (r1, g1, b1);
        if (c0 > c1)
        {
            outColors[2] = (Lerp(r0, r1, 1, 3), Lerp(g0, g1, 1, 3), Lerp(b0, b1, 1, 3));
            outColors[3] = (Lerp(r0, r1, 2, 3), Lerp(g0, g1, 2, 3), Lerp(b0, b1, 2, 3));
        }
        else
        {
            // 3-color + transparent-black mode - valid for BC1 and for BC3's color block too
            // (the format doesn't forbid it, even though most BC3 encoders avoid it since alpha
            // already carries transparency); color 3 is black with alpha forced to 0 by the
            // caller not applying here - BC3's own alpha block already handles real transparency,
            // so this just matches the documented fallback exactly.
            outColors[2] = (Lerp(r0, r1, 1, 2), Lerp(g0, g1, 1, 2), Lerp(b0, b1, 1, 2));
            outColors[3] = (0, 0, 0);
        }
    }

    static byte ColorIndexAt(byte[] data, int colorBlockStart, int pixelIndex)
    {
        uint indices = (uint)(data[colorBlockStart + 4] | (data[colorBlockStart + 5] << 8)
            | (data[colorBlockStart + 6] << 16) | (data[colorBlockStart + 7] << 24));
        return (byte)((indices >> (2 * pixelIndex)) & 0x3);
    }

    static byte Lerp(byte a, byte b, int weight, int total) => (byte)(((total - weight) * a + weight * b) / total);

    static (byte R, byte G, byte B) Unpack565(ushort c)
    {
        int r5 = (c >> 11) & 0x1F, g6 = (c >> 5) & 0x3F, b5 = c & 0x1F;
        byte r = (byte)((r5 << 3) | (r5 >> 2));
        byte g = (byte)((g6 << 2) | (g6 >> 4));
        byte b = (byte)((b5 << 3) | (b5 >> 2));
        return (r, g, b);
    }
}
