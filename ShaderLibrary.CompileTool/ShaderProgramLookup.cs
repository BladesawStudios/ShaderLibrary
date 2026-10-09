using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BfresLibrary;
using EffectLibraryTest;
using ShaderLibrary;

namespace ShaderLibrary.CompileTool
{
    public static class ShaderProgramLookup
    {
        public static Tuple<int, BnshFile.BnshShaderProgram> GetShaderProgram(BfshaFile bfsha, ResFile resFile, string mesh_name, string pipeline)
        {
            var model = resFile.Models[0];
            var shape = model.Shapes[mesh_name];
            var material = model.Materials[shape.MaterialIndex];
            var shader = bfsha.ShaderModels[material.ShaderAssign.ShadingModelName];

            //get options
            var shader_options = GetOptionSearch(shader, material, shape, pipeline);
            NormalizeOptionsForShader(shader, shader_options);

            if (Environment.GetEnvironmentVariable("MC_DEBUG_OPTIONSEARCH") == "1")
            {
                void DumpAll(string label, ResDict<ShaderOption> dict)
                {
                    foreach (var name in dict.Keys)
                    {
                        var opt = dict[name];
                        string supplied = shader_options.ContainsKey(name) ? shader_options[name] : "(unset -> default)";
                        Console.WriteLine($"      {label} {name,-40} supplied={supplied,-20} default={opt.DefaultChoice} choices=[{string.Join(",", opt.Choices.Keys)}]");
                    }
                }
                DumpAll("static ", shader.StaticOptions);
                DumpAll("dynamic", shader.DynamicOptions);
            }

            //get program
            var programIdx = shader.GetProgramIndex(shader_options);

            if (programIdx == -1)
                programIdx = ResolveViaClosestProgram(shader, shader_options);

            Console.WriteLine($"Found index {programIdx}");

            //get target variation data
            return Tuple.Create(programIdx, shader.GetVariation(programIdx).BinaryProgram);
        }

        /// <summary>
        /// Some material ShaderOptions store a raw author-facing numeric value (seen: 3, 10, 40,
        /// 202, 2395356617 for options like o_enable_fresnel_cheat/o_enable_ao) even though the
        /// compiled .bfsha for that option only has a small set of choices (commonly just "0"/"1"
        /// - i.e. the option is functionally boolean at the compiled-shader level). Verified this
        /// is genuine on-disk material data, not a parsing bug, by cross-checking two different
        /// materials in the same model: both independently produce the same values through
        /// completely consistent, in-range OptionIndices lookups.
        ///
        /// TotK's own asset pipeline must resolve these at shader-compile time using a rule that
        /// isn't stored in either file we have access to. First guess was "nonzero means the
        /// choice bit is set", but FindClosestProgram disproved that empirically: the
        /// closest-matching real program for every affected option wants its DEFAULT choice
        /// regardless of the material's raw numeric value, not a truthiness-derived "1". So these
        /// numeric author values apparently aren't shader-compile inputs at all for this material/
        /// shader - the compiled variant always uses the option's own default whenever the raw
        /// value isn't literally a valid choice key. Only touches options whose raw material
        /// value isn't already a valid choice, so real "True"/"False" toggles and numeric choices
        /// that DO exist are left untouched.
        /// </summary>
        static void NormalizeOptionsForShader(ShaderModel shader, Dictionary<string, string> options)
        {
            void Fix(ResDict<ShaderOption> optionDict)
            {
                foreach (var key in options.Keys.ToList())
                {
                    if (!optionDict.ContainsKey(key))
                        continue;

                    ShaderOption option = optionDict[key];
                    string value = options[key];
                    if (option.Choices.GetIndex(value) != -1)
                        continue; // already a valid choice, leave it alone

                    Console.WriteLine($"      [normalize] {key}: \"{value}\" -> \"{option.DefaultChoice}\" (default)");
                    options[key] = option.DefaultChoice;
                }
            }

            Fix(shader.StaticOptions);
            Fix(shader.DynamicOptions);
        }

