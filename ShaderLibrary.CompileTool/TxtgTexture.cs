using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShaderLibrary.CompileTool
{
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
        ASTC_8x6_UNORM,
        ASTC_8x6_SRGB,
        ASTC_8x5_UNORM,
        ASTC_6x6_UNORM,
        ASTC_5x5_UNORM,
        R8G8B8A8_UNORM,
        R8G8B8A8_SRGB,
        R16G16B16A16_FLOAT,
    }

    public class TxtgSurface
    {
        public int ArrayLevel;
        public int MipLevel;
        public byte[] Data = Array.Empty<byte>(); // deswizzled, linear block-compressed data
    }

    /// <summary>
    /// A texture as the rest of preparation uses it: a size, a format and its surfaces as linear GPU-native blocks. A <c>.txtg</c> is read
    /// with TxtgSharp, which also deswizzles it; other containers (<see cref="TexToGo"/>'s BNTX path) fill the same shape.
    /// </summary>
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
        public byte[] CompSelect = [0, 1, 2, 3];

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
            TxtgFormat.ASTC_8x6_UNORM => (16, 8, 6),
            TxtgFormat.ASTC_8x6_SRGB => (16, 8, 6),
            TxtgFormat.ASTC_8x5_UNORM => (16, 8, 5),
            TxtgFormat.ASTC_6x6_UNORM => (16, 6, 6),
            TxtgFormat.ASTC_5x5_UNORM => (16, 5, 5),
            TxtgFormat.R8G8B8A8_UNORM => (4, 1, 1),
            TxtgFormat.R8G8B8A8_SRGB => (4, 1, 1),
            TxtgFormat.R16G16B16A16_FLOAT => (8, 1, 1),
            _ => throw new NotSupportedException($"Unhandled TxtgFormat {format}"),
        };

        /// <param name="surfaces">How many surfaces, in file order, to deswizzle; 0 reads the header only.</param>
        public static TxtgTexture Load(string path, int surfaces = int.MaxValue) =>
            FromFile(TxtgSharp.TxtgFile.FromFile(path), surfaces);

        public static TxtgTexture Load(Stream stream, int surfaces = int.MaxValue)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return FromFile(TxtgSharp.TxtgFile.FromBytes(copy.ToArray()), surfaces);
        }

        static TxtgTexture FromFile(TxtgSharp.TxtgFile file, int surfaces)
        {
            var (red, green, blue, alpha) = file.ChannelSelectors;
            var tex = new TxtgTexture
            {
                Width = file.Width,
                Height = file.Height,
                Depth = file.LayerCount,
                ArrayCount = file.LayerCount,
                MipCount = file.MipCount,
                Format = Map(file),
                CompSelect = [red, green, blue, alpha],
            };

            foreach (var surface in file.Surfaces.Take(Math.Clamp(surfaces, 0, file.Surfaces.Count)))
                tex.Surfaces.Add(new TxtgSurface { ArrayLevel = surface.ArrayIndex, MipLevel = surface.MipLevel, Data = surface.Data });
            return tex;
        }

        // ASTC's footprint comes from the file's block info, not from the format code: terrain textures share a code with the 4x4 ones.
        static TxtgFormat Map(TxtgSharp.TxtgFile file)
        {
            bool srgb = TxtgSharp.TxtgFormats.IsSrgb(file.Format);
            var block = file.BlockInfo;
            return file.Format switch
            {
                TxtgSharp.TxtgFormat.Bc1Unorm => TxtgFormat.BC1_UNORM,
                TxtgSharp.TxtgFormat.Bc1UnormSrgb => TxtgFormat.BC1_UNORM_SRGB,
                TxtgSharp.TxtgFormat.Bc3UnormSrgb => TxtgFormat.BC3_UNORM_SRGB,
                TxtgSharp.TxtgFormat.Bc4Unorm => TxtgFormat.BC4_UNORM,
                TxtgSharp.TxtgFormat.Bc5Unorm => TxtgFormat.BC5_UNORM,
                TxtgSharp.TxtgFormat.Bc7Unorm => TxtgFormat.BC7_UNORM,
                TxtgSharp.TxtgFormat.R8G8B8A8Unorm => TxtgFormat.R8G8B8A8_UNORM,
                _ when TxtgSharp.TxtgFormats.IsAstc(file.Format) => (block.Width, block.Height, srgb) switch
                {
                    (4, 4, false) => TxtgFormat.ASTC_4x4_UNORM,
                    (4, 4, true) => TxtgFormat.ASTC_4x4_SRGB,
                    (8, 8, false) => TxtgFormat.ASTC_8x8_UNORM,
                    (8, 8, true) => TxtgFormat.ASTC_8x8_SRGB,
                    (8, 6, false) => TxtgFormat.ASTC_8x6_UNORM,
                    (8, 6, true) => TxtgFormat.ASTC_8x6_SRGB,
                    (8, 5, false) => TxtgFormat.ASTC_8x5_UNORM,
                    (6, 6, false) => TxtgFormat.ASTC_6x6_UNORM,
                    (5, 5, false) => TxtgFormat.ASTC_5x5_UNORM,
                    _ => throw new NotSupportedException($"ASTC {block.Width}x{block.Height} ({(srgb ? "sRGB" : "unorm")}) has no TxtgFormat."),
                },
                _ => throw new NotSupportedException($"TXTG format {file.FormatName} (0x{file.RawFormatCode:X}) has no TxtgFormat."),
            };
        }
    }
}
