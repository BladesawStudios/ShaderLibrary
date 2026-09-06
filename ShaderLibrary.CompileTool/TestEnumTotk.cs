using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;
using EffectLibraryTest;

namespace ShaderLibrary.CompileTool
{
    public static class TestEnumTotk
    {
        public static void EnumBfsha(string path, string outputTxt)
        {
            var bfsha = new BfshaFile(path);

            using var writer = new StreamWriter(outputTxt);

            writer.WriteLine($"=== {path} ===");
            writer.WriteLine($"ShaderModels count: {bfsha.ShaderModels.Count}");

            foreach (var kv in bfsha.ShaderModels)
            {
                var name = kv.Key;
                var model = kv.Value;

                writer.WriteLine();
                writer.WriteLine($"---- ShaderModel: {name} ----");
                writer.WriteLine($"Programs: {model.Programs.Count}");

                writer.WriteLine("StaticOptions:");
                foreach (var opt in model.StaticOptions)
                {
                    writer.WriteLine($"  {opt.Key} (default={opt.Value.DefaultChoice}) choices=[{string.Join(",", opt.Value.Choices.Keys)}]");
                }

                writer.WriteLine("DynamicOptions:");
                foreach (var opt in model.DynamicOptions)
                {
                    writer.WriteLine($"  {opt.Key} (default={opt.Value.DefaultChoice}) choices=[{string.Join(",", opt.Value.Choices.Keys)}]");
                }

                writer.WriteLine("Samplers:");
                foreach (var s in model.Samplers.Keys)
                    writer.WriteLine($"  {s}");

                writer.WriteLine("UniformBlocks:");
                foreach (var b in model.UniformBlocks)
                    writer.WriteLine($"  {b.Key} type={b.Value.Type} size={b.Value.Size}");

                writer.WriteLine("StorageBuffers:");
                foreach (var b in model.StorageBuffers.Keys)
                    writer.WriteLine($"  {b}");
            }
        }

        // Reads any option (static OR dynamic) by name for a given program index.
        // BfshaFile.ShaderModel.GetOptionChoice only handles StaticOptions; this covers both,
        // mirroring the logic in ShaderModel.PrintProgramKeys.
        static string GetAnyOptionChoice(ShaderModel model, int programIndex, string optionName)
        {
            int numKeysPerProgram = model.StaticKeyLength + model.DynamicKeyLength;
            int baseIndex = numKeysPerProgram * programIndex;

            if (model.StaticOptions.ContainsKey(optionName))
            {
                var option = model.StaticOptions[optionName];
                int choiceIndex = option.GetChoiceIndex(model.KeyTable[baseIndex + option.Bit32Index]);
                if (choiceIndex < 0 || choiceIndex > option.Choices.Count)
                    return "<invalid>";
                return option.Choices.GetKey(choiceIndex);
            }
            if (model.DynamicOptions.ContainsKey(optionName))
            {
                var option = model.DynamicOptions[optionName];
                int ind = option.Bit32Index - option.KeyOffset;
                int choiceIndex = option.GetChoiceIndex(model.KeyTable[baseIndex + model.StaticKeyLength + ind]);
                if (choiceIndex < 0 || choiceIndex > option.Choices.Count)
                    return "<invalid>";
                return option.Choices.GetKey(choiceIndex);
            }
            return "<n/a>";
        }