        /// <summary>
        /// When GetProgramIndex finds no exact match, some material option values are "valid" in
        /// isolation (a real choice key for that option) but still don't correspond to any
        /// compiled program - verified concretely for Mt_Sword_070_Blade's
        /// o_inner_light_source_radius_end="202": that's a real choice, but its feature gate
        /// (o_enable_inner_light) is off, and TotK's shader compiler collapses a disabled
        /// feature's dependent sub-options to their defaults across every compiled variant, so no
        /// program actually carries the material's raw "202". Rather than hardcode which options
        /// gate which (a per-shader, per-option-family relationship we don't have a spec for),
        /// this finds the program(s) that disagree with our current guess in the fewest places,
        /// and - only when every tied closest program unanimously agrees on the same required
        /// value for a mismatching option - adopts that value and retries. That unanimity check is
        /// the safety rail: it means "every real compiled variant closest to what we asked for
        /// agrees this option must be X", not "here's an arbitrary program's opinion". Repeats
        /// (in case fixing one option's ambiguity resolves once others are pinned down) until it
        /// either reaches an exact match, makes no further progress, or the mismatch set stops
        /// being resolvable unanimously - at which point it gives up and returns -1 rather than
        /// guess.
        /// </summary>
        static int ResolveViaClosestProgram(ShaderModel shader, Dictionary<string, string> options)
        {
            const int maxIterations = 8;
            for (int iter = 0; iter < maxIterations; iter++)
            {
                var (bestMismatches, bestPrograms, diffsByProgram) = FindClosestPrograms(shader, options);

                if (bestMismatches == 0)
                    return bestPrograms[0];

                // Only apply a fix where every tied-closest program wants the exact same value -
                // otherwise we'd be guessing which program's opinion to trust.
                var unanimous = new Dictionary<string, string>();
                foreach (var key in diffsByProgram[0].Keys)
                {
                    string firstWanted = diffsByProgram[0][key];
                    if (diffsByProgram.All(d => d.TryGetValue(key, out string? v) && v == firstWanted))
                        unanimous[key] = firstWanted;
                }

                if (unanimous.Count == 0)
                {
                    Console.WriteLine($"      [closest] {bestPrograms.Count} program(s) tie at {bestMismatches} mismatch(es), but disagree on the fix - giving up rather than guess:");
                    foreach (var key in diffsByProgram[0].Keys)
                        Console.WriteLine($"        {key,-40} ours=\"{options[key]}\" candidates=[{string.Join(",", diffsByProgram.Select(d => d.GetValueOrDefault(key, "?")).Distinct())}]");
                    return -1;
                }

                foreach (var kv in unanimous)
                {
                    Console.WriteLine($"      [closest-fix] {kv.Key}: \"{options[kv.Key]}\" -> \"{kv.Value}\" ({bestPrograms.Count} closest program(s) unanimously agree)");
                    options[kv.Key] = kv.Value;
                }
            }

            Console.WriteLine($"      [closest] gave up after {maxIterations} iterations without an exact match");
            return -1;
        }

        /// <summary>
        /// Finds the program(s) whose key table disagrees with our supplied options in the fewest
        /// places. Returns the mismatch count, the tied-closest program indices, and each of
        /// those programs' (optionName -> value it actually wants) diff, all in the same order.
        /// </summary>
        static (int mismatches, List<int> programs, List<Dictionary<string, string>> diffs) FindClosestPrograms(
            ShaderModel shader, Dictionary<string, string> options)
        {
            int numKeysPerProgram = shader.StaticKeyLength + shader.DynamicKeyLength;
            int bestMismatches = int.MaxValue;
            var bestPrograms = new List<int>();
            var bestDiffs = new List<Dictionary<string, string>>();

            for (int i = 0; i < shader.Programs.Count; i++)
            {
                int baseIndex = numKeysPerProgram * i;
                var diff = new Dictionary<string, string>();

                void Check(ResDict<ShaderOption> dict, bool isDynamic)
                {
                    for (int j = 0; j < dict.Count; j++)
                    {
                        var option = dict[j];
                        if (!options.ContainsKey(option.Name))
                            continue;

                        int keyIdx = isDynamic ? shader.StaticKeyLength + (option.Bit32Index - option.KeyOffset) : option.Bit32Index;
                        int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + keyIdx]);
                        string choice = choiceIndex >= 0 && choiceIndex < option.Choices.Count ? option.Choices.GetKey(choiceIndex) : $"<bad idx {choiceIndex}>";
                        if (options[option.Name] != choice)
                            diff[option.Name] = choice;
                    }
                }
                Check(shader.StaticOptions, false);
                Check(shader.DynamicOptions, true);

