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
    }
}
