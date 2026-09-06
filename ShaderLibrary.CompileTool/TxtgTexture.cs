using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// TotK ships textures as "*.txtg" ("Texture To Go", magic "6PK0") rather than plain .bntx.
    /// This is a from-scratch port of the parsing logic in
    /// C:\Users\dylan\repos\Switch-Toolbox\File_Format_Library\FileFormats\Texture\TXTG.cs (the
    /// user pointed at this file directly as the reference) - trimmed down to just parsing +
    /// deswizzling (no WinForms/editor/save support, none of which this tool needs).
    ///
    /// Container layout (fixed 0x50-byte header, all little-endian):
    ///   u16 HeaderSize (=0x50), u16 Version, char[4] Magic="6PK0", u16 Width, u16 Height,
    ///   u16 Depth, u8 MipCount, u8/u8/u16 unknown+padding, u8 FormatFlag, u32 FormatSetting,
    ///   u8x4 channel swizzle selectors, byte[32] Hash, u16 Format, u16 Unknown3,
    ///   u32 x4 more texture settings (unused here).
    /// Then, for MipCount*ArrayCount surfaces: a small index (u16 ArrayLevel, u8 MipLevel,
    /// u8 pad) per surface, followed by (u32 Size, u32 const-6) per surface, then the raw
    /// surface bytes back to back - each surface individually zstd-compressed with NO
    /// dictionary (confirmed against Switch-Toolbox's Zstb.SDecompress call site: it's called
    /// with no filename context, so its dictionary auto-selection never matches, unlike the
    /// dictionary-compressed .bfsha.zs/.pack.zs files elsewhold in romfs).
    ///
    /// Each decompressed surface is still in the GPU's native Tegra X1 block-linear tiled
    /// layout, not row-major - TegraX1Deswizzle (ported from the same reference, using the
    /// same native tegra_swizzle_x64.dll Switch-Toolbox ships) converts it to a linear buffer
    /// of the block-compressed format's own blocks, ready for direct GPU upload
    /// (glCompressedTexImage2D or equivalent - these formats don't need decoding to RGBA,
    /// they're already GPU-native compressed formats).
    /// </summary>
    public enum TxtgFormat
    {
        BC1_UNORM,
        BC1_UNORM_SRGB,
        BC3_UNORM_SRGB,
        BC4_UNORM,
        BC5_UNORM,
        BC7_UNORM,
        ASTC_4x4_UNORM,
        ASTC_4x4_SRGB,
        ASTC_8x8_UNORM,
        ASTC_8x8_SRGB,
    }

    public class TxtgSurface
    {
        public int ArrayLevel;
        public int MipLevel;
        public byte[] Data = Array.Empty<byte>(); // deswizzled, linear block-compressed data
    }

    public class TxtgTexture
    {
        public int Width;
        public int Height;
        public int Depth;
        public int MipCount;
        public int ArrayCount;
        public TxtgFormat Format;
        public byte[] Hash = Array.Empty<byte>();
        public List<TxtgSurface> Surfaces = new();

        static readonly Dictionary<ushort, TxtgFormat> FormatList = new()
        {
            { 0x101, TxtgFormat.ASTC_4x4_UNORM },
            { 0x102, TxtgFormat.ASTC_8x8_UNORM },
            { 0x105, TxtgFormat.ASTC_8x8_SRGB },
            { 0x109, TxtgFormat.ASTC_4x4_SRGB },
            { 0x202, TxtgFormat.BC1_UNORM },
            { 0x203, TxtgFormat.BC1_UNORM_SRGB },
            { 0x302, TxtgFormat.BC1_UNORM },
            { 0x505, TxtgFormat.BC3_UNORM_SRGB },
            { 0x602, TxtgFormat.BC4_UNORM },
            { 0x606, TxtgFormat.BC4_UNORM },
            { 0x607, TxtgFormat.BC4_UNORM },
            { 0x609, TxtgFormat.BC4_UNORM },
            { 0x702, TxtgFormat.BC5_UNORM },
            { 0x703, TxtgFormat.BC5_UNORM },
            { 0x707, TxtgFormat.BC5_UNORM },
            { 0x709, TxtgFormat.BC5_UNORM },
            { 0x901, TxtgFormat.BC7_UNORM },
        };

        public static (uint bpp, uint blockW, uint blockH) GetFormatInfo(TxtgFormat format) => format switch
        {
            TxtgFormat.BC1_UNORM => (8, 4, 4),
            TxtgFormat.BC1_UNORM_SRGB => (8, 4, 4),
            TxtgFormat.BC3_UNORM_SRGB => (16, 4, 4),
            TxtgFormat.BC4_UNORM => (8, 4, 4),
            TxtgFormat.BC5_UNORM => (16, 4, 4),
            TxtgFormat.BC7_UNORM => (16, 4, 4),
            TxtgFormat.ASTC_4x4_UNORM => (16, 4, 4),
            TxtgFormat.ASTC_4x4_SRGB => (16, 4, 4),
            TxtgFormat.ASTC_8x8_UNORM => (16, 8, 8),
            TxtgFormat.ASTC_8x8_SRGB => (16, 8, 8),
            _ => throw new NotSupportedException($"Unhandled TxtgFormat {format}"),
        };

        struct SurfaceHeader
        {
            public ushort ArrayLevel;
            public byte MipLevel;
            public uint Size;
        }

        public static TxtgTexture Load(string path)
        {
            using var fs = File.OpenRead(path);
            return Load(fs);
        }

        public static TxtgTexture Load(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

            ushort headerSize = reader.ReadUInt16();
            ushort version = reader.ReadUInt16();
            byte[] magicBytes = reader.ReadBytes(4);
            string magic = System.Text.Encoding.ASCII.GetString(magicBytes);
            if (magic != "6PK0")
                throw new InvalidDataException($"Not a TXTG file - expected magic \"6PK0\", got \"{magic}\"");

            var tex = new TxtgTexture();
            tex.Width = reader.ReadUInt16();
            tex.Height = reader.ReadUInt16();
            tex.Depth = reader.ReadUInt16();
            tex.MipCount = reader.ReadByte();
            reader.ReadByte(); // Unknown1
            reader.ReadByte(); // Unknown2
            reader.ReadUInt16(); // Padding

            reader.ReadByte(); // FormatFlag
            reader.ReadUInt32(); // FormatSetting

            reader.ReadByte(); // CompSelectR
            reader.ReadByte(); // CompSelectG
            reader.ReadByte(); // CompSelectB
            reader.ReadByte(); // CompSelectA

            tex.Hash = reader.ReadBytes(32);

            ushort formatCode = reader.ReadUInt16();
            reader.ReadUInt16(); // Unknown3

            reader.ReadUInt32(); // TextureSetting1
            uint textureSetting2 = reader.ReadUInt32();
            reader.ReadUInt32(); // TextureSetting3
            reader.ReadUInt32(); // TextureSetting4

            if (!FormatList.TryGetValue(formatCode, out TxtgFormat format))
                throw new NotSupportedException($"Unsupported TXTG format code 0x{formatCode:X}");
            tex.Format = format;

            // Same "dumb hack" as the reference: terrain textures are 8x8 ASTC but share the
            // 0x101 (normally 4x4 ASTC) format code - TextureSetting2 disambiguates. 32628
            // (8x5 ASTC) isn't in TxtgFormat yet - none of the materials dumped so far (sword,
            // creatures) hit it, only terrain does.
            if (textureSetting2 == 32628)
                throw new NotSupportedException("ASTC_8x5_UNORM (terrain textures) not implemented yet.");
            if (textureSetting2 == 32631) tex.Format = TxtgFormat.ASTC_8x8_UNORM;

            tex.ArrayCount = tex.Depth;
            int surfaceCount = tex.MipCount * tex.ArrayCount;

            reader.BaseStream.Seek(headerSize, SeekOrigin.Begin);

            var headers = new SurfaceHeader[surfaceCount];
            for (int i = 0; i < surfaceCount; i++)
            {
                headers[i].ArrayLevel = reader.ReadUInt16();
                headers[i].MipLevel = reader.ReadByte();
                reader.ReadByte(); // always 1
            }
            for (int i = 0; i < surfaceCount; i++)
            {
                headers[i].Size = reader.ReadUInt32();
                reader.ReadUInt32(); // always 6
            }

            using var decompressor = new ZstdNet.Decompressor();

            for (int i = 0; i < surfaceCount; i++)
            {
                byte[] compressed = reader.ReadBytes((int)headers[i].Size);
                byte[] raw = decompressor.Unwrap(compressed);

                int mipWidth = Math.Max(1, tex.Width >> headers[i].MipLevel);
                int mipHeight = Math.Max(1, tex.Height >> headers[i].MipLevel);
                byte[] deswizzled = TegraX1Deswizzle.Deswizzle(raw, format, tex.Height, mipWidth, mipHeight);

                tex.Surfaces.Add(new TxtgSurface
                {
                    ArrayLevel = headers[i].ArrayLevel,
                    MipLevel = headers[i].MipLevel,
                    Data = deswizzled,
                });
            }

            return tex;
        }
    }

    /// <summary>
    /// Minimal port of TegraX1Swizzle.cs's block-linear deswizzle path (the only path TXTG
    /// actually uses - LinearTileMode is never set for these files) via the same native
    /// tegra_swizzle_x64.dll Switch-Toolbox ships (https://github.com/ScanMountGoat/tegra_swizzle).
    /// Trimmed to x64-only (this tool only runs as a 64-bit process) and to just the one
    /// operation needed: deswizzle a single mip's surface data.
    /// </summary>
    static class TegraX1Deswizzle
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BlockDim
        {
            public ulong width, height, depth;
        }

        [DllImport("tegra_swizzle_x64", EntryPoint = "deswizzle_block_linear")]
        static extern unsafe void DeswizzleBlockLinear(ulong width, ulong height, ulong depth,
            byte* source, ulong sourceLength, byte* destination, ulong destinationLength,
            ulong blockHeight, ulong bytesPerPixel);

        [DllImport("tegra_swizzle_x64", EntryPoint = "block_height_mip0")]
        static extern ulong BlockHeightMip0(ulong height);

        [DllImport("tegra_swizzle_x64", EntryPoint = "mip_block_height")]
        static extern ulong MipBlockHeight(ulong mipHeightInBlocks, ulong blockHeightMip0);

        static uint DivRoundUp(uint n, uint d) => (n + d - 1) / d;

        /// <summary>
        /// fullTextureHeight is the texture's mip-0 height (pixels) - blockHeightMip0 is always
        /// derived from the FULL texture, not the current mip, then reduced per mip via
        /// MipBlockHeight. Getting this backwards (deriving blockHeightMip0 from the mip's own
        /// height) silently produces a wrong-but-plausible-looking deswizzle for every mip except
        /// mip 0.
        /// </summary>
        public static byte[] Deswizzle(byte[] swizzledData, TxtgFormat format, int fullTextureHeight, int mipWidth, int mipHeight)
        {
            var (bpp, blockW, blockH) = TxtgTexture.GetFormatInfo(format);

            uint widthInBlocks = DivRoundUp((uint)mipWidth, blockW);
            uint heightInBlocks = DivRoundUp((uint)mipHeight, blockH);
            uint fullHeightInBlocks = DivRoundUp((uint)fullTextureHeight, blockH);

            ulong blockHeightMip0 = BlockHeightMip0(fullHeightInBlocks);
            ulong mipBlockHeight = MipBlockHeight(heightInBlocks, blockHeightMip0);

            var output = new byte[widthInBlocks * heightInBlocks * bpp];

            unsafe
            {
                fixed (byte* srcPtr = swizzledData)
                fixed (byte* dstPtr = output)
                {
                    DeswizzleBlockLinear(widthInBlocks, heightInBlocks, 1,
                        srcPtr, (ulong)swizzledData.Length, dstPtr, (ulong)output.Length,
                        mipBlockHeight, bpp);
                }
            }

            return output;
        }
    }
}