        // Groups every program in a shading model by a tuple of option values + its
        // fragment reflection signature (outputs/samplers), and dumps ONE representative
        // per group (index + options + samplers/outputs + decompiled GLSL) so we can
        // spot which option combination writes which deferred prepass texture.
        public static void GroupAndDump(string bfshaPath, string shaderModelName, string outputTxt,
            string[] groupByOptions, bool includeCode)
        {
            var bfsha = new BfshaFile(bfshaPath);
            var model = bfsha.ShaderModels[shaderModelName];

            using var writer = new StreamWriter(outputTxt);
            writer.WriteLine($"=== {shaderModelName} in {bfshaPath} ===");
            writer.WriteLine($"Total programs: {model.Programs.Count}");

            var seenGroups = new HashSet<string>();

            for (int i = 0; i < model.Programs.Count; i++)
            {
                var optionValues = groupByOptions.Select(o => $"{o}={GetAnyOptionChoice(model, i, o)}").ToList();

                var variation = model.GetVariation(i);
                var program = variation?.BinaryProgram;
                var fragReflect = program?.FragmentShaderReflection;

                string outputsKey = fragReflect != null ? string.Join(",", fragReflect.Outputs.Keys) : "<none>";
                string samplersKey = fragReflect != null ? string.Join(",", fragReflect.Samplers.Keys) : "<none>";

                string groupKey = string.Join("|", optionValues) + "||OUT:" + outputsKey + "||SAMP:" + samplersKey;

                if (!seenGroups.Add(groupKey))
                    continue; // already dumped a representative for this exact combo

                writer.WriteLine();
                writer.WriteLine($"==== Program {i} (group representative) ====");
                foreach (var ov in optionValues)
                    writer.WriteLine($"  {ov}");

                if (fragReflect == null)
                {
                    writer.WriteLine("  <no fragment reflection>");
                    continue;
                }

                writer.WriteLine($"  Frag Outputs: [{outputsKey}]");
                writer.WriteLine($"  Frag Samplers: [{samplersKey}]");
                writer.WriteLine($"  Frag UniformBuffers: [{string.Join(", ", fragReflect.UniformBuffers.Keys)}]");

                if (includeCode && program.FragmentShader != null)
                {
                    try
                    {
                        string code = ShaderExtract.GetCode(program.FragmentShader, fragReflect);
                        writer.WriteLine("  ---- GLSL ----");
                        writer.WriteLine(code);
                        writer.WriteLine("  ---- END GLSL ----");
                    }
                    catch (Exception ex)
                    {
                        writer.WriteLine($"  <decompile failed: {ex.Message}>");
                    }
                }
            }

            writer.WriteLine();
            writer.WriteLine($"Total distinct groups: {seenGroups.Count}");
        }

        // Filters a shading model's programs down to those matching a partial option dictionary
        // (uses IsValidProgram semantics: only checks options present in the dict, others free),
        // then dumps ONE representative per distinct (options-of-interest, frag outputs, frag samplers)
        // combination. Used to hunt through the huge "material" shading model (22944 programs) for
        // whichever assign_type/option combo writes the deferred light/shadow/fog/misc prepass targets.
        public static void FilterAndDump(string bfshaPath, string shaderModelName, string outputTxt,
            Dictionary<string, string> filterOptions, string[] groupByOptions)
        {
            var bfsha = new BfshaFile(bfshaPath);
            var model = bfsha.ShaderModels[shaderModelName];

            using var writer = new StreamWriter(outputTxt);
            writer.WriteLine($"=== {shaderModelName} in {bfshaPath} ===");
            writer.WriteLine($"Total programs: {model.Programs.Count}");
            writer.WriteLine($"Filter: {string.Join(", ", filterOptions.Select(kv => kv.Key + "=" + kv.Value))}");

            var matches = model.GetProgramIndexList(filterOptions);
            writer.WriteLine($"Matching programs: {matches.Count}");

            var seenGroups = new HashSet<string>();
            int dumped = 0;

            foreach (var i in matches)
            {
                var optionValues = groupByOptions.Select(o => $"{o}={GetAnyOptionChoice(model, i, o)}").ToList();

                var variation = model.GetVariation(i);
                var program = variation?.BinaryProgram;
                var fragReflect = program?.FragmentShaderReflection;

                string outputsKey = fragReflect != null ? string.Join(",", fragReflect.Outputs.Keys) : "<none>";
                string samplersKey = fragReflect != null ? string.Join(",", fragReflect.Samplers.Keys) : "<none>";

                string groupKey = string.Join("|", optionValues) + "||OUT:" + outputsKey + "||SAMP:" + samplersKey;
                if (!seenGroups.Add(groupKey))
                    continue;

                dumped++;
                writer.WriteLine();
                writer.WriteLine($"==== Program {i} (group representative #{dumped}) ====");
                foreach (var ov in optionValues)
                    writer.WriteLine($"  {ov}");

                if (fragReflect == null)
                {
                    writer.WriteLine("  <no fragment reflection>");
                    continue;
                }

                writer.WriteLine($"  Frag Outputs: [{outputsKey}]");
                writer.WriteLine($"  Frag Samplers: [{samplersKey}]");
                writer.WriteLine($"  Frag UniformBuffers: [{string.Join(", ", fragReflect.UniformBuffers.Keys)}]");
            }

            writer.WriteLine();
            writer.WriteLine($"Total distinct groups dumped: {seenGroups.Count}");
        }

