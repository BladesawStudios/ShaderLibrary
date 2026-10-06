using System;
using System.IO;
using System.Linq;
using EffectLibraryTest;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// The game's terrain shaders, from <c>Shader/terrain</c>: the G-buffer programs of its
    /// <c>terrain</c> shading model and that model's default <c>gsys_material</c> block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terrain is drawn as heightmap patches: a unit grid (<c>_p0</c>) displaced in the vertex
    /// stage from per-patch textures (<c>tera_height</c>, <c>tera_normal_relaxed</c>,
    /// <c>tera_water</c>), placed by a per-patch block (<c>TerrainNode</c>, binding 12), with a
    /// per-scene block (<c>TerrainSystem</c>, binding 11) carrying the height scale and the 256-entry
    /// material table (from slot 12: uv scale, uv scale, array layer, detail strength). The fragment
    /// stage reads <c>tera_material</c> (two material indices, a flag, and - filtered - the blend),
    /// <c>tera_normal</c> and <c>tera_bake</c>, and samples the 121-layer <c>MaterialAlb</c>/
    /// <c>MaterialCmb</c> arrays through the table.
    /// </para>
    /// <para>
    /// The G-buffer programs: 2 (regular), 32 and 62 (the shader's own coarser levels - 62 drops the
    /// soft edge that reads the G-buffer under it). The hiatus variants that stitch patches of
    /// different sizes are not needed by a host with its own stitching.
    /// </para>
    /// </remarks>
    public static class ExportTerrainShaders
    {
        public static readonly int[] Programs = [2, 32, 62];

        public const string MaterialFile = "terrain_gsys_material.bin";

        public static string ProgramName(int program) => $"terrain_prog{program}";

        public static bool IsExported(string shadersDir) =>
            File.Exists(Path.Combine(shadersDir, MaterialFile))
            && Programs.All(p => File.Exists(Path.Combine(shadersDir, ProgramName(p) + "_extracted.frag")));

        public static void Run(string romfsRoot, string shadersDir)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;
            Directory.CreateDirectory(shadersDir);
            string path = Directory.GetFiles(Path.Combine(romfsRoot, "Shader"), "terrain.*bfsha*").First();
            byte[] raw = File.ReadAllBytes(path);
            byte[] plain = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var bfsha = new BfshaFile(new MemoryStream(plain));
            var sm = bfsha.ShaderModels["terrain"];

            foreach (int p in Programs)
            {
                var bin = sm.GetVariation(p).BinaryProgram;
                foreach (var (code, reflect, ext) in new[] { (bin.VertexShader, bin.VertexShaderReflection, "vert"), (bin.FragmentShader, bin.FragmentShaderReflection, "frag") })
                {
                    if (code?.ByteCode == null)
                        continue;
                    string target = Path.Combine(shadersDir, $"{ProgramName(p)}_extracted.{ext}");
                    File.WriteAllText(target + ".tmp", ShaderExtract.GetCode(code, reflect));
                    File.Move(target + ".tmp", target, overwrite: true);
                }
            }

            var block = sm.UniformBlocks["gsys_material"];
            byte[] defaults = block.DefaultBuffer ?? new byte[block.Size];
            File.WriteAllBytes(Path.Combine(shadersDir, MaterialFile), defaults);
            Console.WriteLine($"[ExportTerrainShaders] terrain programs {string.Join(", ", Programs)} and gsys_material defaults ({defaults.Length} bytes)");
        }
    }
}
