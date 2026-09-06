using System;
using System.IO;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Loads real romfs "TexToGo/*.txtg" files and verifies every mip surface deswizzles to
    /// exactly the byte count its format/dimensions predict (widthInBlocks * heightInBlocks *
    /// bytesPerBlock) - the same kind of byte-exact check TestMaterialDump.DumpGeometry used to
    /// confirm vertex/index buffers survived MeshCodec's FMSH decode intact.
    /// </summary>
    public static class TestTxtg
    {
        public static void RunDemo(string romfsRoot, params string[] textureNames)
        {
            foreach (var texName in textureNames)
            {
                string texPath = Path.Combine(romfsRoot, "TexToGo", texName + ".txtg");
                if (!File.Exists(texPath))
                {
                    Console.WriteLine($"[TXTG] skip - not found: {texPath}");
                    continue;
                }

                var tex = TxtgTexture.Load(texPath);
                Console.WriteLine($"[TXTG] {texName}: {tex.Width}x{tex.Height} depth={tex.Depth} mips={tex.MipCount} format={tex.Format} surfaces={tex.Surfaces.Count}");

                var (bpp, blockW, blockH) = TxtgTexture.GetFormatInfo(tex.Format);
                foreach (var surf in tex.Surfaces)
                {
                    int mipW = Math.Max(1, tex.Width >> surf.MipLevel);
                    int mipH = Math.Max(1, tex.Height >> surf.MipLevel);
                    long expected = ((mipW + blockW - 1) / blockW) * ((mipH + blockH - 1) / blockH) * bpp;
                    string flag = surf.Data.Length == expected ? "OK" : $"MISMATCH (expected {expected})";
                    Console.WriteLine($"    mip{surf.MipLevel} array{surf.ArrayLevel}: {mipW}x{mipH} -> {surf.Data.Length} bytes {flag}");
                }
            }
        }

        /// <summary>Writes one already-deswizzled mip surface's raw block-compressed bytes straight to disk - for offline analysis of a texture's actual channel content when a shader bug is suspected in the format/channel interpretation rather than the deswizzle itself.</summary>
        public static void DumpSurface(string romfsRoot, string texName, int mip, string outPath)
        {
            string texPath = Path.Combine(romfsRoot, "TexToGo", texName + ".txtg");
            var tex = TxtgTexture.Load(texPath);
            var surf = tex.Surfaces.Find(s => s.MipLevel == mip && s.ArrayLevel == 0)
                ?? throw new InvalidOperationException($"no mip {mip} in '{texName}'");
            File.WriteAllBytes(outPath, surf.Data);
            Console.WriteLine($"[TXTG] {texName} mip{mip}: format={tex.Format} {Math.Max(1, tex.Width >> mip)}x{Math.Max(1, tex.Height >> mip)} -> {outPath} ({surf.Data.Length} bytes)");
        }
    }
}
