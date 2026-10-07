using UAssetAPI.ExportTypes;

namespace AbsolverModTool.Core;

/// <summary>Decodes a <c>Texture2D</c> asset's own mip-0 pixel data straight out of
/// <see cref="Export.Extras"/> - the raw bytes UAssetAPI leaves unparsed after a texture's tagged
/// properties end, since UE4 serializes <c>FTexturePlatformData</c> with a hand-rolled binary
/// format outside the normal tagged-property system (no UAssetAPI support for it as of 1.1.0).
///
/// Reverse-engineered 2026-09-12 by hex-diffing real game textures (no public format reference
/// used - UAssetAPI has no Texture2D-specific export type, and UAssetGUI doesn't decode pixels
/// either) and confirmed by rendering the result and looking at it. Layout, all offsets counted
/// from the start of <c>Extras</c>:
///
/// <code>
/// 0..19   : 5 unidentified int32 fields (version/strip-flags/GUID-ish data - never needed to
///           know what these mean, only that they're a fixed 20-byte block before SizeX)
/// 20      : int32 SizeX
/// 24      : int32 SizeY
/// 28      : int32 (unidentified, always 1 in every sample seen - likely NumSlices/array size)
/// 32      : int32 PixelFormatStrLen, followed immediately by that many ASCII bytes
///           (null-terminated, e.g. "PF_B8G8R8A8\0") - this is a real FString, same shape as
///           this project's own save-file FStr, just without the UTF-16/negative-length case
///           (texture format names are always short ASCII).
/// (end)+0 : int32 (unidentified, always 0 in every sample seen)
/// (end)+4 : int32 NumMips
/// (end)+8..+31 : 6 more int32 fields describing mip 0's own FByteBulkData header (flags, an
///           unidentified field, and the payload size duplicated twice - the duplication is what
///           made this block reliably identifiable while reverse-engineering it) - a fixed
///           32-byte block confirmed identical whether the texture has 1 mip or 9, i.e. it does
///           not depend on NumMips.
/// (end)+32 : mip 0's raw pixel bytes, row-major - either SizeX * SizeY * BytesPerPixel of them
///           (uncompressed formats) or a tightly-packed BC1/BC3 block stream (compressed formats,
///           see <see cref="Bc"/>).
/// </code>
///
/// Only confirmed for **inline** mip 0 data - true of every icon/picto texture checked in this
/// game, compressed or not (attack pictos and simple item pictos are uncompressed
/// <c>PF_B8G8R8A8</c>; equipment/dye icons are commonly inline <c>PF_DXT5</c>, e.g. a 256x256 belt
/// icon under <c>UI/Items/Gears/...</c>). Larger 3D-model textures (equipment diffuse/normal/
/// specular maps, 1024x1024+) usually store their largest mips out-of-line in a companion
/// <c>.ubulk</c> file instead - deliberately NOT supported (no bulk-data-file reading):
/// <see cref="TryDecode"/> returns null rather than guess at a layout not confirmed against a real
/// sample.</summary>
public static class TextureDecoder
{
    /// <summary><see cref="Rgba"/> is <see cref="Width"/> * <see cref="Height"/> * 4 bytes,
    /// row-major, standard R-G-B-A byte order (not the source format's own channel order).</summary>
    public record DecodedTexture(int Width, int Height, byte[] Rgba);

    static readonly Dictionary<string, int> UncompressedFormats = new()
    {
        ["PF_B8G8R8A8"] = 4,
        ["PF_R8G8B8A8"] = 4,
        ["PF_A8R8G8B8"] = 4,
        ["PF_G8"] = 1,
    };

