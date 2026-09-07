using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using BfresLibrary;
using EffectLibraryTest;
using ShaderLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Builds the gsys_material uniform block (the shader's "Mat" block, binding 5) for every
    /// material of a model, entirely from romfs data.
    ///
    /// WHY THIS EXISTS: the test bench had been feeding the deferred/G-Buffer shaders
    /// matsrc_*.bin files captured live from the running game, and those captures are known to be
    /// wrong - every one is exactly 416 bytes with only 5 distinct hashes across 255 files, which
    /// cannot be per-material data. Everything downstream (specular response, colour constants,
    /// feature toggles) was therefore driven by garbage.
    ///
    /// It is reconstructible offline because both halves are on disk:
    ///
    ///   * The BFSHA's gsys_material block declares the layout - its size, and a name -> byte
    ///     offset for each of its uniforms - plus a DefaultBuffer holding the values a material
    ///     gets when it does not override them.
    ///   * The BFRES material carries ShaderParamData (a raw blob) and a ShaderParams table
    ///     giving, per parameter, its Name, its Type (hence DataSize) and its DataOffset INTO
    ///     THAT BLOB.
    ///
    /// The two use different offsets - the blob is packed per-material, the block is the shader's
    /// layout - so they must be joined BY NAME, not copied wholesale. That is the whole job:
    /// start from the shader's defaults, then overlay each named parameter the material actually
    /// specifies at the offset the shader expects it.
    /// </summary>
    public static class BuildMaterialUbo
    {
        public static void Run(string bfshaPath, string bfresMcPath, string outDir)
        {
            Console.WriteLine("################################################################");
            Console.WriteLine("# Building gsys_material UBOs from romfs");
            Console.WriteLine($"#   shader:  {bfshaPath}");
            Console.WriteLine($"#   model:   {bfresMcPath}");
            Console.WriteLine("################################################################");

            var bfsha = new BfshaFile(bfshaPath);
            // The DeferredMain system model ships as a plain .bfres; only the game's own models
            // are MCPK-compressed .bfres.mc.
            byte[] fres = bfresMcPath.EndsWith(".mc", StringComparison.OrdinalIgnoreCase)
                ? TestMaterialDump.DecompressBfresMc(bfresMcPath)
                : File.ReadAllBytes(bfresMcPath);
            using var ms = new MemoryStream(fres);
            var resFile = new ResFile(ms, false);
            BuildForModel(bfsha, resFile.Models[0], outDir);
        }

        /// <summary>
        /// Builds the gsys_material block for every DEFERRED-RESOLVE pass, from the shared
        /// system shader archive and the shared <c>SystemModel.DeferredMain</c> model - the "Mat"
        /// block <see cref="Marrow.Core.Pipeline.DeferredResolvePass"/> binds when re-shading a
        /// G-buffer surface by its <c>o_material_behave</c> bucket (chara_skin, chara_hair, ...).
        ///
        /// THIS STEP WAS MISSING FROM <c>ModelPreparer.Prepare</c> ENTIRELY: nothing in the normal
        /// app pipeline ever wrote a model's `matubo_deferred/` directory, so
        /// <c>DeferredResolvePass.ResolveDeferredPasses</c>'s <c>File.Exists(matPath)</c> check
        /// always failed and every deferred-resolve pass ran against an ALL-ZERO material block -
        /// silently, since an all-zero block still uploads and links fine, it just makes every
        /// deferred-only parameter (a resolve pass's own rim/tint/miasma/etc. constants - anything
        /// NOT read directly by the object's own G-buffer program) behave as if it were zero
        /// regardless of what the object's own per-material params.json says, no matter how
        /// correct those are. A `--deferred-material-ubo` CLI flag already built this correctly
        /// once, by hand, against a manually pre-decompressed copy of these two archives sitting
        /// outside this repo (`TestBench/data/`) - this is that same logic, generalized to
        /// decompress straight from a real romfs so it does not depend on that copy existing.
        /// </summary>
        public static void RunSystemDeferred(string romfsRoot, string outDir)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;

            string bfshaPath = Path.Combine(romfsRoot, "Shader", "system.Product.110.product.Nin_NX_NVN.bfsha");
            byte[] bfshaBytes = LoadPossiblyCompressed(bfshaPath);
            var bfsha = new BfshaFile(new MemoryStream(bfshaBytes));

            string modelPath = Path.Combine(romfsRoot, "Model", "SystemModel.DeferredMain.bfres.mc");
            if (!File.Exists(modelPath))
                modelPath = Path.Combine(romfsRoot, "Model", "SystemModel.DeferredMain.bfres");
            byte[] fres = modelPath.EndsWith(".mc", StringComparison.OrdinalIgnoreCase)
                ? TestMaterialDump.DecompressBfresMc(modelPath)
                : File.ReadAllBytes(modelPath);
            using var ms = new MemoryStream(fres);
            var resFile = new ResFile(ms, false);
            BuildForModel(bfsha, resFile.Models[0], outDir);
        }

        /// <summary>
        /// Dumps the REAL authored values behind <c>gsys_scene_material</c>'s "Const"-prefixed
        /// fields (the Blueprint/ghost-effect family among them) - traced via Ghidra to
        /// <c>gsys::ModelScene::initialize_</c>, which loads a real, separate model
        /// (<c>Model/SystemModel.SceneMaterial.bfres.mc</c>, found by searching romfs for the
        /// resource name "SceneMaterial" the scene looks up) via <c>gsys::ModelNW::initialize</c>
        /// and passes THAT loaded model into every render context's
        /// <c>gsys::ModelRenderContext::setSceneMaterial</c>. So unlike Context/Env ("Dynamic"
        /// scene state written by engine code every frame, by field name - see
        /// <c>setDynamicShadowParams</c>), SceneMat's "Const" fields are just an ordinary
        /// MATERIAL's authored <c>ShaderParams</c> on this one dedicated model - the exact same
        /// name-join mechanism <see cref="BuildBlock"/> already does for <c>gsys_material</c>,
        /// just pointed at the <c>gsys_scene_material</c> block instead. No further Ghidra
        /// archaeology needed once this model is located.
        /// </summary>
        public static void DumpSceneMaterial(string romfsRoot)
        {
            // The SceneMaterial model's own material compiles under a DIFFERENT shading model
            // ("system_scene_material") than object materials do ("material") - live in the
            // system archive, not the one every G-buffer/forward program uses. The BYTE LAYOUT is
            // what matters for applying these values elsewhere (confirmed identical offsets/names
            // to the "material" shading model's own gsys_scene_material block), not which archive
            // happens to declare it - this is one shared, engine-wide UBO either way.
            string materialBfshaPath = Path.Combine(romfsRoot, "Shader", "material.Product.110.product.Nin_NX_NVN.bfsha");
            string systemBfshaPath = Path.Combine(romfsRoot, "Shader", "system.Product.110.product.Nin_NX_NVN.bfsha");
            var materialBfsha = new BfshaFile(new MemoryStream(LoadPossiblyCompressed(materialBfshaPath)));
            var systemBfsha = new BfshaFile(new MemoryStream(LoadPossiblyCompressed(systemBfshaPath)));

            string modelPath = Path.Combine(romfsRoot, "Model", "SystemModel.SceneMaterial.bfres.mc");
            byte[] fres = TestMaterialDump.DecompressBfresMc(modelPath);
            using var ms = new MemoryStream(fres);
            var resFile = new ResFile(ms, false);

            foreach (var matEntry in resFile.Models[0].Materials)
            {
                Material mat = matEntry.Value;
                string shading = mat.ShaderAssign.ShadingModelName;
                Console.WriteLine($"-- material \"{mat.Name}\" (shading model \"{shading}\") --");
                if (!materialBfsha.ShaderModels.TryGetValue(shading, out var sm) &&
                    !systemBfsha.ShaderModels.TryGetValue(shading, out sm))
                {
                    Console.WriteLine($"   [skip] shading model \"{shading}\" not in material.bfsha or system.bfsha");
                    continue;
                }
                // MasterMaterial's own shading model ("system_scene_material") doesn't CONSUME a
                // gsys_scene_material block itself - it declares an ordinary gsys_material block
                // (its own self-material data), which turns out to BE the SceneMat byte layout
                // (confirmed: identical field names/offsets to the "material" shading model's own
                // gsys_scene_material block) - this material's job is to SUPPLY that data to every
                // OTHER shading model's gsys_scene_material, not read one itself.
                if (!sm.UniformBlocks.TryGetValue("gsys_material", out var block))
                {
                    Console.WriteLine($"   [skip] this shading model has no gsys_material block either. Has: {string.Join(", ", sm.UniformBlocks.Keys)}");
                    continue;
                }

                byte[] buffer = BuildBlock(block, mat, out int matched, out int missing, out var missingNames);
                Console.WriteLine($"   matched {matched}, unmatched {missing}" +
                                  (missing > 0 ? $" ({string.Join(", ", missingNames)})" : ""));

                var ordered = block.Uniforms.Keys.OrderBy(n => GetUniformOffset(block.Uniforms[n])).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    string name = ordered[i];
                    int off = GetUniformOffset(block.Uniforms[name]);
                    int next = i < ordered.Count - 1 ? GetUniformOffset(block.Uniforms[ordered[i + 1]]) : block.Size;
                    int span = Math.Max(4, Math.Min(next - off, 16)); // cap at 16 - a mat3x4/mat4 isn't a plain float vector, print just its first row's worth
                    bool authored = mat.ShaderParams.ContainsKey(name);
                    var floats = new List<string>();
                    for (int b = 0; b + 4 <= span && off + b + 4 <= buffer.Length; b += 4)
                        floats.Add(BitConverter.ToSingle(buffer, off + b).ToString("G6"));
                    Console.WriteLine($"   +{off,4}  {name,-50} = ({string.Join(", ", floats)}){(authored ? "  [AUTHORED by this material]" : "")}");
                }
            }
        }

        /// <summary>Reads a romfs file that may exist either plain or as its usual zstd-compressed <c>.zs</c> sibling (or both - the plain form, if present, is authoritative and is tried first since it needs no dictionary lookup).</summary>
        static byte[] LoadPossiblyCompressed(string plainPath)
        {
            if (File.Exists(plainPath))
                return File.ReadAllBytes(plainPath);
            string zsPath = plainPath + ".zs";
            byte[] raw = File.ReadAllBytes(zsPath);
            return TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
        }

        static void BuildForModel(BfshaFile bfsha, Model model, string outDir)
        {
            Directory.CreateDirectory(outDir);
            foreach (var matEntry in model.Materials)
            {
                Material mat = matEntry.Value;
                string shading = mat.ShaderAssign.ShadingModelName;
                if (!bfsha.ShaderModels.ContainsKey(shading))
                {
                    Console.WriteLine($"[skip] {mat.Name}: shading model \"{shading}\" not in this archive");
                    continue;
                }

                var sm = bfsha.ShaderModels[shading];
                if (!sm.UniformBlocks.ContainsKey("gsys_material"))
                {
                    Console.WriteLine($"[skip] {mat.Name}: shading model has no gsys_material block");
                    continue;
                }

                var block = sm.UniformBlocks["gsys_material"];
                byte[] buffer = BuildBlock(block, mat, out int matched, out int missing, out var missingNames);

                string safe = mat.Name.Replace(":", "_").Replace("/", "_");
                string path = Path.Combine(outDir, $"{safe}.gsys_material.bin");
                File.WriteAllBytes(path, buffer);
                WriteParamLayout(block, mat, Path.Combine(outDir, $"{safe}.params.json"));

                Console.WriteLine();
                Console.WriteLine($"-- {mat.Name} --");
                Console.WriteLine($"   block size {block.Size} B, {block.Uniforms.Count} declared uniforms, " +
                                  $"defaults {(block.DefaultBuffer == null ? "ABSENT" : block.DefaultBuffer.Length + " B")}");
                Console.WriteLine($"   material has {mat.ShaderParams.Count} ShaderParams, " +
                                  $"ShaderParamData {mat.ShaderParamData?.Length ?? 0} B");
                Console.WriteLine($"   -> matched {matched}, unmatched {missing}" +
                                  (missing > 0 ? $" ({string.Join(", ", missingNames.Take(8))}{(missing > 8 ? ", ..." : "")})" : ""));
                Console.WriteLine($"   -> {path}");
            }
        }

        /// <summary>
        /// Writes the sidecar every SHADER PARAMETER ANIMATION needs: which byte of the
        /// <c>gsys_material</c> block each named parameter lives at.
        ///
        /// A material anim addresses its target as (parameter NAME, byte offset WITHIN that
        /// parameter) - <c>ParamAnimInfo.Name</c> plus <c>AnimCurve.AnimDataOffset</c>, verified
        /// against real data: Enemy_Dragon_Darkness's <c>Face_Eye_Scroll_fts</c> drives
        /// <c>p_tex_srt1</c> at <c>animOffset 20</c>, which is byte 20 of a 24-byte TexSrt
        /// (mode:int, scaleX, scaleY, rotation, translateX, translateY) - the translate Y that
        /// scrolls the eye - with a constant at animOffset 0 setting the mode. So the anim never
        /// needs the material's own packed <c>ShaderParamData</c> layout at all; it needs THIS
        /// table, which is the only thing that knows where <c>p_tex_srt1</c> sits in the compiled
        /// block. Emitting the block's whole uniform table (not just the parameters this material
        /// happens to set) means an anim can drive a uniform the material left at its default.
        ///
        /// Also carries, for each uniform, the BFRES <c>ShaderParamType</c> the MATERIAL itself
        /// declares for that name (e.g. "Float4", "Int", "TexSrt") when the material actually
        /// authors a value for it - omitted (no "type" field) when the uniform sits at the
        /// shader's own default and no material ShaderParam names it, since then there is no
        /// authored type to report. This is what lets a live editor pick a sane widget (a colour
        /// picker for a Float4 named like a colour, a plain slider for a scalar, ...) without
        /// guessing from the byte layout alone - the compiled block itself carries no component
        /// count on this platform (BfshaUniform's GX2Type/GX2Count fields are Wii U only and are
        /// zero here), so the material's own authored type is the only real source for it.
        ///
        /// <paramref name="usedNames"/> is filled in separately, later, by
        /// <c>ExportManifest</c> (which is the step that actually has the material's real
        /// decompiled shader text to check against - this method runs too early in the pipeline to
        /// know it) - see that class's <c>FindUsedParamNames</c>. Passing it re-writes this same
        /// file with a <c>"used"</c> field added per typed uniform; omitting it (the first, earlier
        /// call from <see cref="Run"/>/<see cref="BuildForModel"/>) leaves every entry without one.
        /// </summary>
        public static void WriteParamLayout(BfshaUniformBlock block, Material mat, string outPath, IReadOnlySet<string>? usedNames = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"material\": \"{mat.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\",");
            sb.AppendLine($"  \"block_size\": {block.Size},");
            sb.AppendLine("  \"uniforms\": [");
            var names = block.Uniforms.Keys.ToList();
            for (int i = 0; i < names.Count; i++)
            {
                var u = block.Uniforms[names[i]];
                int off = GetUniformOffset(u);
                string typeField = mat.ShaderParams.TryGetValue(names[i], out ShaderParam? p)
                    ? $", \"type\": \"{p.Type}\"" : "";
                string usedField = typeField.Length > 0 && usedNames != null
                    ? $", \"used\": {(usedNames.Contains(names[i]) ? "true" : "false")}" : "";
                sb.Append($"    {{ \"name\": \"{names[i]}\", \"offset\": {off}{typeField}{usedField} }}");
                sb.AppendLine(i == names.Count - 1 ? "" : ",");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(outPath, sb.ToString());
        }

        /// <summary>BFSHA stores <c>DataOffset</c> 1-based; 0 means "derive from Index * 4". Same convention <c>TestSystemShading.DumpUniformBlocks</c>/<c>ShaderLabelUtil.GetUniformLabels</c> use.</summary>
        public static int GetUniformOffset(BfshaUniform u) => u.DataOffset == 0 ? u.Index * 4 : u.DataOffset - 1;

        /// <summary>
        /// Start from the shader's DefaultBuffer, then overlay every material ShaderParam whose
        /// name the block declares. Parameters the block does not declare are skipped and
        /// reported rather than written somewhere arbitrary - a material can legitimately carry
        /// author-side parameters the compiled variant does not consume.
        ///
        /// TexSrt/TexSrtEx are the one type NOT copied byte-for-byte: the material's raw
        /// ShaderParamData for these is the AUTHORED (mode, scaleX, scaleY, rotation, translateX,
        /// translateY) form, but the compiled shader's block expects the BAKED form the real game
        /// computes at load time - a 2x2 rotate-scale matrix plus translation, read directly as
        /// `[u',v'] = [u,v] * [[M0.x,M0.y],[M0.z,M0.w]] + [M1.x,M1.y]` (confirmed against every real
        /// decompiled vertex shader that reads a TexSrt-typed Mat slot, e.g.
        /// material_prog11146_extracted.vert's UV0 transform). Raw-copying the authored form was
        /// silently feeding (mode, scaleX, scaleY, rotation) into a matrix's (a,b,c,d) slots -
        /// mode (0 or 1) and rotation (a small radian value) standing in for what should be
        /// cos/sin*scale terms - which for mode 0 and near-1 scales collapses to an almost-exact
        /// U/V swap (u' ~= v*scaleY + tx, v' ~= u*scaleX + rotation*v + ty). See BakeTexSrt's own
        /// remarks for the real baking formula, reverse engineered via Ghidra
        /// (nn::g3d2::MaterialObj's per-kind TexSrt callback table).
        /// </summary>
        static byte[] BuildBlock(BfshaUniformBlock block, Material mat,
                                 out int matched, out int missing, out List<string> missingNames)
        {
            int size = block.Size;
            byte[] buffer = new byte[size];
            if (block.DefaultBuffer != null && block.DefaultBuffer.Length > 0)
                Array.Copy(block.DefaultBuffer, buffer, Math.Min(block.DefaultBuffer.Length, size));

            var blockOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var u in block.Uniforms)
                blockOffsets[u.Key] = GetUniformOffset(u.Value);

            matched = 0;
            missing = 0;
            missingNames = new List<string>();
            byte[] src = mat.ShaderParamData ?? Array.Empty<byte>();

            foreach (var pEntry in mat.ShaderParams)
            {
                ShaderParam p = pEntry.Value;
                string name = pEntry.Key;

                if (!blockOffsets.TryGetValue(name, out int dstOff))
                {
                    missing++;
                    missingNames.Add(name);
                    continue;
                }

                int len = (int)p.DataSize;
                int srcOff = p.DataOffset;
                if (srcOff < 0 || srcOff + len > src.Length || dstOff < 0 || dstOff + len > size)
                {
                    // Out of range either side - report rather than write a partial value.
                    missing++;
                    missingNames.Add($"{name}(range)");
                    continue;
                }

                if (p.Type is ShaderParamType.TexSrt or ShaderParamType.TexSrtEx)
                {
                    if (Environment.GetEnvironmentVariable("MC_DEBUG_TEXSRT") == "1")
                    {
                        int mode = BitConverter.ToInt32(src, srcOff + 0);
                        float sx = BitConverter.ToSingle(src, srcOff + 4);
                        float sy = BitConverter.ToSingle(src, srcOff + 8);
                        float rot = BitConverter.ToSingle(src, srcOff + 12);
                        float tx = BitConverter.ToSingle(src, srcOff + 16);
                        float ty = BitConverter.ToSingle(src, srcOff + 20);
                        Console.WriteLine($"[TexSrt] {mat.Name}.{name}: mode={mode} sx={sx} sy={sy} rot={rot} tx={tx} ty={ty}");
                    }
                    byte[] baked = TexSrtBake.Bake(src, srcOff);
                    Array.Copy(baked, 0, buffer, dstOff, Math.Min(baked.Length, len));
                }
                else
                {
                    Array.Copy(src, srcOff, buffer, dstOff, len);
                }
                matched++;
            }

            return buffer;
        }
    }

    /// <summary>
    /// Bakes a material's authored TexSrt (mode, scaleX, scaleY, rotation, translateX,
    /// translateY) into the 2x2 rotate-scale matrix + translation the compiled shader's
    /// gsys_material block actually stores - reverse engineered via Ghidra from
    /// nn::g3d2::MaterialObj::ConvertDirtyParams's per-kind callback table (kind &gt;= 0x1c gets a
    /// callback instead of a raw copy). The dispatcher (0x7100072448) reads the mode field and
    /// tail-jumps to a mode-specific baker; only modes 0 ("Maya"-style, pivot at UV centre,
    /// 0x7100072860) and 1 ("3dsMax"-style, same rotate-scale block, different translate/pivot
    /// term, 0x7100072950) were confirmed against real decompiled code. No real TotK material
    /// observed so far uses a mode other than 0 or 1; anything else falls back to the raw values
    /// (matching the old behaviour) rather than guessing a formula for an unconfirmed mode.
    /// </summary>
    public static class TexSrtBake
    {
        public static byte[] Bake(byte[] src, int srcOff)
        {
            int mode = BitConverter.ToInt32(src, srcOff + 0);
            float sx = BitConverter.ToSingle(src, srcOff + 4);
            float sy = BitConverter.ToSingle(src, srcOff + 8);
            float rot = BitConverter.ToSingle(src, srcOff + 12);
            float tx = BitConverter.ToSingle(src, srcOff + 16);
            float ty = BitConverter.ToSingle(src, srcOff + 20);

            float cos = MathF.Cos(rot);
            float sin = MathF.Sin(rot);

            float m0x = sx * cos, m0y = -sy * sin, m0z = sx * sin, m0w = sy * cos;
            float m1x, m1y;

            switch (mode)
            {
                case 0:
                {
                    // 0x7100072860 - Maya-style, pivot at UV centre (0.5, 0.5).
                    float sinHalf = fma(sin, 0.5f, -0.5f);
                    m1x = sx * ((cos * -0.5f - sinHalf) - tx);
                    m1y = sy * (fma(cos, -0.5f, sinHalf) + ty) + 1.0f;
                    break;
                }
                case 1:
                {
                    // 0x7100072950 - 3dsMax-style, pivot at UV centre, different translate handedness.
                    m1x = sx * sin * (ty - 0.5f) - sx * cos * (tx + 0.5f) + 0.5f;
                    m1y = sy * sin * (tx + 0.5f) + sy * cos * (ty - 0.5f) + 0.5f;
                    break;
                }
                default:
                    // Unconfirmed mode - preserve the old (wrong, but no worse) raw pass-through
                    // rather than apply a formula that was never verified against real content.
                    return src.Length >= srcOff + 24 ? src[srcOff..(srcOff + 24)] : new byte[24];
            }

            byte[] baked = new byte[32];
            BitConverter.GetBytes(m0x).CopyTo(baked, 0);
            BitConverter.GetBytes(m0y).CopyTo(baked, 4);
            BitConverter.GetBytes(m0z).CopyTo(baked, 8);
            BitConverter.GetBytes(m0w).CopyTo(baked, 12);
            BitConverter.GetBytes(m1x).CopyTo(baked, 16);
            BitConverter.GetBytes(m1y).CopyTo(baked, 20);
            return baked;

            static float fma(float a, float b, float c) => a * b + c;
        }
    }
}
