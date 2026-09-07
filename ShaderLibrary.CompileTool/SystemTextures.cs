using System;
using System.IO;
using Syroot.NintenTools.NSW.Bntx;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// "System" textures a compiled shader's sampler reflection names (e.g. <c>cTex_Proc3DNoise</c>)
    /// reference but that don't belong to any material's own <c>TextureRefs</c> - shared, global
    /// resources every draw needing that effect binds the same way. Most of the ones the "material"
    /// (forward) shading model reads are genuinely DYNAMIC, camera/world-state-dependent render
    /// targets Marrow has no equivalent system for at all (shadow cascades, sky-island shadows, the
    /// Depths' "minus field" darkness/light maps, terrain streaming, sky atmospheric scattering) -
    /// there is no romfs file to find for those, only a whole subsystem Marrow doesn't implement.
    ///
    /// <c>cTex_Proc3DNoise</c> is the one confirmed exception: a genuine static asset,
    /// <c>TexToGo/3DWorleyPerlinNoise_Fi.bntx.zs</c> - a real 64x64x64 3D (volume) BC4_UNORM
    /// texture, not a per-frame render target. Unlike every other texture this codebase handles
    /// (all <c>.txtg</c>, TotK's own wrapper), this one ships as a plain zstd-compressed <c>.bntx</c>
    /// - the vanilla Switch container format, parsed here via the same <c>Syroot.NintenTools.NSW.Bntx</c>
    /// library <c>BfresLibrary</c> already pulls in for icon extraction (see <c>--dump-bntx</c>).
    /// </summary>
    public static class SystemTextures
    {
        const string NoiseAssetPath = "3DWorleyPerlinNoise_Fi";

        /// <summary>
        /// Extracts <c>cTex_Proc3DNoise</c>'s real data to <c>outDir/Proc3DNoise.r8</c> - raw,
        /// already-deswizzled, already BC4-DECODED single-channel bytes (width*height*depth, no
        /// header) at whatever dimensions the source asset actually has (64x64x64 today) - decoded
        /// on the CPU rather than uploaded as a compressed 3D texture because desktop GL's RGTC
        /// extension does not guarantee 3D compressed texture support the way it does 2D, and a
        /// single small one-off decode is cheap enough that "always works" beats "usually works."
        /// Writes a tiny sidecar <c>Proc3DNoise.dims.txt</c> ("width height depth") next to it so
        /// the runtime doesn't need to re-parse the BNTX just to know the upload size.
        /// </summary>
        public static void ExtractProc3DNoise(string romfsRoot, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string zsPath = Path.Combine(romfsRoot, "TexToGo", NoiseAssetPath + ".bntx.zs");
            string plainPath = Path.Combine(romfsRoot, "TexToGo", NoiseAssetPath + ".bntx");

            byte[] raw = File.Exists(plainPath) ? File.ReadAllBytes(plainPath) : File.ReadAllBytes(zsPath);
            byte[] decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;

            using var ms = new MemoryStream(decompressed);
            var bntx = new BntxFile(ms);
            var tex = bntx.Textures[0];
            int width = (int)tex.Width, height = (int)tex.Height, depth = (int)tex.Depth;

            // TextureData is [arraySlice][mipLevel] - mip 0, the only one this needs.
            byte[] swizzled = tex.TextureData[0][0];
            byte[] deswizzledBc4 = TegraX1Deswizzle.Deswizzle3D(swizzled, TxtgFormat.BC4_UNORM, width, height, depth);
            byte[] decoded = DecodeBc4Volume(deswizzledBc4, width, height, depth);

            File.WriteAllBytes(Path.Combine(outDir, "Proc3DNoise.r8"), decoded);
            File.WriteAllText(Path.Combine(outDir, "Proc3DNoise.dims.txt"), $"{width} {height} {depth}");
            Console.WriteLine($"[SystemTextures] cTex_Proc3DNoise <- {NoiseAssetPath}: {width}x{height}x{depth}, {decoded.Length} bytes -> {outDir}");
        }

        /// <summary>
        /// Decodes a BC4 (single-channel RGTC1/ATI1n) volume to plain single-byte-per-texel output,
        /// slice by slice - each Z slice of a 3D BC4 texture is an ordinary independent 2D BC4
        /// image (block compression never spans Z), so this is just the standard BC4 block format
        /// (identical scheme to DXT5's alpha block) applied per slice.
        /// </summary>
        static byte[] DecodeBc4Volume(byte[] bc4Data, int width, int height, int depth)
        {
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
            int sliceBlockBytes = blocksX * blocksY * 8;
            var output = new byte[width * height * depth];

            for (int z = 0; z < depth; z++)
            {
                int sliceOffset = z * sliceBlockBytes;
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        int blockOffset = sliceOffset + (by * blocksX + bx) * 8;
                        DecodeBc4Block(bc4Data, blockOffset, out var palette, out ulong indices);

                        for (int ty = 0; ty < 4; ty++)
                        {
                            int y = by * 4 + ty;
                            if (y >= height) continue;
                            for (int tx = 0; tx < 4; tx++)
                            {
                                int x = bx * 4 + tx;
                                if (x >= width) continue;
                                int texelIndex = ty * 4 + tx;
                                int paletteIndex = (int)((indices >> (texelIndex * 3)) & 0x7);
                                output[(z * height + y) * width + x] = palette[paletteIndex];
                            }
                        }
                    }
                }
            }
            return output;
        }

        static void DecodeBc4Block(byte[] data, int offset, out byte[] palette, out ulong indices)
        {
            byte r0 = data[offset], r1 = data[offset + 1];
            indices = 0;
            for (int i = 0; i < 6; i++)
                indices |= (ulong)data[offset + 2 + i] << (8 * i);

            palette = new byte[8];
            palette[0] = r0;
            palette[1] = r1;
            if (r0 > r1)
            {
                for (int i = 1; i <= 6; i++)
                    palette[1 + i] = (byte)(((6 - i) * r0 + i * r1) / 7);
            }
            else
            {
                for (int i = 1; i <= 4; i++)
                    palette[1 + i] = (byte)(((4 - i) * r0 + i * r1) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }
        }
    }
}
