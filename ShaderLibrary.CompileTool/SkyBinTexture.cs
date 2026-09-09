using System;
using System.IO;
using System.Runtime.InteropServices;
using SarcLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// <c>Env/GameScene.Nin_NX_NVN.genvb.zs</c>'s <c>res/master_field.skybin</c> - the real
    /// precomputed sky-scattering lookup table <c>agl::pfx::Sky</c> loads, found and reverse
    /// engineered this session (no prior documentation existed for this extension anywhere).
    ///
    /// CONFIRMED STRUCTURE (independently verified: the alpha-channel peak of every one of the 8
    /// tested slices matches an externally-sourced technical description to 4 decimal places when
    /// decoded with the exact dimensions/format below, AND this project's own already-trusted
    /// native Tegra deswizzler - <see cref="TegraX1Deswizzle"/>, used elsewhere for real material
    /// textures - reproduces byte-identical results to an independent from-scratch Python port of
    /// the GOB math when called PER SLICE):
    ///   - A headerless raw <c>RGBA16F</c> volume texture, Tegra X1 block-linear swizzled.
    ///   - 64 (width) x 64 (height) x 37 (depth) texels, 8 bytes/texel (4 x half-float channels).
    ///   - File size 1,212,416 bytes = 64*64*37*8 exactly.
    ///   - Depth slices: 0 = all-zero boundary; 1-3 = ground/horizon transition; 4-11/12-19/20-27/
    ///     28-35 = four 8-slice "climate group" blocks (their own distinct alpha/fog-extinction
    ///     ceiling each, confirmed by direct measurement); 36 = upper-atmosphere cap.
    ///   - CONFIRMED (via the same cross-check, both against the trusted native deswizzler AND
    ///     visually): each 64x64 slice is NOT a smooth gradient - it's an 8x8 grid of near-flat,
    ///     hard-edged blocks (exactly one Tegra GOB - 8x8 texels - per cell), i.e. a genuine
    ///     DISCRETE lookup table baked at GOB granularity, not meant to be read pixel-by-pixel.
    ///
    /// NOT YET CONFIRMED (a reasoned hypothesis from the visual evidence, not verified against any
    /// game code): which axis of the 8x8 grid is which. VIEW ELEVATION (row 0 = zenith, row 7 =
    /// horizon) and SUN ELEVATION/time-of-day (column 0 = low/no sun, column 7 = high sun) is the
    /// working hypothesis - see <c>Marrow.Core.Rendering.SkyBinLut</c>/<c>Pipeline.BackgroundPass</c>
    /// for how it's actually used.
    ///
    /// Systematically checked (row-vs-luma and column-vs-blueness Pearson correlation, per slice,
    /// across all 37) rather than eyeballing one slice: for the group Marrow actually uses (Group 0,
    /// "Surface Sky", slices 4-11) BOTH axes hold strongly and consistently - row/luma correlation
    /// +0.95 to +0.99, column/blueness correlation +0.74 to +0.80, every slice. Column direction
    /// (higher column = more blue) also matches real Rayleigh physics: a low sun's longer
    /// atmospheric path scatters out more blue, leaving the residual light redder - so "column 7 =
    /// high sun = bluer" is not just a data pattern, it's physically the right way around.
    ///
    /// Interesting wrinkle, NOT yet acted on: row/luma correlation flips sign exactly at slice 20 -
    /// the Group 2 ("Clear/Sky Islands") boundary - and stays negative (row 0 brighter, row 7
    /// darker) through Group 3 and the upper-atmosphere cap. That's a clean, group-boundary-aligned
    /// flip, not noise, so it's read as a genuine per-climate authored difference (plausibly real:
    /// an overcast/storm sky's horizon band sitting under thicker low cloud than its zenith) rather
    /// than evidence the row axis means something different per group. Only relevant once Marrow
    /// gains a reason to sample a group other than 0.
    /// </summary>
    public static class SkyBinTexture
    {
        public const int Width = 64, Height = 64, Depth = 37, BytesPerTexel = 8;
        public const int CellCount = 8; // one GOB (8x8 texels) per discrete lookup cell, both axes

        [DllImport("tegra_swizzle_x64", EntryPoint = "deswizzle_block_linear")]
        static extern unsafe void DeswizzleBlockLinear(ulong width, ulong height, ulong depth,
            byte* source, ulong sourceLength, byte* destination, ulong destinationLength,
            ulong blockHeight, ulong bytesPerPixel);

        [DllImport("tegra_swizzle_x64", EntryPoint = "block_height_mip0")]
        static extern ulong BlockHeightMip0(ulong height);

        /// <summary>
        /// Opens <c>Env/GameScene.Nin_NX_NVN.genvb.zs</c>, extracts <c>res/master_field.skybin</c>,
        /// deswizzles and block-averages it, and writes the compact result (see
        /// <see cref="WriteLut"/>) to <paramref name="outDir"/>/sky_lut.bin.
        /// </summary>
        public static void ExtractMasterField(string romfsRoot, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string genvbPath = Path.Combine(romfsRoot, "Env", "GameScene.Nin_NX_NVN.genvb.zs");
            byte[] raw = File.ReadAllBytes(genvbPath);
            byte[] decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var sarc = Sarc.FromBinary(new ArraySegment<byte>(decompressed));

            byte[]? skybinBytes = null;
            foreach (var (path, data) in sarc)
            {
                if (path == "res/master_field.skybin")
                {
                    skybinBytes = data.ToArray();
                    break;
                }
            }
            if (skybinBytes is null)
                throw new FileNotFoundException("'res/master_field.skybin' not found in genvb archive");

            float[,,,] lut = LoadAndReduce(skybinBytes);
            WriteLut(lut, Path.Combine(outDir, "sky_lut.bin"));
            Console.WriteLine($"[SkyBinTexture] master_field.skybin -> {Depth}x{CellCount}x{CellCount} RGBA LUT -> {outDir}");
        }

        /// <summary>
        /// Deswizzles all 37 slices (PER SLICE, depth=1 each - see class remarks: a single call
        /// covering the whole depth=37 in one shot silently produced slices that repeated
        /// identically every 8 indices, while per-slice calls exactly reproduced an independent
        /// from-scratch decode; the root cause in the native wrapper's depth handling for this
        /// specific format/size wasn't chased down further since per-slice is simple and verified)
        /// and reduces each 64x64 slice's 8x8 grid of near-flat blocks to one averaged RGBA value
        /// per cell - <c>[z, cellY, cellX, channel]</c>, channel 0..3 = R,G,B,A.
        /// </summary>
        public static float[,,,] LoadAndReduce(byte[] skybinBytes)
        {
            int expected = Width * Height * Depth * BytesPerTexel;
            if (skybinBytes.Length != expected)
                throw new InvalidDataException($"Unexpected .skybin size {skybinBytes.Length}, expected {expected}");

            ulong blockHeightMip0 = BlockHeightMip0(Height);
            int sliceBytes = Width * Height * BytesPerTexel;
            var deswizzled = new byte[sliceBytes];
            var result = new float[Depth, CellCount, CellCount, 4];

            for (int z = 0; z < Depth; z++)
            {
                unsafe
                {
                    fixed (byte* srcPtr = &skybinBytes[z * sliceBytes])
                    fixed (byte* dstPtr = deswizzled)
                    {
                        DeswizzleBlockLinear(Width, Height, 1,
                            srcPtr, (ulong)sliceBytes, dstPtr, (ulong)sliceBytes,
                            blockHeightMip0, BytesPerTexel);
                    }
                }

                int cellPixels = Width / CellCount; // 8
                for (int cellY = 0; cellY < CellCount; cellY++)
                {
                    for (int cellX = 0; cellX < CellCount; cellX++)
                    {
                        double r = 0, g = 0, b = 0, a = 0;
                        int n = 0;
                        for (int ly = 0; ly < cellPixels; ly++)
                        {
                            int py = cellY * cellPixels + ly;
                            for (int lx = 0; lx < cellPixels; lx++)
                            {
                                int px = cellX * cellPixels + lx;
                                int off = (py * Width + px) * BytesPerTexel;
                                r += ReadHalf(deswizzled, off + 0);
                                g += ReadHalf(deswizzled, off + 2);
                                b += ReadHalf(deswizzled, off + 4);
                                a += ReadHalf(deswizzled, off + 6);
                                n++;
                            }
                        }
                        result[z, cellY, cellX, 0] = (float)(r / n);
                        result[z, cellY, cellX, 1] = (float)(g / n);
                        result[z, cellY, cellX, 2] = (float)(b / n);
                        result[z, cellY, cellX, 3] = (float)(a / n);
                    }
                }
            }
            return result;
        }

        static float ReadHalf(byte[] data, int byteOffset)
        {
            ushort bits = (ushort)(data[byteOffset] | (data[byteOffset + 1] << 8));
            float v = (float)BitConverter.UInt16BitsToHalf(bits);
            return float.IsFinite(v) ? v : 0f;
        }

        /// <summary>Raw binary: "SKYL" magic, then Depth/CellCount as int32, then Depth*CellCount*CellCount*4 float32 values in [z,cellY,cellX,channel] row-major order - small enough (37KB) that a plain flat dump is simpler than any real format.</summary>
        static void WriteLut(float[,,,] lut, string path)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            w.Write(new[] { 'S', 'K', 'Y', 'L' });
            w.Write(Depth);
            w.Write(CellCount);
            for (int z = 0; z < Depth; z++)
                for (int y = 0; y < CellCount; y++)
                    for (int x = 0; x < CellCount; x++)
                        for (int c = 0; c < 4; c++)
                            w.Write(lut[z, y, x, c]);
        }
    }
}
