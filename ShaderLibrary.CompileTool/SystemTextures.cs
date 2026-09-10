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
        /// Extracts the two real 512x512 BC4 masks the <c>agl_cloud</c> shader samples, decoded to
        /// plain R8 (same "always works" reasoning as <see cref="ExtractProc3DNoise"/>, and BC4's
        /// single channel is all the shader reads anyway - the real capture shows these bound with an
        /// RRRR swizzle).
        ///
        /// WHICH TEXTURES, and how confident: a real GPU capture of the game's own cloud draw shows
        /// its four texture slots are all 512x512 <c>BC4_UNORM</c>, RRRR-swizzled, single-channel
        /// masks (two distinct images - one shared by both "base" slots, one for the noise slots).
        /// Their romfs FILENAMES were never recovered from the executable, so these two were
        /// identified by fingerprint instead: scanning all 29,120 TexToGo textures for that exact
        /// 512x512/BC4 signature narrows it to 313, and of those only these two have content that
        /// decodes (checked by decoding the real bytes to an image, not by rendering) to something a
        /// cloud dome would sample:
        ///   - <c>PolarSphereMappingNoise_Fi</c> - literally named for polar sphere mapping, and its
        ///     pixels confirm it: flat uniform bands top and bottom (where a dome's apex and rim
        ///     compress under polar mapping) with streaky cloud-shaped blobs through the middle.
        ///     Used for the BASE slots.
        ///   - <c>VolumeMist03</c> - soft, tileable, mid-grey cloud/Perlin-style turbulence. Used for
        ///     the NOISE slots.
        /// That is strong circumstantial evidence, NOT proof these are the exact two the game binds;
        /// treat them as the best available real candidates rather than confirmed. Both real slots
        /// in the capture reuse one image across their pair, which is why one texture per pair here
        /// matches the real binding pattern.
        /// </summary>
        /// <summary>
        /// Installs the three shipped, captured cloud masks (raw 512x512 BC4 blocks) by decoding
        /// them to the same R8 form <c>CloudDomePass</c> already loads.
        /// </summary>
        /// <remarks>
        /// <b>This is the one place the project ships game data instead of extracting it, and it is
        /// deliberate and temporary</b> - see <c>res/cloud/README.md</c> for the full reasoning. The
        /// short version: these masks are not romfs assets at all (a byte-exact scan of all ~29,000
        /// <c>TexToGo</c> textures finds no match), they are baked at runtime by <c>noise_cloud</c>,
        /// and the parameters that bake would need are not authored anywhere findable. The previous
        /// name-based guesses were confirmed wrong against a capture, with correlations of ~0.01.
        ///
        /// Unlike a captured sky LUT these are safe to ship as a stopgap: they are static, baked
        /// once from static parameters, so they do not vary with time of day or weather the way the
        /// atmosphere does.
        /// </remarks>
        public static bool InstallCapturedCloudMasks(string bc4Dir, string outDir)
        {
            string[] names = { "CloudBase", "CloudNoise", "CloudNoiseBlend" };
            if (!Directory.Exists(bc4Dir) || names.Any(n => !File.Exists(Path.Combine(bc4Dir, n + ".bc4"))))
                return false;

            Directory.CreateDirectory(outDir);
            foreach (string name in names)
            {
                byte[] blocks = File.ReadAllBytes(Path.Combine(bc4Dir, name + ".bc4"));
                // 512x512 BC4 is 8 bytes per 4x4 block; anything else means the file is not what
                // this expects, and silently decoding it would produce plausible-looking garbage.
                const int w = 512, h = 512;
                int expected = (w / 4) * (h / 4) * 8;
                if (blocks.Length != expected)
                {
                    Console.WriteLine($"[SystemTextures] '{name}.bc4' is {blocks.Length} bytes, expected {expected} - skipping.");
                    return false;
                }

                byte[] decoded = DecodeBc4Volume(blocks, w, h, 1);
                File.WriteAllBytes(Path.Combine(outDir, name + ".r8"), decoded);
                File.WriteAllText(Path.Combine(outDir, name + ".dims.txt"), $"{w} {h} 1");
            }
            Console.WriteLine($"[SystemTextures] installed 3 captured cloud masks (512x512) -> {outDir}");
            return true;
        }

        public static void ExtractCloudTextures(string romfsRoot, string outDir)
        {
            Directory.CreateDirectory(outDir);
            ExtractTxtgMask(romfsRoot, outDir, "PolarSphereMappingNoise_Fi", "CloudBase");
            ExtractTxtgMask(romfsRoot, outDir, "VolumeMist03", "CloudNoise");
        }

        /// <summary>
        /// Finds which romfs texture a reference blob actually is, by comparing raw block bytes.
        /// </summary>
        /// <remarks>
        /// This exists because identifying a texture by CONCEPT does not work. The cloud masks were
        /// originally picked by searching <c>TexToGo/</c> for plausible names at the right size and
        /// format, and both picks turned out to be wrong - a capture of the real cloud draw showed
        /// the game binding entirely different textures (byte-comparison correlation ~0.01 against
        /// what had been guessed). Names in this game do not describe contents reliably enough to
        /// identify anything.
        ///
        /// The reference blob is the raw, already-deswizzled block-compressed data for mip 0 - i.e.
        /// exactly what <c>TxtgTexture.Surfaces[0].Data</c> holds, and exactly what RenderDoc's
        /// texture read returns for a BC4 surface, which is what makes the two directly comparable
        /// with no decode step in between.
        /// </remarks>
        public static void FindTxtgByBytes(string romfsRoot, string referencePath, int topN = 8)
        {
            byte[] reference = File.ReadAllBytes(referencePath);
            string dir = Path.Combine(romfsRoot, "TexToGo");
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"[SystemTextures] '{dir}' not found.");
                return;
            }

            // Resumable, because a handful of these files kill the process outright rather than
            // throwing: the zstd decompressor is native and panics on them, which no try/catch can
            // intercept. State lives beside the reference blob - "done" accumulates scores so a
            // restart keeps earlier results, and "skip" records every file already attempted, so
            // the run after a crash steps over the file that caused it. Loop the CLI until it
            // prints a final ranking.
            string donePath = referencePath + ".scan_done.txt";
            string skipPath = referencePath + ".scan_skip.txt";
            var skip = File.Exists(skipPath)
                ? new HashSet<string>(File.ReadAllLines(skipPath), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var results = new List<(double Score, string Name, int W, int H, string Fmt)>();
            if (File.Exists(donePath))
            {
                foreach (string line in File.ReadAllLines(donePath))
                {
                    string[] p = line.Split('\t');
                    if (p.Length == 5 && double.TryParse(p[0], out double sc)
                        && int.TryParse(p[2], out int w) && int.TryParse(p[3], out int h))
                        results.Add((sc, p[1], w, h, p[4]));
                }
            }

            int scanned = 0, considered = 0;
            foreach (string path in Directory.EnumerateFiles(dir, "*.txtg"))
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (!skip.Add(stem))
                    continue;
                File.AppendAllText(skipPath, stem + Environment.NewLine);

                scanned++;
                TxtgTexture tex;
                try { tex = TxtgTexture.Load(path); }
                catch { continue; }
                if (tex.Surfaces.Count == 0)
                    continue;

                byte[] data = tex.Surfaces[0].Data;
                if (data.Length != reference.Length)
                    continue;
                considered++;

                long same = 0;
                for (int i = 0; i < data.Length; i++)
                    if (data[i] == reference[i]) same++;
                double score = same / (double)data.Length;

                results.Add((score, stem, tex.Width, tex.Height, tex.Format.ToString()));
                File.AppendAllText(donePath,
                    $"{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}\t{stem}\t{tex.Width}\t{tex.Height}\t{tex.Format}{Environment.NewLine}");
                if (score >= 0.999)
                {
                    Console.WriteLine($"[SystemTextures] EXACT MATCH: {stem} ({tex.Width}x{tex.Height} {tex.Format})");
                    Console.WriteLine("[SystemTextures] SCAN-COMPLETE");
                    return;
                }
            }

            Console.WriteLine($"[SystemTextures] scanned {scanned} new textures this pass, {considered} with a matching byte length.");
            Console.WriteLine("[SystemTextures] SCAN-COMPLETE");
            foreach (var r in results.OrderByDescending(r => r.Score).Take(topN))
                Console.WriteLine($"   {r.Score,7:P2}  {r.Name}  ({r.W}x{r.H} {r.Fmt})");
        }

        static void ExtractTxtgMask(string romfsRoot, string outDir, string textureName, string outName)
        {
            string path = Path.Combine(romfsRoot, "TexToGo", textureName + ".txtg");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[SystemTextures] '{path}' not found - {outName} mask unavailable, cloud dome will fall back to a flat stand-in.");
                return;
            }

            var tex = TxtgTexture.Load(path);
            if (tex.Format != TxtgFormat.BC4_UNORM)
            {
                Console.WriteLine($"[SystemTextures] '{textureName}' is {tex.Format}, expected BC4_UNORM - skipping.");
                return;
            }

            int width = tex.Width, height = tex.Height;
            // Surfaces[0] is mip 0 and its Data is ALREADY deswizzled by the loader (see
            // TxtgSurface.Data) - deswizzling again here would scramble it.
            byte[] decoded = DecodeBc4Volume(tex.Surfaces[0].Data, width, height, 1);

            File.WriteAllBytes(Path.Combine(outDir, outName + ".r8"), decoded);
            File.WriteAllText(Path.Combine(outDir, outName + ".dims.txt"), $"{width} {height} 1");
            Console.WriteLine($"[SystemTextures] {outName} <- {textureName}: {width}x{height}, {decoded.Length} bytes -> {outDir}");
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
            // The interpolation weights must SUM TO THE DENOMINATOR, or every interpolated entry is
            // systematically too dark. This previously read ((6 - i) * r0 + i * r1) / 7 and
            // ((4 - i) * r0 + i * r1) / 5 - numerators summing to 6 and 4 against denominators of
            // 7 and 5 - which quietly darkened six of the eight palette entries in every block.
            // Caught by decoding the same captured texture with an independent decoder and finding
            // differences of up to 31/255, far past anything rounding could explain. It affected
            // every BC4 surface decoded here, the cTex_Proc3DNoise volume included.
            if (r0 > r1)
            {
                for (int i = 1; i <= 6; i++)
                    palette[1 + i] = (byte)(((7 - i) * r0 + i * r1) / 7);
            }
            else
            {
                for (int i = 1; i <= 4; i++)
                    palette[1 + i] = (byte)(((5 - i) * r0 + i * r1) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }
        }
    }
}
