using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BfresLibrary;
using EffectLibraryTest;
using ShaderLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Static analysis of TotK's SYSTEM shader archive (system.*.bfsha) - the deferred-lighting
    /// half of the renderer, as opposed to material.*.bfsha which is the G-Buffer half.
    ///
    /// The point of this pass is to turn the raw decompiled deferred shaders (which reference
    /// anonymous "fp_c4.data[45].x" / "fp_t_tcb_14" slots) into something a test bench can
    /// actually FEED CORRECTLY, by recovering three things from the archive itself rather than
    /// guessing:
    ///
    ///   1. fp_cN -> gsys uniform block identity. Ryujinx's decompiler names fragment constant
    ///      buffers fp_c{location+3}; the BNSH reflection table maps location -> real block name
    ///      ("Context", "Env", "SceneMat", ...). fp_c1 specifically is NOT a bound block at all -
    ///      it is the immediate constant buffer baked into the shader ISA, recoverable from the
    ///      control section (see ShaderExtract.ApplyConstants).
    ///   2. The byte layout INSIDE each of those blocks - BFSHA carries per-uniform names and
    ///      DataOffsets, so "fp_c9.data[52].y" resolves to a named Env field instead of a magic
    ///      index. This is what makes it possible to fill the block with real values.
    ///   3. fp_t_tcb_HH -> sampler identity, via the same reflection table
    ///      (location = 0xHH/2 - 4), e.g. cTex_GBuffAlbedo / cTex_DepthShadowCascade.
    ///
    /// Everything printed here is read straight out of the shipped files - nothing is inferred.
    /// </summary>
    public static class TestSystemShading
    {
        public static void Run(string systemBfshaPath, string deferredBfresPath, string outDir)
        {
            Directory.CreateDirectory(outDir);

            Console.WriteLine("################################################################");
            Console.WriteLine($"# System shader archive: {systemBfshaPath}");
            Console.WriteLine("################################################################");

            var bfsha = new BfshaFile(systemBfshaPath);
            Console.WriteLine($"ShaderModels ({bfsha.ShaderModels.Count}): {string.Join(", ", bfsha.ShaderModels.Keys)}");

            foreach (var smEntry in bfsha.ShaderModels)
            {
                var sm = smEntry.Value;
                Console.WriteLine();
                Console.WriteLine($"=== ShaderModel \"{smEntry.Key}\" ===");
                Console.WriteLine($"  Programs: {sm.Programs.Count}, StaticOptions: {sm.StaticOptions.Count}, DynamicOptions: {sm.DynamicOptions.Count}");
                Console.WriteLine($"  UniformBlocks: {sm.UniformBlocks.Count}, Samplers: {sm.Samplers.Count}, Attributes: {sm.Attributes.Count}");

                DumpUniformBlocks(sm, outDir, smEntry.Key);
                DumpSamplers(sm);
            }

            if (!string.IsNullOrEmpty(deferredBfresPath) && File.Exists(deferredBfresPath))
            {
                var resolved = DumpDeferredModel(bfsha, deferredBfresPath);

                // Extract each deferred pass under its OWN pass name rather than a bare program
                // number, so a file's name says which screen pass it implements.
                foreach (var kv in resolved.OrderBy(k => k.Key))
                    ExtractPrograms(systemBfshaPath, "system_shading", new[] { kv.Value }, outDir, $"deferred_{kv.Key}");
            }
        }

        /// <summary>
        /// Prints every uniform block in the shading model with its real gsys name, bind index,
        /// declared size, and full field layout (name + byte offset + inferred vec size), and
        /// writes a matching GLSL struct declaration to disk so the test bench can build the
        /// block byte-exactly.
        ///
        /// DataOffset in BFSHA is stored 1-based (0 means "no explicit offset, derive from
        /// Index * 4") - same convention ShaderLabelUtil.GetUniformLabels already relies on.
        /// </summary>
        static void DumpUniformBlocks(ShaderModel sm, string outDir, string modelName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// Uniform block layouts recovered from BFSHA reflection for shading model \"{modelName}\".");
            sb.AppendLine($"// Offsets are byte offsets within the block; vecN index is offset/16, component is (offset%16)/4.");
            sb.AppendLine();

            foreach (var blockEntry in sm.UniformBlocks)
            {
                var block = blockEntry.Value;

                // The dictionary key is the shader-facing name; SymbolData carries the engine-facing
                // symbol (what the C++ side calls it). An empty symbol means the unnamed scene block,
                // which ShaderLabelUtil already treats as "SceneMat".
                string symbol = null;
                if (sm.SymbolData?.UniformBlocks != null && block.Index < sm.SymbolData.UniformBlocks.Count)
                    symbol = sm.SymbolData.UniformBlocks[block.Index].Name1;
                if (string.IsNullOrEmpty(symbol))
                    symbol = "SceneMat";

                // Ryujinx names the fragment constant buffer for block location L as fp_c{L+3}
                // (ShaderExtract.SetReflectionNames). Index here is the bind location.
                string fpName = $"fp_c{block.Index + 3}";

                Console.WriteLine();
                Console.WriteLine($"  -- UniformBlock \"{blockEntry.Key}\" (symbol \"{symbol}\") --");
                Console.WriteLine($"     Index(bind location)={block.Index}  Size={block.Size} bytes  Type={block.Type}  Uniforms={block.Uniforms.Count}");
                Console.WriteLine($"     decompiled name: {fpName}  (i.e. {fpName}.data[N] == byte offset N*16)");

                sb.AppendLine($"// ---- {blockEntry.Key} (symbol {symbol}) : bind location {block.Index}, size {block.Size}, decompiled as {fpName}");

                var uniforms = block.Uniforms
                    .Select(u => new
                    {
                        Name = u.Key,
                        Offset = u.Value.DataOffset == 0 ? (uint)(u.Value.Index * 4) : (uint)(u.Value.DataOffset - 1),
                    })
                    .OrderBy(u => u.Offset)
                    .ToList();

                for (int i = 0; i < uniforms.Count; i++)
                {
                    uint offset = uniforms[i].Offset;
                    // Size is only inferable from the gap to the next field; the last field's size
                    // comes from the block's declared total.
                    uint next = i < uniforms.Count - 1 ? uniforms[i + 1].Offset : block.Size;
                    uint size = next > offset ? next - offset : 4;

                    // The block's own compiled DEFAULT bytes - for a "Const" field (author-set
                    // once, not written per-frame by engine code the way "Dynamic" fields are),
                    // this is very likely the ONLY real value that exists anywhere, since nothing
                    // in the executable looks it up by name the way p_dynamic_* fields do. Printed
                    // as a raw float per 4 bytes of this uniform's span so a vec3/vec4 shows all
                    // its components, not just the first.
                    string defaults = "";
                    if (block.DefaultBuffer != null && offset + size <= block.DefaultBuffer.Length)
                    {
                        var parts = new List<string>();
                        for (uint b = 0; b < size; b += 4)
                            parts.Add(BitConverter.ToSingle(block.DefaultBuffer, (int)(offset + b)).ToString("G6"));
                        defaults = $"  default=({string.Join(", ", parts)})";
                    }
                    else if (block.DefaultBuffer == null)
                    {
                        defaults = "  default=(NO DefaultBuffer on this block)";
                    }

                    Console.WriteLine($"       +0x{offset:X4} ({offset,5})  {fpName}.data[{offset / 16}].{Component(offset)}  size={size,3}  {uniforms[i].Name}{defaults}");
                    sb.AppendLine($"    {GlslTypeFor(size)} {uniforms[i].Name}; // +{offset} -> {fpName}.data[{offset / 16}].{Component(offset)}{defaults}");
                }
                sb.AppendLine();
            }

            string path = Path.Combine(outDir, $"{modelName}_uniform_blocks.glsl");
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine();
            Console.WriteLine($"  -> Wrote block layouts: {path}");
        }

        static string Component(uint byteOffset)
        {
            switch ((byteOffset % 16) / 4)
            {
                case 0: return "x";
                case 1: return "y";
                case 2: return "z";
                default: return "w";
            }
        }

        static string GlslTypeFor(uint size)
        {
            switch (size)
            {
                case 4: return "float";
                case 8: return "vec2";
                case 12: return "vec3";
                case 16: return "vec4";
                case 32: return "mat2x4";
                case 48: return "mat3x4";
                case 64: return "mat4";
                default: return $"float[{Math.Max(1, size / 4)}] /*{size}B*/";
            }
        }

        /// <summary>
        /// Samplers get the same treatment: BFSHA's sampler dict order is the reflection order,
        /// and the decompiler emits fp_t_tcb_{(location*2+8):X}. Printing both directions makes
        /// the "which G-Buffer target goes in which texture unit" question answerable by lookup
        /// instead of by guessing from usage.
        /// </summary>
        static void DumpSamplers(ShaderModel sm)
        {
            Console.WriteLine();
            Console.WriteLine("  -- Samplers (shading-model level) --");
            for (int i = 0; i < sm.Samplers.Count; i++)
            {
                var name = sm.Samplers.Keys.ElementAt(i);
                string symbol = null;
                if (sm.SymbolData?.Samplers != null && i < sm.SymbolData.Samplers.Count)
                    symbol = sm.SymbolData.Samplers[i].Name1;
                Console.WriteLine($"     [{i,2}] {name}{(string.IsNullOrEmpty(symbol) ? "" : $"   (symbol \"{symbol}\")")}");
            }
        }

        /// <summary>
        /// SystemModel.DeferredMain.bfres is the mesh-per-deferred-pass model: each Shape is one
        /// screen pass (field_hybrid, chara_skin, preshading_field, ...). Resolving each shape's
        /// material through the normal option-search path yields the REAL program index for that
        /// pass, which is what the test bench must run - rather than assuming "prog 0" / "prog 72".
        /// </summary>
        public static Dictionary<string, int> DumpDeferredModel(BfshaFile bfsha, string deferredBfresPath)
        {
            var resolved = new Dictionary<string, int>();
            Console.WriteLine();
            Console.WriteLine("################################################################");
            Console.WriteLine($"# Deferred pass model: {deferredBfresPath}");
            Console.WriteLine("################################################################");

            var resFile = new ResFile(deferredBfresPath);
            var model = resFile.Models[0];
            Console.WriteLine($"Model \"{model.Name}\": {model.Shapes.Count} shape(s), {model.Materials.Count} material(s)");

            foreach (var shapeEntry in model.Shapes)
            {
                var shape = shapeEntry.Value;
                var material = model.Materials[shape.MaterialIndex];
                string shadingModel = material.ShaderAssign.ShadingModelName;

                if (!bfsha.ShaderModels.ContainsKey(shadingModel))
                {
                    Console.WriteLine($"  {shape.Name,-32} material={material.Name,-32} shading=\"{shadingModel}\" -> NOT IN THIS ARCHIVE");
                    continue;
                }

                var sb = new StringBuilder();
                sb.Append($"  {shape.Name,-32} material={material.Name,-32} shading=\"{shadingModel}\"");

                foreach (string pipeline in new[] { "gsys_assign_material", "gsys_assign_gbuffer" })
                {
                    try
                    {
                        var result = TestTOTK.GetShaderProgram(bfsha, resFile, shape.Name, pipeline);
                        sb.Append($"  [{pipeline}={result.Item1}]");
                        if (pipeline == "gsys_assign_material" && result.Item1 >= 0)
                            resolved[shape.Name] = result.Item1;
                    }
                    catch (Exception ex)
                    {
                        sb.Append($"  [{pipeline}=ERR:{ex.GetType().Name}]");
                    }
                }
                Console.WriteLine(sb.ToString());
            }
            return resolved;
        }

        /// <summary>
        /// Re-decompiles specific programs of a shading model with reflection names AND the
        /// immediate constant buffer folded in, which is the difference between a shader that
        /// merely reads and one that can actually be compiled and run: the raw decompile leaves
        /// fp_c1.data[N] dangling (there is no such bound block), while the extracted form
        /// substitutes the real baked float literals from the shader's control section.
        /// </summary>
        public static void ExtractPrograms(string systemBfshaPath, string shadingModelName, int[] programIndices, string outDir, string labelPrefix)
        {
            Directory.CreateDirectory(outDir);
            var bfsha = new BfshaFile(systemBfshaPath);

            if (!bfsha.ShaderModels.ContainsKey(shadingModelName))
            {
                Console.WriteLine($"[ExtractPrograms] archive has no shading model \"{shadingModelName}\" (has: {string.Join(", ", bfsha.ShaderModels.Keys)})");
                return;
            }
            var sm = bfsha.ShaderModels[shadingModelName];

            foreach (int progIdx in programIndices)
            {
                Console.WriteLine();
                Console.WriteLine($"=== {labelPrefix} program {progIdx} ===");
                if (progIdx < 0 || progIdx >= sm.Programs.Count)
                {
                    Console.WriteLine($"  [skip] out of range (model has {sm.Programs.Count} programs)");
                    continue;
                }

                var binProg = sm.GetVariation(progIdx).BinaryProgram;

                DumpReflection("Fragment", binProg.FragmentShaderReflection);
                DumpReflection("Vertex", binProg.VertexShaderReflection);

                WriteStage(outDir, $"{labelPrefix}_prog{progIdx}", "frag", binProg.FragmentShader, binProg.FragmentShaderReflection);
                WriteStage(outDir, $"{labelPrefix}_prog{progIdx}", "vert", binProg.VertexShader, binProg.VertexShaderReflection);
            }
        }

        static void DumpReflection(string stage, BnshFile.ShaderReflectionData reflect)
        {
            if (reflect == null)
                return;

            Console.WriteLine($"  [{stage} samplers]");
            foreach (var kvp in reflect.Samplers)
            {
                int loc = reflect.GetSamplerLocation(kvp.Key);
                if (loc == -1)
                    continue;
                // Matches ShaderExtract.SetReflectionNames' fp_t_tcb_{(loc*2)+8:X} convention.
                Console.WriteLine($"    location {loc,3}  fp_t_tcb_{((loc * 2) + 8):X}  {kvp.Key}");
            }

            Console.WriteLine($"  [{stage} uniform buffers]");
            foreach (var kvp in reflect.UniformBuffers)
            {
                int loc = reflect.GetConstantBufferLocation(kvp.Key);
                if (loc == -1)
                    continue;
                Console.WriteLine($"    location {loc,3}  fp_c{loc + 3,-3}  {kvp.Key}");
            }

            Console.WriteLine($"  [{stage} inputs]");
            foreach (var kvp in reflect.Inputs)
            {
                int loc = reflect.GetInputLocation(kvp.Key);
                if (loc == -1)
                    continue;
                Console.WriteLine($"    in_attr{loc,-3} {kvp.Key}");
            }

            Console.WriteLine($"  [{stage} outputs]");
            foreach (var kvp in reflect.Outputs)
            {
                int loc = reflect.GetOutputLocation(kvp.Key);
                if (loc == -1)
                    continue;
                Console.WriteLine($"    out_attr{loc,-3} {kvp.Key}");
            }
        }

        static void WriteStage(string outDir, string label, string ext, BnshFile.ShaderCode code, BnshFile.ShaderReflectionData reflect)
        {
            if (code?.ByteCode == null)
                return;

            try
            {
                string raw = TegraShaderTranslator.Decompile(code.ByteCode);
                File.WriteAllText(Path.Combine(outDir, $"{label}.{ext}"), raw);

                string extracted = ShaderExtract.GetCode(code, reflect);
                File.WriteAllText(Path.Combine(outDir, $"{label}_extracted.{ext}"), extracted);
                Console.WriteLine($"  -> {label}.{ext} ({raw.Length} chars) + {label}_extracted.{ext} ({extracted.Length} chars)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{ext} decompile FAILED] {ex.Message}");
            }
        }

        /// <summary>
        /// Prints each deferred pass material's RAW ShaderAssign options exactly as they sit in
        /// the bfres, with no normalization, plus which of them the compiled archive actually
        /// knows about. This is the ground truth for diagnosing why option-search collapses
        /// distinct passes onto the same program index.
        /// </summary>
        public static void DumpDeferredMaterialOptions(string systemBfshaPath, string deferredBfresPath)
        {
            var bfsha = new BfshaFile(systemBfshaPath);
            var resFile = new ResFile(deferredBfresPath);
            var model = resFile.Models[0];

            Console.WriteLine();
            Console.WriteLine("################################################################");
            Console.WriteLine("# RAW deferred material options (no normalization)");
            Console.WriteLine("################################################################");

            foreach (var matEntry in model.Materials)
            {
                var mat = matEntry.Value;
                var sm = bfsha.ShaderModels.ContainsKey(mat.ShaderAssign.ShadingModelName)
                    ? bfsha.ShaderModels[mat.ShaderAssign.ShadingModelName] : null;

                Console.WriteLine();
                Console.WriteLine($"-- {mat.Name} (shading \"{mat.ShaderAssign.ShadingModelName}\", {mat.ShaderAssign.ShaderOptions.Count} options) --");
                foreach (var op in mat.ShaderAssign.ShaderOptions)
                {
                    if (op.Value == "<Default Value>")
                        continue;

                    string where = "NOT-IN-ARCHIVE";
                    string choices = "";
                    if (sm != null)
                    {
                        ShaderOption? so = sm.StaticOptions.ContainsKey(op.Key) ? sm.StaticOptions[op.Key]
                            : sm.DynamicOptions.ContainsKey(op.Key) ? sm.DynamicOptions[op.Key] : null;
                        if (so != null)
                        {
                            where = sm.StaticOptions.ContainsKey(op.Key) ? "static" : "dynamic";
                            bool valid = so.Choices.GetIndex(op.Value == "True" ? "1" : op.Value == "False" ? "0" : op.Value) != -1;
                            choices = $" default={so.DefaultChoice} choices=[{string.Join(",", so.Choices.Keys)}]{(valid ? "" : "  <<< NOT A VALID CHOICE")}";
                        }
                    }
                    Console.WriteLine($"   {op.Key,-46} = {op.Value,-24} [{where}]{choices}");
                }
            }
        }


        /// <summary>
        /// Ground truth for the shader-variant space, straight out of the archive's own key
        /// table: for every static/dynamic option, which choice each of the N compiled programs
        /// actually uses. Options that hold the same choice in all programs are dead axes and are
        /// summarized rather than listed per-program; the ones that VARY are the real dimensions
        /// of the deferred shader space, and are printed as a program-by-program matrix.
        ///
        /// This is the check that says whether an option-search result is plausible: if 23
        /// distinct deferred passes resolve onto 2 programs, the varying-axis list shows what the
        /// material data would have had to say to reach the other 151.
        /// </summary>
        public static void DumpProgramOptionMatrix(string systemBfshaPath, string shadingModelName, string outDir)
        {
            var bfsha = new BfshaFile(systemBfshaPath);
            if (!bfsha.ShaderModels.ContainsKey(shadingModelName))
            {
                Console.WriteLine($"[DumpProgramOptionMatrix] no shading model \"{shadingModelName}\"");
                return;
            }
            var sm = bfsha.ShaderModels[shadingModelName];
            int keysPerProgram = sm.StaticKeyLength + sm.DynamicKeyLength;

            Console.WriteLine();
            Console.WriteLine("################################################################");
            Console.WriteLine($"# Program/option matrix for \"{shadingModelName}\" ({sm.Programs.Count} programs, {keysPerProgram} key words each)");
            Console.WriteLine("################################################################");

            // choicesPerOption[optionName][programIndex] = chosen key string
            var choicesPerOption = new Dictionary<string, string[]>();

            void Collect(ResDict<ShaderOption> dict, bool isDynamic)
            {
                for (int j = 0; j < dict.Count; j++)
                {
                    var option = dict[j];
                    var perProgram = new string[sm.Programs.Count];
                    int keyIdx = isDynamic ? sm.StaticKeyLength + (option.Bit32Index - option.KeyOffset) : option.Bit32Index;

                    for (int i = 0; i < sm.Programs.Count; i++)
                    {
                        int choiceIndex = option.GetChoiceIndex(sm.KeyTable[keysPerProgram * i + keyIdx]);
                        perProgram[i] = choiceIndex >= 0 && choiceIndex < option.Choices.Count
                            ? option.Choices.GetKey(choiceIndex)
                            : $"<bad {choiceIndex}>";
                    }
                    choicesPerOption[option.Name] = perProgram;
                }
            }
            Collect(sm.StaticOptions, false);
            Collect(sm.DynamicOptions, true);

            var varying = choicesPerOption.Where(kv => kv.Value.Distinct().Count() > 1).ToList();
            var fixedOpts = choicesPerOption.Where(kv => kv.Value.Distinct().Count() == 1).ToList();

            Console.WriteLine($"  {fixedOpts.Count} option(s) are identical across every program (dead axes):");
            foreach (var kv in fixedOpts.OrderBy(k => k.Key))
                Console.WriteLine($"    {kv.Key,-52} = {kv.Value[0]}");

            Console.WriteLine();
            Console.WriteLine($"  {varying.Count} option(s) VARY across programs - these are the real axes:");
            foreach (var kv in varying.OrderBy(k => k.Key))
                Console.WriteLine($"    {kv.Key,-52} values seen: [{string.Join(",", kv.Value.Distinct().OrderBy(v => v))}]");

            // Full matrix to disk: one row per program, one column per option (ALL options, not
            // just the varying ones - the constant ones still have to match when a material's
            // value vector is matched against a program row).
            var all = choicesPerOption.OrderBy(k => k.Key).ToList();
            var sb = new StringBuilder();
            sb.Append("program");
            foreach (var kv in all)
                sb.Append(',').Append(kv.Key);
            sb.AppendLine();
            for (int i = 0; i < sm.Programs.Count; i++)
            {
                sb.Append(i);
                foreach (var kv in all)
                    sb.Append(',').Append(kv.Value[i]);
                sb.AppendLine();
            }
            string path = Path.Combine(outDir, $"{shadingModelName}_program_option_matrix.csv");
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine();
            Console.WriteLine($"  -> Wrote full matrix: {path}");
        }


        /// <summary>
        /// Writes each deferred material's shader-option VALUES in raw slot order, with no
        /// name-based interpretation.
        ///
        /// This exists because the option NAMES that come back for these materials are not
        /// trustworthy - only 2 of 17 non-default names on every DeferredMain material exist in
        /// the archive at all, while the values themselves land exactly inside the archive's own
        /// per-axis value sets (e.g. 100/101/103/104/105 for the chara_* passes, which is
        /// precisely o_material_behave's value set). Names and values are read through completely
        /// different paths in MaterialParserV10.LoadShaderOptions - names via
        /// ShaderAssign.Options' ResDict keys (TotK V10 = 64-bit keys into the shared
        /// ExternalBinaryString table), values via OptionIndices into the toggles+values list -
        /// so the values can be sound while the names are not.
        ///
        /// Emitting the ordered value vector lets the real program index be recovered by matching
        /// against the archive's own key table instead, which is self-validating: a slot-to-option
        /// mapping is only believable if it makes every material match some program.
        /// </summary>
        public static void DumpDeferredMaterialOptionVectors(string deferredBfresPath, string outDir)
        {
            var resFile = new ResFile(deferredBfresPath);
            var model = resFile.Models[0];

            var sb = new StringBuilder();
            sb.AppendLine("material,slot,name_as_parsed,value");
            foreach (var matEntry in model.Materials)
            {
                var mat = matEntry.Value;
                var opts = mat.ShaderAssign.ShaderOptions;
                for (int j = 0; j < opts.Count; j++)
                    sb.AppendLine($"{mat.Name},{j},{opts.GetKey(j)},{opts[j]}");
            }

            string path = Path.Combine(outDir, "deferred_material_option_vectors.csv");
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine();
            Console.WriteLine($"  -> Wrote raw per-material option vectors: {path}");
        }


        /// <summary>
        /// Emits the shading model's options in NATIVE archive order (static block first, then
        /// dynamic, each in ResDict order) with their choice lists and defaults.
        ///
        /// Needed because a material's ShaderAssign.Options ResDict is written in the shader's own
        /// option order, so the material's slot order should be a monotonic subsequence of this
        /// list. That turns "which archive option is slot 31" from a combinatorial search into a
        /// checkable alignment.
        /// </summary>
        public static void DumpOptionOrder(string systemBfshaPath, string shadingModelName, string outDir)
        {
            var bfsha = new BfshaFile(systemBfshaPath);
            if (!bfsha.ShaderModels.ContainsKey(shadingModelName))
                return;
            var sm = bfsha.ShaderModels[shadingModelName];

            var sb = new StringBuilder();
            sb.AppendLine("order,kind,name,default,choices");
            int n = 0;
            void Emit(ResDict<ShaderOption> dict, string kind)
            {
                for (int i = 0; i < dict.Count; i++)
                {
                    var o = dict[i];
                    sb.AppendLine($"{n++},{kind},{o.Name},{o.DefaultChoice},{string.Join("|", o.Choices.Keys)}");
                }
            }
            Emit(sm.StaticOptions, "static");
            Emit(sm.DynamicOptions, "dynamic");

            string path = Path.Combine(outDir, $"{shadingModelName}_option_order.csv");
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"  -> Wrote native option order: {path}");
        }

    }
}