using System;
using System.IO;
using System.Linq;
using EffectLibraryTest;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// <c>--probe-bfsha &lt;archive&gt; [outDir] [program...]</c>: what any shader archive in
    /// <c>Shader/</c> holds - its shading models, their options and choices, samplers, uniform
    /// blocks (with members) and vertex attributes - and, given program indices, their decompiled
    /// vertex and fragment shaders. For reading an archive nothing else in preparation touches yet
    /// (<c>terrain</c>, <c>terrain_water</c>, <c>grass</c>, <c>tree</c>, <c>cave*</c>).
    /// </summary>
    public static class ProbeBfsha
    {
        public static void Run(string romfsRoot, string archive, string? outDir, int[] programs)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;
            string path = Directory.GetFiles(Path.Combine(romfsRoot, "Shader"), archive + ".*bfsha*").First();
            byte[] raw = File.ReadAllBytes(path);
            byte[] plain = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var bfsha = new BfshaFile(new MemoryStream(plain));

            foreach (var (name, sm) in bfsha.ShaderModels)
            {
                Console.WriteLine($"=== shading model '{name}': {sm.Programs.Count} programs");
                Console.WriteLine("  static options:");
                foreach (var (oname, o) in sm.StaticOptions)
                    Console.WriteLine($"    {oname} = {o.DefaultChoice}  [{string.Join(", ", o.Choices.Keys)}]");
                Console.WriteLine("  dynamic options:");
                foreach (var (oname, o) in sm.DynamicOptions)
                    Console.WriteLine($"    {oname} = {o.DefaultChoice}  [{string.Join(", ", o.Choices.Keys)}]");
                Console.WriteLine("  samplers:");
                foreach (var (sname, s) in sm.Samplers)
                    Console.WriteLine($"    [{s.Index}] {sname} ({s.Annotation})");
                Console.WriteLine("  uniform blocks:");
                foreach (var (bname, b) in sm.UniformBlocks)
                {
                    Console.WriteLine($"    [{b.Index}] {bname} size={b.Size} type={b.Type}");
                    foreach (var (uname, u) in b.Uniforms)
                        Console.WriteLine($"        @{u.DataOffset - 1} {uname}");
                }
                Console.WriteLine("  attributes:");
                foreach (var (aname, a) in sm.Attributes)
                    Console.WriteLine($"    [{a.Index}] {aname} location={a.Location}");

                if (outDir is null || programs.Length == 0)
                    continue;
                Directory.CreateDirectory(outDir);
                foreach (int p in programs.Where(p => p >= 0 && p < sm.Programs.Count))
                {
                    sm.DumpProgramChoices(p, Path.Combine(outDir, $"{archive}_{name}_prog{p}_options.txt"));
                    using (var w = new StreamWriter(Path.Combine(outDir, $"{archive}_{name}_prog{p}_bindings.txt")))
                    {
                        var prog = sm.Programs[p];
                        for (int i = 0; i < sm.Samplers.Count && i < prog.SamplerIndices.Count; i++)
                        {
                            var loc = prog.SamplerIndices[i];
                            if (loc.VertexLocation >= 0 || loc.FragmentLocation >= 0)
                                w.WriteLine($"sampler {sm.Samplers.GetKey(i)} vertex={loc.VertexLocation} fragment={loc.FragmentLocation}");
                        }
                        for (int i = 0; i < sm.UniformBlocks.Count && i < prog.UniformBlockIndices.Count; i++)
                        {
                            var loc = prog.UniformBlockIndices[i];
                            if (loc.VertexLocation >= 0 || loc.FragmentLocation >= 0)
                                w.WriteLine($"block {sm.UniformBlocks.GetKey(i)} vertex={loc.VertexLocation} fragment={loc.FragmentLocation}");
                        }
                    }
                    var bin = sm.GetVariation(p).BinaryProgram;
                    foreach (var (code, reflect, ext) in new[] { (bin.VertexShader, bin.VertexShaderReflection, "vert"), (bin.FragmentShader, bin.FragmentShaderReflection, "frag") })
                    {
                        if (code?.ByteCode == null)
                            continue;
                        try
                        {
                            File.WriteAllText(Path.Combine(outDir, $"{archive}_{name}_prog{p}_extracted.{ext}"), ShaderExtract.GetCode(code, reflect));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [decompile {name} prog {p}.{ext} failed] {ex.Message}");
                        }
                    }
                    Console.WriteLine($"  decompiled program {p}");
                }
            }
        }
    }
}