    /// <summary>Returns null (never throws) when the export isn't a recognized, supported texture
    /// layout - an unrecognized pixel format, out-of-line bulk data, corrupt data, or anything
    /// else that doesn't match every real sample this was verified against. Never produces a
    /// "best guess" image from unconfirmed assumptions.</summary>
    public static DecodedTexture? TryDecode(NormalExport export)
    {
        var b = export.Extras;
        if (b.Length < 36) return null;

        int sizeX = BitConverter.ToInt32(b, 20);
        int sizeY = BitConverter.ToInt32(b, 24);
        if (sizeX is <= 0 or > 8192 || sizeY is <= 0 or > 8192) return null;

        int strLen = BitConverter.ToInt32(b, 32);
        if (strLen is <= 0 or > 64 || 36 + strLen > b.Length) return null;
        if (b[36 + strLen - 1] != 0) return null; // not null-terminated - not the format we expect
        string pixelFormat = System.Text.Encoding.ASCII.GetString(b, 36, strLen - 1);

        int pixelDataOffset = 32 + 4 + strLen + 32; // fixed trailer width - see class doc

        if (UncompressedFormats.TryGetValue(pixelFormat, out int bytesPerPixel))
        {
            long payloadLength = (long)sizeX * sizeY * bytesPerPixel;
            if (pixelDataOffset < 0 || pixelDataOffset + payloadLength > b.Length) return null;
            return new DecodedTexture(sizeX, sizeY, DecodeUncompressed(b, pixelDataOffset, sizeX, sizeY, pixelFormat, bytesPerPixel));
        }

        if (pixelFormat is "PF_DXT1" or "PF_DXT5")
        {
            bool hasAlpha = pixelFormat == "PF_DXT5";
            long blocksX = (sizeX + 3) / 4, blocksY = (sizeY + 3) / 4;
            long payloadLength = blocksX * blocksY * (hasAlpha ? 16 : 8);
            if (pixelDataOffset < 0 || pixelDataOffset + payloadLength > b.Length) return null;
            return new DecodedTexture(sizeX, sizeY, Bc.Decompress(b, pixelDataOffset, sizeX, sizeY, hasAlpha));
        }

        return null;
    }

    /// <summary>Overwrites an uncompressed BGRA texture's pixels in place - the cheap way to ship a new
    /// icon: copy an existing icon asset of the same size, swap the pixels, rename it. No UE cook, so
    /// every texture setting the game expects (format, no mips, UI group) is inherited from a real asset.
    /// <paramref name="bgra"/> is raw B-G-R-A bytes, row-major, exactly width * height * 4 long.</summary>
    public static void ReplacePixels(NormalExport export, byte[] bgra)
    {
        var b = export.Extras;
        var current = TryDecode(export) ?? throw new InvalidOperationException("Template is not a texture layout this tool understands.");
        int strLen = BitConverter.ToInt32(b, 32);
        string pixelFormat = System.Text.Encoding.ASCII.GetString(b, 36, strLen - 1);
        if (pixelFormat != "PF_B8G8R8A8")
            throw new NotSupportedException($"Template is {pixelFormat}; only uncompressed PF_B8G8R8A8 templates can take new pixels.");
        int expected = current.Width * current.Height * 4;
        if (bgra.Length != expected)
            throw new ArgumentException($"Pixel data is {bgra.Length} bytes but the {current.Width}x{current.Height} template needs exactly {expected}.");
        Buffer.BlockCopy(bgra, 0, b, 32 + 4 + strLen + 32, expected);
    }

    static byte[] DecodeUncompressed(byte[] b, int offset, int sizeX, int sizeY, string pixelFormat, int bytesPerPixel)
    {
        var rgba = new byte[sizeX * sizeY * 4];
        for (int i = 0; i < sizeX * sizeY; i++)
        {
            int src = offset + i * bytesPerPixel;
            int dst = i * 4;
            switch (pixelFormat)
            {
                case "PF_B8G8R8A8":
                    rgba[dst] = b[src + 2]; rgba[dst + 1] = b[src + 1]; rgba[dst + 2] = b[src]; rgba[dst + 3] = b[src + 3];
                    break;
                case "PF_R8G8B8A8":
                    rgba[dst] = b[src]; rgba[dst + 1] = b[src + 1]; rgba[dst + 2] = b[src + 2]; rgba[dst + 3] = b[src + 3];
                    break;
                case "PF_A8R8G8B8":
                    rgba[dst] = b[src + 1]; rgba[dst + 1] = b[src + 2]; rgba[dst + 2] = b[src + 3]; rgba[dst + 3] = b[src];
                    break;
                case "PF_G8":
                    rgba[dst] = rgba[dst + 1] = rgba[dst + 2] = b[src]; rgba[dst + 3] = 255;
                    break;
            }
        }
        return rgba;
    }
}
