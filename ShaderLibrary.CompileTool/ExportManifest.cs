using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BfresLibrary;
using EffectLibraryTest;
using ShaderLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Emits a per-shape render manifest for a model, so the Python bench can draw ANY TotK model
    /// without hardcoding shader names, vertex layouts or texture bindings.
    ///
    /// Everything here is derived, not assumed:
    ///   * program indices come from the material's own option search (exact since the
    ///     ExternalBinaryString off-by-one fix - no fallback heuristics fire any more);
    ///   * the vertex format string is built from the VERTEX SHADER'S declared input locations
    ///     against ExportTestBench's fixed 192-byte interleaved layout, so a shape only binds the
    ///     attributes its compiled program actually reads;
    ///   * sampler units come from the shader's reflection (location -> unit), joined to the
    ///     material's SamplerAssign (shader key -> material sampler) and TextureRefs.
    ///
    /// The output is JSON written by hand rather than via a serializer to keep this file free of
    /// extra package references.
    /// </summary>
    public static class ExportManifest
    {
        /// <summary>
        /// ExportTestBench writes a fixed 192-byte interleaved vertex. Attribute LOCATION -> its
        /// (byte offset, component count) in that layout. A program declares a subset of these,
        /// and the moderngl format string is built by walking this table in offset order and
        /// padding over everything the program does not use.
        /// </summary>
        static readonly (int loc, int offset, int comps, string name)[] VertexLayout =
        {
            (0,  0,   4, "aPosition"),
            (1,  16,  4, "aNormal"),
            (2,  32,  4, "aTangent"),
            (3,  48,  4, "aU254"),
            (4,  64,  4, "aBlendWeight0"),
            (5,  80,  4, "aBlendWeight1"),
            (6,  96,  4, "aBlendIndex0"),
            (7,  112, 4, "aBlendIndex1"),
            (8,  128, 2, "aTexCoord0"),
            (9,  136, 2, "aTexCoord1"),
            (10, 144, 2, "aTexCoord2"),
            (11, 152, 2, "aTexCoord3"),
            (12, 160, 4, "aColor0"),
            (13, 176, 4, "aColor1"),
        };
        const int VertexStride = 192;

        /// <summary>
        /// o_material_behave -> the DeferredMain pass whose material carries that same value.
        /// Recovered by resolving every DeferredMain shape's options against system.bfsha.
        ///
        /// Value 0 is the shading model's DEFAULT, and chara_nonmetal is the one resolve pass
        /// that never sets the option - so an ordinary character surface, which is most of any
        /// character model, lands here. Value 1 is genuinely ambiguous (chara_metal, clear and
        /// debug_simple all use it) and value 2 spans the whole field family, so the manifest
        /// reports the raw value alongside the suggestion.
        /// </summary>
        static readonly Dictionary<string, string> BehaveToPass = new()
        {
            ["0"] = "chara_nonmetal", ["1"] = "chara_metal", ["10"] = "chara_grossy",
            ["100"] = "chara_hair", ["101"] = "chara_skin", ["104"] = "chara_eye",
            ["105"] = "field_leaf", ["103"] = "field_water", ["2"] = "field_hybrid",
        };

        public static void Run(string romfsRoot, string bfshaPath, string modelName, string outDir,
                               string shaderOutDir)
        {
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(shaderOutDir);
            // Decompile each program ONCE, keyed by index - many materials share a program, and
            // naming the file by index (not by shape) is what lets the manifest reference it.
            var decompiled = new HashSet<int>();
            var bfsha = new BfshaFile(bfshaPath);
            string mcPath = RomfsPaths.ModelFile(romfsRoot, modelName)
                            ?? throw new FileNotFoundException(RomfsPaths.Explain(romfsRoot, modelName));
            byte[] fres = TestMaterialDump.DecompressBfresMc(mcPath);
            using var ms = new MemoryStream(fres);
            var resFile = new ResFile(ms, false);
            var model = resFile.Models[0];

            Console.WriteLine("################################################################");
            Console.WriteLine($"# Manifest for {modelName}: {model.Shapes.Count} shape(s)");
            Console.WriteLine("################################################################");

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"model\": \"{modelName}\",");
            sb.AppendLine($"  \"vertex_stride\": {VertexStride},");
            sb.AppendLine($"  \"vertex_layout\": [{string.Join(", ", VertexLayout.Select(v =>
                $"{{ \"name\": \"{v.name}\", \"location\": {v.loc}, \"offset\": {v.offset}, " +
                $"\"components\": {v.comps} }}"))}],");
            sb.AppendLine("  \"shapes\": [");

            var shapes = model.Shapes.Values.ToList();
            for (int si = 0; si < shapes.Count; si++)
            {
                Shape shape = shapes[si];
                Material mat = model.Materials[shape.MaterialIndex];
                string safe = shape.Name.Replace(":", "_").Replace("/", "_");
                var shading = mat.ShaderAssign.ShadingModelName;

                var programs = new Dictionary<string, int>();
                foreach (string pipeline in new[] { "gsys_assign_gbuffer", "gsys_assign_zonly", "gsys_assign_material" })
                {
                    try
                    {
                        var r = TestTOTK.GetShaderProgram(bfsha, resFile, shape.Name, pipeline);
                        programs[pipeline.Replace("gsys_assign_", "")] = r.Item1;
                    }
                    catch { programs[pipeline.Replace("gsys_assign_", "")] = -1; }
                }

                var attrs = new List<(int loc, string name)>();
                string vfmt = "";
                var samplers = new List<string>();
                var zonlySamplers = new List<string>();
                var matSamplers = new List<string>();
                int gb = programs.GetValueOrDefault("gbuffer", -1);
                if (gb >= 0 && bfsha.ShaderModels.ContainsKey(shading))
                {
                    var binProg = bfsha.ShaderModels[shading].GetVariation(gb).BinaryProgram;
                    attrs = ProgramInputs(binProg);
                    vfmt = BuildFormat(attrs.Select(a => a.loc).ToHashSet());
                    samplers = BuildSamplers(bfsha.ShaderModels[shading], gb, mat, romfsRoot);

                    if (decompiled.Add(gb))
                        DecompileProgram(binProg, shaderOutDir, $"{shading}_prog{gb}");

                    // Also decompile the Z-ONLY variant. Some materials carry no inline alpha
                    // test in their G-Buffer program and rely on the depth prepass to do the
                    // cutting, so the bench needs that program to reproduce the cutout.
                    int zo = programs.GetValueOrDefault("zonly", -1);
                    if (zo >= 0)
                        zonlySamplers = BuildSamplers(bfsha.ShaderModels[shading], zo, mat, romfsRoot);
                    if (zo >= 0 && decompiled.Add(zo))
                        DecompileProgram(bfsha.ShaderModels[shading].GetVariation(zo).BinaryProgram,
                                         shaderOutDir, $"{shading}_prog{zo}");

                    // The FORWARD program. Blended materials cannot go through a deferred
                    // G-Buffer at all - the engine draws them with gsys_assign_material after the
                    // resolve. Enemy_Chuchu_Junior is the first model here that needs it: both
                    // its materials are render state "custom" with src_alpha/one_minus_src_alpha.
                    int mp = programs.GetValueOrDefault("material", -1);
                    if (mp >= 0)
                        matSamplers = BuildSamplers(bfsha.ShaderModels[shading], mp, mat, romfsRoot);
                    if (mp >= 0 && decompiled.Add(mp))
                        DecompileProgram(bfsha.ShaderModels[shading].GetVariation(mp).BinaryProgram,
                                         shaderOutDir, $"{shading}_prog{mp}");
                }

                string behave = ResolveOption(bfsha, shading, mat, "o_material_behave");
                string pass = BehaveToPass.GetValueOrDefault(behave, "");

                int idxCount = (int)shape.Meshes[0].IndexCount;
                Console.WriteLine();
                Console.WriteLine($"-- {shape.Name}");
                Console.WriteLine($"   material {mat.Name}  programs gbuffer={programs["gbuffer"]} " +
                                  $"zonly={programs["zonly"]} material={programs["material"]}");
                Console.WriteLine($"   attrs [{string.Join(", ", attrs.Select(a => a.name))}]");
                Console.WriteLine($"   format \"{vfmt}\"");
                Console.WriteLine($"   o_material_behave={behave} -> deferred pass {(pass == "" ? "(unmapped)" : pass)}");
                Console.WriteLine($"   {samplers.Count} sampler binding(s)");

                sb.AppendLine("    {");
                sb.AppendLine($"      \"name\": \"{shape.Name}\",");
                sb.AppendLine($"      \"material\": \"{mat.Name}\",");
                sb.AppendLine($"      \"shading_model\": \"{shading}\",");
                sb.AppendLine($"      \"vertex_file\": \"{safe}_vtx.bin\",");
                sb.AppendLine($"      \"index_file\": \"{safe}_idx.bin\",");
                sb.AppendLine($"      \"index_count\": {idxCount},");
                // Which space this shape's positions were authored in: 0 = rigid (bone space,
                // Shape.BoneIndex), 1 = single-bind (bone space, per-vertex _i0), >=2 = smooth
                // (already model space). The exporter bakes the bone matrix for 0 and 1.
                sb.AppendLine($"      \"vertex_skin_count\": {shape.VertexSkinCount},");
                sb.AppendLine($"      \"bone_index\": {shape.BoneIndex},");
                sb.AppendLine($"      \"vertex_format\": \"{vfmt}\",");
                sb.AppendLine($"      \"attributes\": [{string.Join(", ", attrs.Select(a => $"\"{a.name}\""))}],");
                // Per-attribute layout, so the bench can REBUILD the format string against
                // whatever the linked GLSL actually exposes. The BNSH reflection lists inputs the
                // decompiled shader may not declare (an input the code never reads is dropped at
                // link time), and binding a name the program does not have is a hard error.
                sb.AppendLine($"      \"attribute_layout\": [{string.Join(", ", attrs.Select(a =>
                {
                    var v = VertexLayout.FirstOrDefault(x => x.loc == a.loc);
                    return $"{{ \"name\": \"{a.name}\", \"location\": {a.loc}, " +
                           $"\"offset\": {v.offset}, \"components\": {v.comps} }}";
                }))}],");
                sb.AppendLine($"      \"programs\": {{ \"gbuffer\": {programs["gbuffer"]}, \"zonly\": {programs["zonly"]}, \"material\": {programs["material"]} }},");
                sb.AppendLine($"      \"gbuffer_shader\": \"{shading}_prog{programs["gbuffer"]}_extracted\",");
                sb.AppendLine($"      \"zonly_shader\": \"{shading}_prog{programs["zonly"]}_extracted\",");
                // Alpha cutout lives in RenderInfo, and for many materials the cut is performed
                // by the DEPTH PREPASS rather than by the G-Buffer program - so the bench has to
                // know to run it.
                sb.AppendLine($"      \"alpha_test\": {(mat.GetRenderInfoString("gsys_alpha_test_enable") == "true" ? "true" : "false")},");
                sb.AppendLine($"      \"material_ubo\": \"matubo/{mat.Name}.gsys_material.bin\",");
                sb.AppendLine($"      \"o_material_behave\": \"{behave}\",");
                sb.AppendLine($"      \"deferred_pass\": \"{pass}\",");
                sb.AppendLine($"      \"samplers\": [{string.Join(", ", samplers)}],");
                // The z-only program has its OWN sampler layout - the same texture sits on a
                // different unit there. Binding the G-Buffer's units during the depth prepass
                // makes its alpha-test discard read the wrong texture, so the cutout silently
                // does nothing.
                sb.AppendLine($"      \"zonly_samplers\": [{string.Join(", ", zonlySamplers)}],");
                sb.AppendLine($"      \"material_shader\": \"{shading}_prog{programs["material"]}_extracted\",");
                sb.AppendLine($"      \"material_samplers\": [{string.Join(", ", matSamplers)}],");
                sb.AppendLine($"      \"render_state\": {RenderState(mat)}");
                sb.AppendLine(si == shapes.Count - 1 ? "    }" : "    },");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            string path = Path.Combine(outDir, $"{modelName}.manifest.json");
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine();
            Console.WriteLine($"-> {path}");
        }

        /// <summary>
        /// A material's ShaderOptions entry can be the literal sentinel "&lt;Default Value&gt;",
        /// which means "whatever the shading model declares as this option's default" - it is NOT
        /// an absent option. Reading the material alone therefore loses the value for most shapes
        /// (18 of the dragon's 29), which is what left their deferred pass unmapped. The default
        /// lives on the ShaderOption itself as DefaultChoice.
        /// </summary>
        static string ResolveOption(BfshaFile bfsha, string shading, Material mat, string key)
        {
            string v = mat.ShaderAssign.ShaderOptions.ContainsKey(key)
                ? mat.ShaderAssign.ShaderOptions[key].ToString() : "";
            if (v != "" && v != "<Default Value>")
                return v;

            if (!bfsha.ShaderModels.ContainsKey(shading))
                return "";
            var sm = bfsha.ShaderModels[shading];
            ShaderOption opt = sm.StaticOptions.ContainsKey(key) ? sm.StaticOptions[key]
                             : sm.DynamicOptions.ContainsKey(key) ? sm.DynamicOptions[key]
                             : null;
            return opt?.DefaultChoice ?? "";
        }

        /// <summary>
        /// The material's own render state, as raw engine strings for the bench to map to GL.
        /// "custom" means these fields are authoritative rather than a preset; that is how a
        /// translucent material declares its blending, and it is the difference between drawing
        /// an object in the deferred G-Buffer and drawing it forward with blending.
        /// </summary>
        static string RenderState(Material mat)
        {
            string S(string k) => mat.GetRenderInfoString(k) ?? "";
            string mode = S("gsys_render_state_mode");
            bool blend = mode == "translucent" ||
                         (mode == "custom" && S("gsys_render_state_blend_mode") == "color");
            return "{ " +
                   $"\"mode\": \"{mode}\", " +
                   $"\"blend\": {(blend ? "true" : "false")}, " +
                   $"\"blend_mode\": \"{S("gsys_render_state_blend_mode")}\", " +
                   $"\"rgb_op\": \"{S("gsys_color_blend_rgb_op")}\", " +
                   $"\"rgb_src\": \"{S("gsys_color_blend_rgb_src_func")}\", " +
                   $"\"rgb_dst\": \"{S("gsys_color_blend_rgb_dst_func")}\", " +
                   $"\"alpha_op\": \"{S("gsys_color_blend_alpha_op")}\", " +
                   $"\"alpha_src\": \"{S("gsys_color_blend_alpha_src_func")}\", " +
                   $"\"alpha_dst\": \"{S("gsys_color_blend_alpha_dst_func")}\", " +
                   $"\"depth_write\": \"{S("gsys_depth_test_write")}\", " +
                   $"\"depth_func\": \"{S("gsys_depth_test_func")}\", " +
                   $"\"display_face\": \"{S("gsys_render_state_display_face")}\"" +
                   " }";
        }

        static void DecompileProgram(BnshFile.BnshShaderProgram binProg, string dir, string label)
        {
            foreach (var (code, reflect, ext) in new[]
                     {
                         (binProg.VertexShader, binProg.VertexShaderReflection, "vert"),
                         (binProg.FragmentShader, binProg.FragmentShaderReflection, "frag"),
                     })
            {
                if (code?.ByteCode == null)
                    continue;
                try
                {
                    File.WriteAllText(Path.Combine(dir, $"{label}_extracted.{ext}"),
                                      ShaderExtract.GetCode(code, reflect));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   [decompile {label}.{ext} FAILED] {ex.Message}");
                }
            }
            Console.WriteLine($"   decompiled -> {label}_extracted.vert/.frag");
        }

        static List<(int loc, string name)> ProgramInputs(BnshFile.BnshShaderProgram binProg)
        {
            var list = new List<(int, string)>();
            var reflect = binProg.VertexShaderReflection;
            if (reflect == null)
                return list;
            foreach (var kv in reflect.Inputs)
            {
                int loc = reflect.GetInputLocation(kv.Key);
                if (loc >= 0)
                    list.Add((loc, kv.Key));
            }
            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return list;
        }

        /// <summary>
        /// Walk the fixed layout in offset order emitting "Nf" for an attribute the program uses
        /// and "Nx" padding for the bytes it does not, so the total always sums to the stride.
        /// </summary>
        static string BuildFormat(HashSet<int> used)
        {
            var parts = new List<string>();
            int cursor = 0;
            foreach (var (loc, offset, comps, _) in VertexLayout.OrderBy(v => v.offset))
            {
                if (!used.Contains(loc))
                    continue;
                if (offset > cursor)
                    parts.Add($"{offset - cursor}x");
                parts.Add($"{comps}f");
                cursor = offset + comps * 4;
            }
            if (cursor < VertexStride)
                parts.Add($"{VertexStride - cursor}x");
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Shader sampler location -> texture.
        ///
        /// The sampler NAMES the material's SamplerAssign uses ("_a0", "_n0", ...) are the SHADING
        /// MODEL's names, not the compiled binary's reflection names (which are "cTexture0",
        /// "cTexture1", ... and will never match). So walk ShaderModel.Samplers in order and read
        /// the per-program SamplerIndices to get each one's fragment location, then join through
        /// SamplerAssign -> the material's own sampler list -> the parallel TextureRefs entry.
        /// </summary>
        static List<string> BuildSamplers(ShaderModel sm, int progIdx, Material mat, string romfsRoot)
        {
            var outList = new List<string>();
            var prog = sm.Programs[progIdx];
            var matSamplerOrder = mat.Samplers.Keys.ToList();

            for (int i = 0; i < sm.Samplers.Count; i++)
            {
                string shaderKey = sm.Samplers.Keys.ElementAt(i);
                var loc = prog.SamplerIndices[i];
                if (loc.FragmentLocation < 0)
                    continue;

                if (!mat.ShaderAssign.SamplerAssigns.ContainsKey(shaderKey))
                    continue;
                string assigned = mat.ShaderAssign.SamplerAssigns[shaderKey].ToString();
                if (string.IsNullOrEmpty(assigned) || assigned == "<Default Value>")
                    continue;

                int idx = matSamplerOrder.IndexOf(assigned);
                if (idx < 0 || idx >= mat.TextureRefs.Count)
                    continue;

                string texName = mat.TextureRefs[idx].Name;
                string file = "", fmt = "";
                int w = 0, h = 0;
                string txtg = Path.Combine(romfsRoot, "TexToGo", texName + ".txtg");
                if (File.Exists(txtg))
                {
                    try
                    {
                        var t = TxtgTexture.Load(txtg);
                        w = (int)t.Width; h = (int)t.Height; fmt = t.Format.ToString();
                        file = $"{texName}_{w}x{h}_{fmt}.bin";
                    }
                    catch { }
                }

                outList.Add($"{{ \"unit\": {loc.FragmentLocation}, \"key\": \"{shaderKey}\", " +
                            $"\"assigned\": \"{assigned}\", \"texture\": \"{texName}\", " +
                            $"\"file\": \"{file}\", \"format\": \"{fmt}\", " +
                            $"\"width\": {w}, \"height\": {h} }}");
            }
            return outList;
        }
    }
}