        // Checks every program in every ShaderModel of a bfsha for a populated ComputeShader,
        // GeometryShader, or a FragmentShader with 0/nonstandard outputs -- anything that isn't
        // a plain single-MRT fragment shader, which is what a light/shadow/fog prepass writer
        // would look like if it's not hiding among the "normal" vertex+fragment programs.
        public static void ScanForComputeOrOddPrograms(string bfshaPath, string outputTxt)
        {
            var bfsha = new BfshaFile(bfshaPath);
            using var writer = new StreamWriter(outputTxt);
            writer.WriteLine($"=== {bfshaPath} ===");

            foreach (var kv in bfsha.ShaderModels)
            {
                var name = kv.Key;
                var model = kv.Value;
                writer.WriteLine($"---- ShaderModel: {name} ({model.Programs.Count} programs, {model.BnshFile.Variations.Count} variations) ----");

                int computeCount = 0, geomCount = 0, noVertCount = 0, noFragCount = 0;
                for (int i = 0; i < model.BnshFile.Variations.Count; i++)
                {
                    var program = model.BnshFile.Variations[i].BinaryProgram;
                    if (program == null) continue;
                    if (program.ComputeShader != null) computeCount++;
                    if (program.GeometryShader != null) geomCount++;
                    if (program.VertexShader == null) noVertCount++;
                    if (program.FragmentShader == null) noFragCount++;
                }
                writer.WriteLine($"  ComputeShader populated: {computeCount}");
                writer.WriteLine($"  GeometryShader populated: {geomCount}");
                writer.WriteLine($"  Missing VertexShader: {noVertCount}");
                writer.WriteLine($"  Missing FragmentShader: {noFragCount}");
            }
        }

        // Lists every ShaderProgram in a .sharcb (SharcfbFile / "BAHS" magic) archive: name,
        // vertex/pixel/geometry stage bitmask, and its variation macros (the sharcb equivalent
        // of bfsha's static/dynamic options). Used to hunt for light/shadow/fog/misc prepass
        // programs inside AGL shader archives like AglLightShader.sharcb.
        public static void EnumSharcb(string path, string outputTxt)
        {
            var sharc = new SharcfbFile(path);
            using var writer = new StreamWriter(outputTxt);
            writer.WriteLine($"=== {path} ===");
            writer.WriteLine($"Name: {sharc.Name}");
            writer.WriteLine($"Version: {sharc.GetVersion()}");
            writer.WriteLine($"Variations: {sharc.Variations.Count}");
            writer.WriteLine($"Programs: {sharc.Programs.Count}");
            writer.WriteLine();

            foreach (var program in sharc.Programs)
            {
                writer.WriteLine($"---- {program.Name} ----");
                writer.WriteLine($"  Kind={program.Kind} (vert={program.HasVertexShader()}, pixel={program.HasPixelShader()}, geom={program.HasGeometryShader()}) BaseIndex={program.BaseIndex}");
                foreach (var macro in program.VariationMacros)
                    writer.WriteLine($"  macro {macro.Name} = [{string.Join(",", macro.Values)}]");
            }
        }

