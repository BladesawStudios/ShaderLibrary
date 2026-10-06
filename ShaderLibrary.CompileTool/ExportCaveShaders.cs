using System;
using System.IO;
using System.Linq;
using EffectLibraryTest;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// The game's cave shaders, from <c>Shader/cave</c>: the programs of its
    /// <c>cave_chunked_mesh_generic</c> shading model that draw a crbin mesh - the caves, the sky
    /// islands, the edit parts, the wells and the Depths' caves - and that model's default
    /// <c>gsys_material</c> block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the terrain, nothing here needs a host vertex stage: the game's own vertex stage reads
    /// the page file's vertices as they are stored (28 bytes: a patch word, a parent block and a
    /// self block, as two <c>vec4</c>s of raw bits), dequantises them against a per-node block
    /// (<c>cave_ChunkDynamicDataUBO</c>), picks each vertex's three materials out of the crbin's
    /// table (<c>cave_MaterialPaletteUBO</c>) and places the result with a per-instance matrix
    /// (<c>cave_CaveInstanceDynamicDataUBO</c>). The host's job is to feed it those.
    /// </para>
    /// <para>
    /// Program 2 is the G-buffer program: the same outputs (1, 3 and 7) and the same soft edge, reading
    /// the G-buffer under it, as the terrain's. Program 1 (4 is identical) is depth only, for the
    /// prepass and the shadow cascades. Programs 0 and 3 are other assigns - the lit forward-style
    /// program and a visualisation - which a deferred frame has no use for.
    /// </para>
    /// </remarks>
    public static class ExportCaveShaders
    {
        public const string ModelName = "cave_chunked_mesh_generic";

        public static readonly int[] Programs = [1, 2];

        public const string MaterialFile = "cave_gsys_material.bin";

        public static string ProgramName(int program) => $"cave_prog{program}";

        public static bool IsExported(string shadersDir) =>
            File.Exists(Path.Combine(shadersDir, MaterialFile))
            && Programs.All(p => File.Exists(Path.Combine(shadersDir, ProgramName(p) + "_extracted.frag"))
                              && File.Exists(Path.Combine(shadersDir, ProgramName(p) + "_extracted.vert")));

        public static void Run(string romfsRoot, string shadersDir)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;
            Directory.CreateDirectory(shadersDir);
            string path = Directory.GetFiles(Path.Combine(romfsRoot, "Shader"), "cave.*bfsha*")
                .First(f => !Path.GetFileName(f).StartsWith("cave_", StringComparison.Ordinal));
            byte[] raw = File.ReadAllBytes(path);
            byte[] plain = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var bfsha = new BfshaFile(new MemoryStream(plain));
            var sm = bfsha.ShaderModels[ModelName];

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
            Console.WriteLine($"[ExportCaveShaders] cave programs {string.Join(", ", Programs)} and gsys_material defaults ({defaults.Length} bytes)");
        }
    }
}