                if (diff.Count < bestMismatches)
                {
                    bestMismatches = diff.Count;
                    bestPrograms.Clear();
                    bestPrograms.Add(i);
                    bestDiffs.Clear();
                    bestDiffs.Add(diff);
                }
                else if (diff.Count == bestMismatches)
                {
                    bestPrograms.Add(i);
                    bestDiffs.Add(diff);
                }
            }

            return (bestMismatches, bestPrograms, bestDiffs);
        }

        static Dictionary<string, string> GetOptionSearch(ShaderModel shader, Material material, Shape shape, string pipeline = "gsys_assign_material")
        {
            Dictionary<string, string> options = new Dictionary<string, string>();
            foreach (var op in material.ShaderAssign.ShaderOptions)
            {
                string choice = op.Value.ToString();
                if (op.Value == "True")
                    choice = "1";
                else if (op.Value == "False")
                    choice = "0";
                else if (op.Value == "<Default Value>")
                    continue;

                options.Add(op.Key, choice);
            }

            options.Add("gsys_weight", shape.VertexSkinCount.ToString()); //skin count
            options.Add("gsys_assign_type", pipeline); //material pass

            //render info configures options of compiled shaders (alpha testing and render state)
            var renderMode = material.GetRenderInfoString("gsys_render_state_mode");
            var alphaTest = material.GetRenderInfoString("gsys_alpha_test_enable");

            // Derived from RenderInfo, not hardcoded. Every material seen so far uses "gequal"
            // (which IS 6), so the old constant happened to be right - but it would silently
            // pick the wrong variant for anything that does not.
            var alphaFunc = material.GetRenderInfoString("gsys_alpha_test_func");
            if (!AlphaTestFuncs.TryGetValue(alphaFunc ?? "", out string? funcValue))
            {
                funcValue = "6";
                if (!string.IsNullOrEmpty(alphaFunc) && alphaFunc != "gequal")
                    Console.WriteLine($"      [gsys_alpha_test_func] unknown \"{alphaFunc}\" - defaulting to gequal");
            }
            options["gsys_alpha_test_func"] = funcValue;

            // These used to be gated behind `options.ContainsKey(...)`, which was always false -
            // that key is never added anywhere earlier in this function, so the RenderInfo-derived
            // values were computed and then silently discarded. The real question is whether the
            // COMPILED SHADER has these as options at all (some shading models don't use one or
            // the other), and whether the RenderInfo actually had a value worth applying.
            if (shader.StaticOptions.ContainsKey("gsys_renderstate") || shader.DynamicOptions.ContainsKey("gsys_renderstate"))
            {
                if (RenderStateModes.TryGetValue(renderMode, out string? renderState))
                    options["gsys_renderstate"] = renderState;
                else if (Environment.GetEnvironmentVariable("MC_DEBUG_OPTIONSEARCH") == "1")
                    Console.WriteLine($"      [gsys_renderstate] material's gsys_render_state_mode = \"{renderMode}\" not in RenderStateModes {{{string.Join(",", RenderStateModes.Keys)}}} - leaving at shader default");
            }

            if (shader.StaticOptions.ContainsKey("gsys_alpha_test_enable") || shader.DynamicOptions.ContainsKey("gsys_alpha_test_enable"))
                options["gsys_alpha_test_enable"] = alphaTest == "true" ? "1" : "0";

            return options;
        }

        /// <summary>GL comparison-function order, which is what the option's choice list uses.</summary>
        static Dictionary<string, string> AlphaTestFuncs = new Dictionary<string, string>()
        {
            { "never", "0" }, { "less", "1" }, { "equal", "2" }, { "lequal", "3" },
            { "greater", "4" }, { "notequal", "5" }, { "gequal", "6" }, { "always", "7" },
        };

        static Dictionary<string, string> RenderStateModes = new Dictionary<string, string>()
        {
            { "opaque", "0" },
            { "mask", "1" },
            { "translucent", "2" },
            { "custom", "3" },
        };
    }
}
