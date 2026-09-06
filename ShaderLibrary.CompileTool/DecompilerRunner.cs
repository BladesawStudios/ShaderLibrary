using System;
using System.IO;
using BfresLibrary;
using EffectLibraryTest;
using ShaderLibrary;
using ShaderLibrary.CompileTool;

namespace ShaderLibrary.CompilerTool
{
    public static class DecompilerRunner
    {
        public static void Run(string romfsRoot, string bfshaPath, string outDir)
        {
            Directory.CreateDirectory(outDir);
            Console.WriteLine($"[DecompilerRunner] Loading BFSHA: {bfshaPath}...");
            var bfsha = new BfshaFile(bfshaPath);
            var sm = bfsha.ShaderModels["material"];
            if (sm.StaticOptions.ContainsKey("gsys_assign_type"))
                Console.WriteLine($"[gsys_assign_type choices]: {string.Join(", ", sm.StaticOptions["gsys_assign_type"].Choices.Keys)}");

            string mcPath = Path.Combine(romfsRoot, "Model", "Weapon_Sword_070.Weapon_Sword_070.bfres.mc");
            byte[] fres = TestMaterialDump.DecompressBfresMc(mcPath);
            var resFile = new ResFile(new MemoryStream(fres), false);
            foreach (var s in resFile.Models[0].Shapes.Values)
            {
                var vb = resFile.Models[0].VertexBuffers[s.VertexBufferIndex];
                Console.WriteLine($"[Shape VB] {s.Name}: {string.Join(", ", vb.Attributes.Keys)}");
            }

            // No hardcoded program indices: these used to be pinned to 17114/17112/2210/2208,
            // which were what the option search produced back when V10 option/param NAMES came
            // back wrong (ExternalBinaryStringTable's key/entry pairing was off by one, so every
            // name resolved to its predecessor's). With that fixed, the search finds an exact
            // key-table match with no NormalizeOptionsForShader or ResolveViaClosestProgram
            // fallback at all, and lands on a different, consistent material/zonly/gbuffer triple
            // per material - 10704/10705/10706 for the blade and 2720/2721/2722 for the hilt.
            // Overriding would now mean decompiling a variant the material does not select.
            var targets = new (string shapeName, string pipeline, string label, int? progOverride)[]
            {
                ("Weapon_Sword_070_Blade__Mt_Sword_070_Blade", "gsys_assign_gbuffer", "Blade_GBuffer", null),
                ("Weapon_Sword_070_Blade__Mt_Sword_070_Blade", "gsys_assign_material", "Blade_ForwardMaterial", null),
                ("Weapon_Sword_070__Mt_Sword_070", "gsys_assign_gbuffer", "Hilt_GBuffer", null),
                ("Weapon_Sword_070__Mt_Sword_070", "gsys_assign_material", "Hilt_ForwardMaterial", null),
            };

            foreach (var t in targets)
            {
                Console.WriteLine();
                Console.WriteLine($"=== Decompiling {t.label} ({t.shapeName} / {t.pipeline}) [Prog {t.progOverride}] ===");
                try
                {
                    int progIdx = t.progOverride ?? TestTOTK.GetShaderProgram(bfsha, resFile, t.shapeName, t.pipeline).Item1;
                    var binProg = bfsha.ShaderModels["material"].GetVariation(progIdx).BinaryProgram;
                    Console.WriteLine($"Program Index: {progIdx}");
                    PrintProgramOptions(bfsha.ShaderModels["material"], progIdx);

                    var shape = resFile.Models[0].Shapes[t.shapeName];
                    var mat = resFile.Models[0].Materials[shape.MaterialIndex];
                    Console.WriteLine($"  [Material SamplerAssigns]:");
                    foreach (var sa in mat.ShaderAssign.SamplerAssigns)
                    {
                        Console.WriteLine($"    SamplerAssign: key='{sa.Key}' -> val='{sa.Value}'");
                    }
                    Console.WriteLine($"  [Material TextureRefs]:");
                    for (int ti = 0; ti < mat.TextureRefs.Count; ti++)
                    {
                        Console.WriteLine($"    TextureRef[{ti}]: '{mat.TextureRefs[ti].Name}'");
                    }

                    var shaderModel = bfsha.ShaderModels[mat.ShaderAssign.ShadingModelName];
                    var bfshaProg = shaderModel.Programs[progIdx];
                    Console.WriteLine("  [BFSHA Sampler Indices to Shader Model Samplers]:");
                    for (int si = 0; si < shaderModel.Samplers.Count; si++)
                    {
                        var sName = shaderModel.Samplers.Keys.ElementAt(si);
                        var loc = bfshaProg.SamplerIndices[si];
                        if (loc.FragmentLocation != -1 || loc.VertexLocation != -1)
                        {
                            Console.WriteLine($"    Sampler[{si}] '{sName}': FragLoc={loc.FragmentLocation}, VtxLoc={loc.VertexLocation}");
                        }
                    }

                    if (shaderModel.SymbolData != null && shaderModel.SymbolData.Samplers != null)
                    {
                        Console.WriteLine("  [SymbolData.Samplers]:");
                        for (int i = 0; i < shaderModel.SymbolData.Samplers.Count; i++)
                        {
                            var sym = shaderModel.SymbolData.Samplers[i];
                            Console.WriteLine($"    Symbol[{i}]: Name1='{sym.Name1}', Val1='{sym.Value1}', Name2='{sym.Name2}', Val2='{sym.Value2}'");
                        }
                    }

                    if (binProg.FragmentShaderReflection != null)
                    {
                        Console.WriteLine("  [Fragment Samplers]:");
                        foreach (var kvp in binProg.FragmentShaderReflection.Samplers)
                        {
                            Console.WriteLine($"    Sampler: {kvp.Key} -> Location {binProg.FragmentShaderReflection.GetSamplerLocation(kvp.Key)}, Value {kvp.Value.Value}");
                        }
                        Console.WriteLine("  [Fragment UniformBuffers]:");
                        foreach (var kvp in binProg.FragmentShaderReflection.UniformBuffers)
                        {
                            Console.WriteLine($"    UBO: {kvp.Key} -> Location {binProg.FragmentShaderReflection.GetConstantBufferLocation(kvp.Key)}, Value {kvp.Value.Value}");
                        }
                    }

                    if (binProg.VertexShader != null && binProg.VertexShader.ByteCode != null)
                    {
                        Console.WriteLine($"Vertex Shader bytecode: {binProg.VertexShader.ByteCode.Length} bytes");
                        try
                        {
                            string vertGlsl = TegraShaderTranslator.Decompile(binProg.VertexShader.ByteCode);
                            string vertPath = Path.Combine(outDir, $"{t.label}_idx{progIdx}.vert");
                            File.WriteAllText(vertPath, vertGlsl);
                            Console.WriteLine($"  -> Decompiled Vertex Shader: {vertPath} ({vertGlsl.Length} chars)");

                            // Also try with ShaderExtract reflection
                            try
                            {
                                string vertExtracted = ShaderExtract.GetCode(binProg.VertexShader, binProg.VertexShaderReflection);
                                File.WriteAllText(Path.Combine(outDir, $"{t.label}_idx{progIdx}_extracted.vert"), vertExtracted);
                                Console.WriteLine($"  -> Extracted with reflection: {t.label}_idx{progIdx}_extracted.vert");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [ShaderExtract Vert Note] {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [Vertex Decompile FAILED] {ex.Message}");
                        }
                    }

                    if (binProg.FragmentShader != null && binProg.FragmentShader.ByteCode != null)
                    {
                        Console.WriteLine($"Fragment Shader bytecode: {binProg.FragmentShader.ByteCode.Length} bytes");
                        try
                        {
                            string fragGlsl = TegraShaderTranslator.Decompile(binProg.FragmentShader.ByteCode);
                            string fragPath = Path.Combine(outDir, $"{t.label}_idx{progIdx}.frag");
                            File.WriteAllText(fragPath, fragGlsl);
                            Console.WriteLine($"  -> Decompiled Fragment Shader: {fragPath} ({fragGlsl.Length} chars)");

                            // Also try with ShaderExtract reflection
                            try
                            {
                                string fragExtracted = ShaderExtract.GetCode(binProg.FragmentShader, binProg.FragmentShaderReflection);
                                File.WriteAllText(Path.Combine(outDir, $"{t.label}_idx{progIdx}_extracted.frag"), fragExtracted);
                                Console.WriteLine($"  -> Extracted with reflection: {t.label}_idx{progIdx}_extracted.frag");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [ShaderExtract Frag Note] {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [Fragment Decompile FAILED] {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed on {t.label}: {ex}");
                }
            }
        }

        static void PrintProgramOptions(ShaderModel shader, int progIdx)
        {
            int numKeysPerProgram = shader.StaticKeyLength + shader.DynamicKeyLength;
            int baseIndex = numKeysPerProgram * progIdx;
            Console.WriteLine($"  [Options for program {progIdx}]:");
            void Check(ResDict<ShaderOption> dict, bool isDynamic)
            {
                for (int j = 0; j < dict.Count; j++)
                {
                    var option = dict[j];
                    int keyIdx = isDynamic ? shader.StaticKeyLength + (option.Bit32Index - option.KeyOffset) : option.Bit32Index;
                    int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + keyIdx]);
                    string choice = choiceIndex >= 0 && choiceIndex < option.Choices.Count ? option.Choices.GetKey(choiceIndex) : $"<bad {choiceIndex}>";
                    if (choice != option.DefaultChoice)
                    {
                        Console.WriteLine($"    {option.Name} = {choice} (default: {option.DefaultChoice})");
                    }
                }
            }
            Check(shader.StaticOptions, false);
        }
        
        static void SearchPrograms(BfshaFile bfsha, ResFile resFile, string shapeName, string pipeline)
        {
            var shape = resFile.Models[0].Shapes[shapeName];
            var mat = resFile.Models[0].Materials[shape.MaterialIndex];
            var sm = bfsha.ShaderModels[mat.ShaderAssign.ShadingModelName];

            Console.WriteLine();
            Console.WriteLine($"=== SEARCHING PROGRAMS for {shapeName} ({mat.Name}) - pipeline: {pipeline} ===");
            int a0Idx = sm.Samplers.Keys.ToList().IndexOf("_a0");
            int n0Idx = sm.Samplers.Keys.ToList().IndexOf("_n0");
            int s0Idx = sm.Samplers.Keys.ToList().IndexOf("_s0");
            Console.WriteLine($"Sampler indices in model: _a0={a0Idx}, _n0={n0Idx}, _s0={s0Idx}");

            int numKeysPerProgram = sm.StaticKeyLength + sm.DynamicKeyLength;
            int found = 0;

            for (int i = 0; i < sm.Programs.Count; i++)
            {
                var prog = sm.Programs[i];
                bool hasA0 = a0Idx >= 0 && prog.SamplerIndices[a0Idx].FragmentLocation != -1;
                bool hasN0 = n0Idx >= 0 && prog.SamplerIndices[n0Idx].FragmentLocation != -1;
                if (!hasA0) continue;

                int baseIndex = numKeysPerProgram * i;
                string GetOpt(ShaderOption opt, bool isDyn)
                {
                    int kIdx = isDyn ? sm.StaticKeyLength + (opt.Bit32Index - opt.KeyOffset) : opt.Bit32Index;
                    int cIdx = opt.GetChoiceIndex(sm.KeyTable[baseIndex + kIdx]);
                    return cIdx >= 0 && cIdx < opt.Choices.Count ? opt.Choices.GetKey(cIdx) : "";
                }

                string assignType = sm.DynamicOptions.ContainsKey("gsys_assign_type") ? GetOpt(sm.DynamicOptions["gsys_assign_type"], true) : "";
                if (assignType != pipeline) continue;

                string renderState = sm.StaticOptions.ContainsKey("gsys_renderstate") ? GetOpt(sm.StaticOptions["gsys_renderstate"], false) : "";
                string gbufferXlu = sm.StaticOptions.ContainsKey("gsys_gbuffer_xlu") ? GetOpt(sm.StaticOptions["gsys_gbuffer_xlu"], false) : "";
                string preNorm = sm.StaticOptions.ContainsKey("o_expression_pre_normal") ? GetOpt(sm.StaticOptions["o_expression_pre_normal"], false) : "";
                if (preNorm != "2395356617") continue;

                found++;
                Console.WriteLine($"  [MATCH 2395356617] Prog {i}: assign={assignType}, renderstate={renderState}, xlu={gbufferXlu}, a0_loc={prog.SamplerIndices[a0Idx].FragmentLocation}, n0_loc={(n0Idx >= 0 ? prog.SamplerIndices[n0Idx].FragmentLocation : -1)}");

                var bin = sm.GetVariation(i).BinaryProgram;
                bool hasBones = bin.VertexShaderReflection != null && bin.VertexShaderReflection.Inputs.ContainsKey("aBlendWeight0");
                if (hasBones) continue;

                int mismatches = 0;
                foreach (var op in mat.ShaderAssign.ShaderOptions)
                {
                    if (op.Value == "<Default Value>") continue;
                    string choice = op.Value == "True" ? "1" : op.Value == "False" ? "0" : op.Value;
                    if (op.Key == "o_texture_scene_depth1_use_full_nld") choice = "0";
                    if (op.Key == "o_enable_ao") choice = "0";
                    if (op.Key == "o_enable_fresnel_cheat") choice = "1";
                    if (op.Key == "o_different_uv_texture0_decode") choice = "0";
                    if (op.Key == "o_inner_light_source_radius_end") choice = "0";

                    if (sm.StaticOptions.ContainsKey(op.Key))
                    {
                        var opt = sm.StaticOptions[op.Key];
                        string val = GetOpt(opt, false);
                        if (val != choice) mismatches++;
                    }
                    else if (sm.DynamicOptions.ContainsKey(op.Key))
                    {
                        var opt = sm.DynamicOptions[op.Key];
                        string val = GetOpt(opt, true);
                        if (val != choice) mismatches++;
                    }
                }

                found++;
                if (mismatches == 0 || found <= 5)
                {
                    Console.WriteLine($"  [OPAQUE] Prog {i}: mismatches={mismatches}, a0_loc={prog.SamplerIndices[a0Idx].FragmentLocation}, n0_loc={prog.SamplerIndices[n0Idx].FragmentLocation}, s0_loc={(s0Idx >= 0 ? prog.SamplerIndices[s0Idx].FragmentLocation : -1)}");
                }
            }
        }

        static void FindClosestToBlade(BfshaFile bfsha)
        {
            var sm = bfsha.ShaderModels["material"];
            int bladeProg = 17114;
            int numKeys = sm.StaticKeyLength + sm.DynamicKeyLength;

            int bestDiff = int.MaxValue;
            int bestProg = -1;

            int bladeBase = numKeys * bladeProg;
            for (int i = 0; i < sm.Programs.Count; i++)
            {
                int baseIdx = numKeys * i;

                var opt = sm.StaticOptions["o_expression_pre_normal"];
                int cIdx = opt.GetChoiceIndex(sm.KeyTable[baseIdx + opt.Bit32Index]);
                string val = cIdx >= 0 && cIdx < opt.Choices.Count ? opt.Choices.GetKey(cIdx) : "";
                if (val != "2395356617") continue;

                var assignOpt = sm.DynamicOptions["gsys_assign_type"];
                int aIdx = assignOpt.GetChoiceIndex(sm.KeyTable[baseIdx + sm.StaticKeyLength + (assignOpt.Bit32Index - assignOpt.KeyOffset)]);
                string aVal = aIdx >= 0 && aIdx < assignOpt.Choices.Count ? assignOpt.Choices.GetKey(aIdx) : "";
                if (aVal != "gsys_assign_gbuffer") continue;

                int diff = 0;
                for (int k = 0; k < numKeys; k++)
                {
                    if (sm.KeyTable[baseIdx + k] != sm.KeyTable[bladeBase + k])
                        diff++;
                }

                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestProg = i;
                    Console.WriteLine($"New Best Match: Prog {i} with diff={diff} from Blade 17114");
                }
            }
            Console.WriteLine($"\nFINAL BEST MATCH: Prog {bestProg} with diff={bestDiff} from Blade 17114!");
        }
    }
}
