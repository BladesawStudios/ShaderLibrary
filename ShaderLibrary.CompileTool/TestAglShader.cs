using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ShaderLibrary;
using EffectLibraryTest;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Reads TotK's agl shader archives (`Shader/ApplicationPackage.Nin_NX_NVN.release.sarc.zs`
    /// -> AglShader.sharcb / AglLightShader.sharcb / GrassShader.sharcb, magic "BAHS").
    ///
    /// These are agl's own engine-side shaders rather than material shaders, which is where the
    /// post-process chain lives - in particular `hdr_compose`, the pass that turns the deferred
    /// renderer's linear HDR output into a displayable image. The deferred passes in
    /// system.bfsha stop at linear HDR, so without this the test bench has to invent its own
    /// tonemap.
    ///
    /// Unlike bfsha's gsys_context/gsys_environment, these archives DO carry symbol tables:
    /// each variation lists its uniform blocks (name, location, size) and its individual uniform
    /// names, so a recovered layout here needs no guessing.
    /// </summary>
    public static class TestAglShader
    {
        public static void Run(string sharcbPath, string outDir, params string[] programFilter)
        {
            Directory.CreateDirectory(outDir);
            Console.WriteLine("################################################################");
            Console.WriteLine($"# agl shader archive: {sharcbPath}");
            Console.WriteLine("################################################################");

            var sharc = new SharcfbFile(sharcbPath);
            Console.WriteLine($"Name=\"{sharc.Name}\"  Programs={sharc.Programs.Count}  Variations={sharc.Variations.Count}");

            var wanted = new HashSet<string>(programFilter ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            Console.WriteLine();
            Console.WriteLine("-- Programs --");
            foreach (var p in sharc.Programs)
            {
                string macros = p.VariationMacros.Count == 0
                    ? ""
                    : "  macros: " + string.Join(", ", p.VariationMacros.Select(m => $"{m.Name}[{string.Join("|", m.Values)}]"));
                Console.WriteLine($"   {p.Name,-42} base={p.BaseIndex,-5} kind={p.Kind} (vtx={p.HasVertexShader()} pix={p.HasPixelShader()}){macros}");
            }

            foreach (var p in sharc.Programs)
            {
                if (wanted.Count > 0 && !wanted.Contains(p.Name))
                    continue;
                DumpProgram(sharc, p, outDir);
            }
        }

        static void DumpProgram(SharcfbFile sharc, SharcfbFile.ShaderProgram p, string outDir)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {p.Name} ===");

            // Variation 0 (the default macro combination) is what the game runs unless a macro
            // is explicitly set. When the program HAS macros, also dump every other combination -
            // an "effect" program is often a no-op passthrough at STEP=0 and only does real work
            // at STEP>=1, which is exactly what hid back_face_fx_renderer's outline logic.
            var combos = p.VariationMacros.Count > 0
                ? ShaderLibrary.Sharc.SharcUtils.GetAllVariationCombinations(p.VariationMacros).ToList()
                : new List<Dictionary<string, string>> { new Dictionary<string, string>() };

            for (int ci = 0; ci < combos.Count; ci++)
            {
                var combo = combos[ci];
                int combIndex = combo.Count > 0
                    ? ShaderLibrary.Sharc.SharcUtils.GetVariationIndex(p.VariationMacros, combo)
                    : 0;
                string suffix = combo.Count > 0
                    ? "_" + string.Join("_", combo.Select(kv => $"{kv.Key}{kv.Value}"))
                    : "";

                int idx = p.GetBinaryIndex(combIndex);
                for (int stage = 0; stage < 2; stage++)
                {
                    int vi = idx + stage;
                    if (vi < 0 || vi >= sharc.Variations.Count)
                        continue;
                    var v = sharc.Variations[vi];
                    string kind = v.Type.ToString();

                    Console.WriteLine($"  -- variation {vi} ({kind}){suffix} : {v.ByteCode?.Length ?? 0} bytes --");

                    if (v.UniformBlocks.Count > 0)
                    {
                        Console.WriteLine("     uniform blocks:");
                        foreach (var b in v.UniformBlocks)
                            Console.WriteLine($"       location {b.Location,3}  size {b.Size,5}  {b.Name}");
                    }
                    if (v.Uniforms.Count > 0)
                    {
                        Console.WriteLine("     uniforms:");
                        foreach (var u in v.Uniforms)
                            Console.WriteLine($"       {u.Name}");
                    }
                    if (v.Samplers.Count > 0)
                        Console.WriteLine("     samplers: " + string.Join(", ", v.Samplers.Select(s => s.Name)));

                    if (v.ByteCode == null || v.ByteCode.Length == 0)
                        continue;

                    try
                    {
                        string glsl = TegraShaderTranslator.Decompile(v.ByteCode);

                        // Fold the immediate constant buffer in, exactly as for the bfsha shaders:
                        // agl shaders bake literals into the ISA too, and leaving fp_c1 dangling
                        // produces GLSL that references a block that is not bound.
                        if (v.ControlShader != null && v.ControlShader.Length > 0)
                        {
                            try
                            {
                                var control = new ControlShader(v.ControlShader);
                                float[] consts = control.GetConstantsAsFloats(v.ByteCode);
                                if (consts.Length > 0)
                                {
                                    glsl = ApplyConstants(glsl, consts);
                                    Console.WriteLine($"     folded {consts.Length} immediate constants");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"     [constants] {ex.Message}");
                            }
                        }

                        string ext = kind.ToLowerInvariant().Contains("pixel") || kind.ToLowerInvariant().Contains("frag") ? "frag" : "vert";
                        string path = Path.Combine(outDir, $"agl_{p.Name}{suffix}.{ext}");
                        File.WriteAllText(path, glsl);
                        Console.WriteLine($"     -> {path} ({glsl.Length} chars)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"     [decompile FAILED] {ex.Message}");
                    }
                }
            }
        }


        /// <summary>
        /// Same substitution ShaderExtract does for bfsha shaders: replace fp_c1/vp_c1 immediate
        /// constant-buffer reads with the literal floats from the shader's control section.
        /// </summary>
        static string ApplyConstants(string code, float[] constants)
        {
            var map = new Dictionary<string, float>();
            int slot = 0;
            for (int i = 0; i < constants.Length;)
            {
                string sw = "x";
                for (int j = 0; j < 4 && i < constants.Length; j++)
                {
                    map[$"vp_c1.data[{slot}].{sw}"] = constants[i];
                    map[$"fp_c1.data[{slot}].{sw}"] = constants[i];
                    map[$"vp_c1_1._m0[{slot}].{sw}"] = constants[i];
                    sw = sw == "x" ? "y" : sw == "y" ? "z" : sw == "z" ? "w" : "x";
                    i++;
                }
                slot++;
            }

            var sb = new StringBuilder();
            foreach (var line in code.Split('\n'))
            {
                string l = line;
                if (l.Contains("_c1.data[") || l.Contains("_c1_1._m0["))
                    foreach (var kv in map)
                        if (l.Contains(kv.Key))
                            l = l.Replace(kv.Key, kv.Value.ToString("R"));
                sb.Append(l).Append('\n');
            }
            return sb.ToString();
        }
    }
}