        // Dumps full detail (samplers/uniform blocks/attributes/buffers) for every variation of
        // one named program in a .sharcb archive, plus the extracted+renamed GLSL for each.
        public static void DumpSharcProgram(string path, string programName, string outDir)
        {
            var sharc = new SharcfbFile(path);
            var program = sharc.Programs.First(p => p.Name == programName);

            Directory.CreateDirectory(outDir);

            using var writer = new StreamWriter(Path.Combine(outDir, $"{programName}_info.txt"));
            writer.WriteLine($"Program: {program.Name}");
            writer.WriteLine($"Kind={program.Kind} vert={program.HasVertexShader()} pixel={program.HasPixelShader()} geom={program.HasGeometryShader()}");
            foreach (var macro in program.VariationMacros)
                writer.WriteLine($"macro {macro.Name} = [{string.Join(",", macro.Values)}]");

            var combos = program.GetAllVariationCombinations().ToList();
            writer.WriteLine($"Total variation combinations: {combos.Count}");

            int stagesPerVariation = program.HasGeometryShader() ? 3 : 2;
            // Just dump variation 0 (or all, if there are only a few) fully.
            int dumpCount = Math.Min(combos.Count, 6);
            for (int v = 0; v < dumpCount; v++)
            {
                var options = combos.Count > 0 ? combos[v] : new Dictionary<string, string>();
                int variationIdx = combos.Count > 0 ? program.GetVariationIndex(options) : 0;
                int baseIdx = program.GetBinaryIndex(variationIdx);

                writer.WriteLine();
                writer.WriteLine($"== Variation {v}: idx={variationIdx} options=[{string.Join(",", options.Select(o => o.Key + "=" + o.Value))}] ==");

                SharcfbFile.ShaderVariation? vert = program.HasVertexShader() ? sharc.Variations[baseIdx] : null;
                SharcfbFile.ShaderVariation? pixel = program.HasPixelShader() ? sharc.Variations[baseIdx + (program.HasVertexShader() ? 1 : 0)] : null;

                void DumpStage(SharcfbFile.ShaderVariation? sv, string stageName)
                {
                    if (sv == null) { writer.WriteLine($"  <no {stageName}>"); return; }
                    writer.WriteLine($"  {stageName} Samplers: [{string.Join(", ", sv.Samplers.Select(s => s.Name))}]");
                    writer.WriteLine($"  {stageName} UniformBlocks: [{string.Join(", ", sv.UniformBlocks.Select(s => s.Name))}]");
                    writer.WriteLine($"  {stageName} Attributes: [{string.Join(", ", sv.Attributes.Select(s => s.Name))}]");
                    writer.WriteLine($"  {stageName} Buffers: [{string.Join(", ", sv.Buffers.Select(s => s.Name))}]");

                    try
                    {
                        var fakeCode = new BnshFile.ShaderCode { ControlCode = sv.ControlShader, ByteCode = sv.ByteCode };
                        string code = ShaderExtract.GetCode(fakeCode);
                        code = SetSharcNames(code, sv);
                        string outFile = Path.Combine(outDir, $"{programName}_v{v}_{stageName}.glsl");
                        File.WriteAllText(outFile, code);
                        writer.WriteLine($"  -> wrote {outFile}");
                    }
                    catch (Exception ex)
                    {
                        writer.WriteLine($"  <decompile failed: {ex.Message}>");
                    }
                }

                DumpStage(vert, "vert");
                DumpStage(pixel, "pixel");
            }
        }

        // Like DumpSharcProgram, but for one explicit option combination (instead of the first
        // few variations in enumeration order) -- lets us target e.g. IS_ENABLE_SPECULAR=1.
        public static void DumpSharcProgramVariation(string path, string programName,
            Dictionary<string, string> options, string outDir, string tag)
        {
            var sharc = new SharcfbFile(path);
            var program = sharc.Programs.First(p => p.Name == programName);
            Directory.CreateDirectory(outDir);

            int variationIdx = program.GetVariationIndex(options);
            int baseIdx = program.GetBinaryIndex(variationIdx);

            using var writer = new StreamWriter(Path.Combine(outDir, $"{programName}_{tag}_info.txt"));
            writer.WriteLine($"Program: {program.Name} tag={tag} variationIdx={variationIdx}");
            writer.WriteLine($"options=[{string.Join(",", options.Select(o => o.Key + "=" + o.Value))}]");

            SharcfbFile.ShaderVariation? vert = program.HasVertexShader() ? sharc.Variations[baseIdx] : null;
            SharcfbFile.ShaderVariation? pixel = program.HasPixelShader() ? sharc.Variations[baseIdx + (program.HasVertexShader() ? 1 : 0)] : null;

            void DumpStage(SharcfbFile.ShaderVariation? sv, string stageName)
            {
                if (sv == null) { writer.WriteLine($"  <no {stageName}>"); return; }
                writer.WriteLine($"  {stageName} Samplers: [{string.Join(", ", sv.Samplers.Select(s => s.Name))}]");
                writer.WriteLine($"  {stageName} UniformBlocks: [{string.Join(", ", sv.UniformBlocks.Select(s => s.Name))}]");
                writer.WriteLine($"  {stageName} Attributes: [{string.Join(", ", sv.Attributes.Select(s => s.Name))}]");
                writer.WriteLine($"  {stageName} Buffers: [{string.Join(", ", sv.Buffers.Select(s => s.Name))}]");

                try
                {
                    var fakeCode = new BnshFile.ShaderCode { ControlCode = sv.ControlShader, ByteCode = sv.ByteCode };
                    string code = ShaderExtract.GetCode(fakeCode);
                    code = SetSharcNames(code, sv);
                    string outFile = Path.Combine(outDir, $"{programName}_{tag}_{stageName}.glsl");
                    File.WriteAllText(outFile, code);
                    writer.WriteLine($"  -> wrote {outFile}");
                }
                catch (Exception ex)
                {
                    writer.WriteLine($"  <decompile failed: {ex.Message}>");
                }
            }

            DumpStage(vert, "vert");
            DumpStage(pixel, "pixel");
        }

