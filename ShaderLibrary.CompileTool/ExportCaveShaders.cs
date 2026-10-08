using System;
using System.IO;
using System.Linq;
using EffectLibraryTest;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// The game's cave shaders from <c>Shader/cave</c> (<c>cave_chunked_mesh_generic</c>), which draw a crbin mesh, and that model's default
    /// <c>gsys_material</c>. The game's own vertex stage reads the page file as stored, so no host vertex stage is needed. Program 2 is the G-buffer
    /// program and program 1 (4 is identical) the depth-only one; 0 and 3 are a forward-style program and a visualisation.
    /// </summary>
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
