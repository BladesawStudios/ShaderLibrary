using System;
using System.IO;
using BntxSharp;
using TexSharp;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Textures a compiled shader's sampler reflection names but that belong to no material's own <c>TextureRefs</c>:
    /// shared, static assets every draw of an effect binds the same way, decoded here to plain bytes for the renderer.
    /// The dynamic ones (shadow cascades, sky-island shadows, terrain streaming) are render targets with no file to find.
    /// </summary>
    public static class SystemTextures
    {
        const string NoiseAssetPath = "3DWorleyPerlinNoise_Fi";

        /// <summary>
        /// <c>cTex_Proc3DNoise</c>: <c>TexToGo/3DWorleyPerlinNoise_Fi.bntx.zs</c>, a 64x64x64 BC4 volume. Written as
        /// <c>Proc3DNoise.r8</c> (one byte per texel) with a <c>Proc3DNoise.dims.txt</c> of "width height depth", decoded on the
        /// CPU because desktop GL does not guarantee compressed 3D textures.
        /// </summary>
        public static void ExtractProc3DNoise(string romfsRoot, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string zsPath = Path.Combine(romfsRoot, "TexToGo", NoiseAssetPath + ".bntx.zs");
            string plainPath = Path.Combine(romfsRoot, "TexToGo", NoiseAssetPath + ".bntx");

            byte[] raw = File.Exists(plainPath) ? File.ReadAllBytes(plainPath) : File.ReadAllBytes(zsPath);
            byte[] data = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;

            var tex = BntxFile.Load(data).Textures[0];
            int width = tex.Width, height = tex.Height, depth = tex.Depth;
            byte[] blocks = tex.GetDeswizzledData();

            int sliceBytes = blocks.Length / depth;
            var decoded = new byte[width * height * depth];
            for (int z = 0; z < depth; z++)
                RedChannel(TextureFormat.Bc4, blocks.AsSpan(z * sliceBytes, sliceBytes), width, height, decoded.AsSpan(z * width * height));

            File.WriteAllBytes(Path.Combine(outDir, "Proc3DNoise.r8"), decoded);
            File.WriteAllText(Path.Combine(outDir, "Proc3DNoise.dims.txt"), $"{width} {height} {depth}");
            Console.WriteLine($"[SystemTextures] cTex_Proc3DNoise <- {NoiseAssetPath}: {width}x{height}x{depth}, {decoded.Length} bytes -> {outDir}");
        }

        /// <summary>
        /// The sun and moon sprites. <c>Etc_Sun_A_Alb</c> is a 64x64 BC4 disc mask (the colour comes from the palette), and
        /// <c>Etc_Moon_A_Alb.1</c> to <c>.8</c> are 256x256 BC5, one per phase. Each is isolated: <c>.5</c> is authored in an
        /// ASTC format and is skipped, and the sky body pass falls back to the nearest phase it has.
        /// </summary>
        public static void ExtractSkyBodyTextures(string romfsRoot, string outDir)
        {
            Directory.CreateDirectory(outDir);
            Try(() => ExtractMask(romfsRoot, outDir, "Etc_Sun_A_Alb", "SunDisc"), "SunDisc");
            for (int phase = 1; phase <= 8; phase++)
            {
                int p = phase;
                Try(() => ExtractRg(romfsRoot, outDir, $"Etc_Moon_A_Alb.{p}", $"Moon{p}"), $"Moon{p}");
            }

            static void Try(Action work, string what)
            {
                try { work(); }
                catch (Exception ex) { Console.WriteLine($"[SystemTextures] {what} unavailable: {ex.Message}"); }
            }
        }

        static void ExtractMask(string romfsRoot, string outDir, string textureName, string outName)
        {
            var tex = LoadFirstSurface(romfsRoot, textureName, TxtgFormat.BC4_UNORM);
            if (tex is null)
                return;

            var decoded = new byte[tex.Width * tex.Height];
            RedChannel(TextureFormat.Bc4, tex.Surfaces[0].Data, tex.Width, tex.Height, decoded);

            File.WriteAllBytes(Path.Combine(outDir, outName + ".r8"), decoded);
            File.WriteAllText(Path.Combine(outDir, outName + ".dims.txt"), $"{tex.Width} {tex.Height} 1");
            Console.WriteLine($"[SystemTextures] {outName} <- {textureName}: {tex.Width}x{tex.Height}, {decoded.Length} bytes -> {outDir}");
        }

        static void ExtractRg(string romfsRoot, string outDir, string textureName, string outName)
        {
            var tex = LoadFirstSurface(romfsRoot, textureName, TxtgFormat.BC5_UNORM);
            if (tex is null)
                return;

            byte[] rgba = TextureDecoder.ToRgba8(TextureFormat.Bc5, tex.Surfaces[0].Data, tex.Width, tex.Height);
            var decoded = new byte[tex.Width * tex.Height * 2];
            for (int i = 0; i < tex.Width * tex.Height; i++)
            {
                decoded[i * 2] = rgba[i * 4];
                decoded[i * 2 + 1] = rgba[i * 4 + 1];
            }

            File.WriteAllBytes(Path.Combine(outDir, outName + ".rg8"), decoded);
            File.WriteAllText(Path.Combine(outDir, outName + ".dims.txt"), $"{tex.Width} {tex.Height} 2");
            Console.WriteLine($"[SystemTextures] {outName} <- {textureName}: {tex.Width}x{tex.Height} BC5, {decoded.Length} bytes -> {outDir}");
        }

        static TxtgTexture? LoadFirstSurface(string romfsRoot, string textureName, TxtgFormat expected)
        {
            string path = Path.Combine(romfsRoot, "TexToGo", textureName + ".txtg");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[SystemTextures] '{path}' not found.");
                return null;
            }

            var tex = TxtgTexture.Load(path, surfaces: 1);
            if (tex.Format != expected)
            {
                Console.WriteLine($"[SystemTextures] '{textureName}' is {tex.Format}, expected {expected} - skipping.");
                return null;
            }
            return tex;
        }

        static void RedChannel(TextureFormat format, ReadOnlySpan<byte> blocks, int width, int height, Span<byte> destination)
        {
            byte[] rgba = TextureDecoder.ToRgba8(format, blocks.ToArray(), width, height);
            for (int i = 0; i < width * height; i++)
                destination[i] = rgba[i * 4];
        }
    }
}