        // Debug helper: dumps the RAW TegraShaderTranslator.Decompile output (no ApplyConstants/
        // FixLocations/name substitution) for one sharc variation, to inspect how the immediate
        // constant buffer (c1) is actually named by this Ryujinx build.
        public static void DumpRawDecompile(string path, string programName, Dictionary<string, string> options, string outFile)
        {
            var sharc = new SharcfbFile(path);
            var program = sharc.Programs.First(p => p.Name == programName);
            int variationIdx = program.GetVariationIndex(options);
            int baseIdx = program.GetBinaryIndex(variationIdx);
            var pixel = sharc.Variations[baseIdx + (program.HasVertexShader() ? 1 : 0)];

            string raw = TegraShaderTranslator.Decompile(pixel.ByteCode);
            File.WriteAllText(outFile, raw);

            var control = new global::ShaderLibrary.ControlShader(pixel.ControlShader);
            float[] constants = control.GetConstantsAsFloats(pixel.ByteCode);
            File.WriteAllText(outFile + ".constants.txt", string.Join("\n", constants.Select((c, i) => $"{i}: {c}")));
        }

        // Mirrors ShaderExtract.SetReflectionNames but sources real symbol names from a
        // SharcfbFile.ShaderVariation (sharcb format) instead of BnshFile.ShaderReflectionData.
        static string SetSharcNames(string code, SharcfbFile.ShaderVariation variation)
        {
            Dictionary<string, string> symbols = new();

            foreach (var sampler in variation.Samplers)
            {
                int location = sampler.Location;
                if (location < 0) continue;
                symbols[$"vp_t_tcb_{((location * 2) + 8):X1}"] = sampler.Name;
                symbols[$"fp_t_tcb_{((location * 2) + 8):X1}"] = sampler.Name;
            }

            foreach (var block in variation.UniformBlocks)
            {
                int location = block.Location;
                if (location < 0) continue;
                symbols[$"_fp_c{location + 3}"] = $"_{block.Name}";
                symbols[$"_vp_c{location + 3}"] = $"_{block.Name}";
                symbols[$"fp_c{location + 3}_1"] = block.Name;
                symbols[$"vp_c{location + 3}_1"] = block.Name;
            }

            foreach (var attr in variation.Attributes)
            {
                int location = attr.Location;
                if (location < 0) continue;
                symbols[$"in_attr{location}"] = attr.Name;
                symbols[$"out_attr{location}"] = attr.Name;
            }

            var sb = new System.Text.StringBuilder();
            using var reader = new StringReader(code);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                foreach (var sym in symbols)
                {
                    if (line.Contains(sym.Key))
                        line = line.Replace(sym.Key, sym.Value);
                }
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        // Extract one specific program's fragment (and optionally vertex) shader code to a file,
        // using the standard ShaderExtract pipeline (raw decompile + constant/location/name fixups).
        public static void ExtractProgram(string bfshaPath, string shaderModelName, int programIndex,
            string outFrag, string? outVert = null)
        {
            var bfsha = new BfshaFile(bfshaPath);
            var model = bfsha.ShaderModels[shaderModelName];
            var variation = model.GetVariation(programIndex);
            var program = variation.BinaryProgram;

            if (program.FragmentShader != null)
            {
                string fragCode = ShaderExtract.GetCode(program.FragmentShader, program.FragmentShaderReflection);
                File.WriteAllText(outFrag, fragCode);
            }

            if (outVert != null && program.VertexShader != null)
            {
                string vertCode = ShaderExtract.GetCode(program.VertexShader, program.VertexShaderReflection);
                File.WriteAllText(outVert, vertCode);
            }
        }
    }
}
