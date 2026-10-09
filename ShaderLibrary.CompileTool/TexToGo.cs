using System;
using System.IO;
using BntxSharp;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Finds a material texture in <c>TexToGo/</c> by name, whichever container it ships in.
    /// </summary>
    /// <remarks>
    /// Nearly every texture is TotK's own <c>.txtg</c>, but 216 ship as a plain zstd-compressed
    /// <c>.bntx</c> - among them <c>CmnTex_BakeDefault</c>, the baked-lighting texture every static
    /// world object samples as <c>bake0</c> when no area bake overrides it. Looking only for
    /// <c>.txtg</c> left that binding unresolved on thousands of shapes, the renderer bound nothing,
    /// and baked light times whatever happened to be on that unit came out black. A <c>.bntx</c> is
    /// read into the same <see cref="TxtgTexture"/> the rest of preparation already handles.
    /// </remarks>
    public static class TexToGo
    {
        /// <summary>A texture read out of a BNTX, as <see cref="TxtgTexture"/> with its first slice's top mip.</summary>
        public static TxtgTexture FromBntx(BntxTexture source, int surfaces)
        {
            TxtgFormat format = MapFormat(source.Format)
                ?? throw new NotSupportedException($"BNTX format {source.Format} (0x{(uint)source.Format:X}) has no TXTG equivalent here");

            var tex = new TxtgTexture
            {
                Width = source.Width,
                Height = source.Height,
                Depth = 1,
                MipCount = 1,
                ArrayCount = 1,
                Format = format,
                CompSelect = [Channel(source.ChannelTypes[0], 0), Channel(source.ChannelTypes[1], 1), Channel(source.ChannelTypes[2], 2), Channel(source.ChannelTypes[3], 3)],
            };
            if (surfaces <= 0)
                return tex;

            // Preparation only ever uses the first slice's top mip.
            tex.Surfaces.Add(new TxtgSurface
            {
                ArrayLevel = 0,
                MipLevel = 0,
                Data = source.GetDeswizzledData(),
            });
            return tex;
        }

        // By the format's raw code first: BntxSharp's enum names predate some of the codes TotK uses
        // and call them something else entirely (WaterAlb's RGBA8 reads as D32_FLOAT_S8X24_UINT).
        static TxtgFormat? MapFormat(SurfaceFormat format) => (uint)format switch
        {
            0x0B01 => TxtgFormat.R8G8B8A8_UNORM,
            // Named D32_FLOAT_S8X24_UINT by BntxSharp, but WaterAlb's texels under
            // this code are plainly four half floats - colours with alpha 1.0.
            0x1505 => TxtgFormat.R16G16B16A16_FLOAT,
            0x0B06 => TxtgFormat.R8G8B8A8_SRGB,
            0x3301 => TxtgFormat.ASTC_8x6_UNORM,
            0x3306 => TxtgFormat.ASTC_8x6_SRGB,
            0x3201 => TxtgFormat.ASTC_8x5_UNORM,
            0x3101 => TxtgFormat.ASTC_6x6_UNORM,
            0x2F01 => TxtgFormat.ASTC_5x5_UNORM,
            _ => MapFormatByName(format),
        };

        static TxtgFormat? MapFormatByName(SurfaceFormat format) => format switch
        {
            SurfaceFormat.BC1_UNORM => TxtgFormat.BC1_UNORM,
            SurfaceFormat.BC1_SRGB => TxtgFormat.BC1_UNORM_SRGB,
            SurfaceFormat.BC3_SRGB => TxtgFormat.BC3_UNORM_SRGB,
            SurfaceFormat.BC4_UNORM => TxtgFormat.BC4_UNORM,
            SurfaceFormat.BC5_UNORM => TxtgFormat.BC5_UNORM,
            SurfaceFormat.BC7_UNORM => TxtgFormat.BC7_UNORM,
            SurfaceFormat.ASTC_4x4_UNORM => TxtgFormat.ASTC_4x4_UNORM,
            SurfaceFormat.ASTC_4x4_SRGB => TxtgFormat.ASTC_4x4_SRGB,
            SurfaceFormat.ASTC_8x8_UNORM => TxtgFormat.ASTC_8x8_UNORM,
            SurfaceFormat.ASTC_8x8_SRGB => TxtgFormat.ASTC_8x8_SRGB,
            SurfaceFormat.ASTC_8x6_UNORM => TxtgFormat.ASTC_8x6_UNORM,
            SurfaceFormat.ASTC_8x6_SRGB => TxtgFormat.ASTC_8x6_SRGB,
            SurfaceFormat.ASTC_8x5_UNORM => TxtgFormat.ASTC_8x5_UNORM,
            SurfaceFormat.ASTC_6x6_UNORM => TxtgFormat.ASTC_6x6_UNORM,
            SurfaceFormat.ASTC_5x5_UNORM => TxtgFormat.ASTC_5x5_UNORM,
            SurfaceFormat.R8_G8_B8_A8_UNORM => TxtgFormat.R8G8B8A8_UNORM,
            SurfaceFormat.R8_G8_B8_A8_SRGB => TxtgFormat.R8G8B8A8_SRGB,
            _ => null,
        };

        /// <summary>A BNTX channel source as a TXTG component index; a constant zero/one keeps the channel's own.</summary>
        static byte Channel(ChannelType type, byte own) => type switch
        {
            ChannelType.Red => 0,
            ChannelType.Green => 1,
            ChannelType.Blue => 2,
            ChannelType.Alpha => 3,
            _ => own,
        };
    }
}
