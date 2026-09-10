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
        public static void Run(string sharcbPath, string outDir, params string[] programFilter) =>
            Run(new SharcfbFile(sharcbPath), sharcbPath, outDir, programFilter);

        /// <summary>
        /// Extracts <c>hdr_compose</c> (the final HDR->displayable tonemap every model needs
        /// regardless of which one is loaded) straight from real romfs: unpacks
        /// <c>Shader/ApplicationPackage.Nin_NX_NVN.release.sarc.zs</c> (zstd, same convention as
        /// every other <c>.pack.zs</c>/<c>.sarc.zs</c> in this game - see <c>ActorInfo.Resolve</c>
        /// for the identical unpack pattern) and reads its <c>AglShader.sharcb</c> entry directly
        /// from memory - no temp file, no pre-decompressed copy required. This is what makes
        /// <c>agl_hdr_compose.vert/frag</c> reproducible on a fresh checkout instead of needing to
        /// ship decompiled game shader output in the repo.
        ///
        /// <c>hdr_compose</c> has a real static macro, <c>ENABLE_COLOR_CORRECTION_TABLE[0|1]</c>,
        /// so there is no bare "default" variation - the real archive only ever yields
        /// <c>_ENABLE_COLOR_CORRECTION_TABLE0</c>/<c>1</c> variants, never the unsuffixed
        /// <c>agl_hdr_compose</c> name <c>DeferredPipeline</c> actually loads. Confirmed via each
        /// variant's own real sampler list (logged during extraction): TABLE1 additionally samples
        /// <c>cColorCorrection</c>, a LUT texture nothing in this pipeline binds anywhere - TABLE0
        /// only needs <c>cBloom</c>/<c>cColor</c>, both of which <c>BloomPass</c>/<c>TonemapPass</c>
        /// already provide. TABLE0 is therefore the only one Marrow can actually run correctly, so
        /// it's copied to the bare name after extraction rather than left as a choice.
        /// </summary>
        public static void ExtractHdrCompose(string romfsRoot, string outDir)
        {
            string sarcPath = Path.Combine(romfsRoot, "Shader", "ApplicationPackage.Nin_NX_NVN.release.sarc.zs");
            byte[] raw = File.ReadAllBytes(sarcPath);
            byte[] decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var sarc = SarcLibrary.Sarc.FromBinary(new ArraySegment<byte>(decompressed));
            var entryNames = sarc.Select(kv => kv.Key).ToList();
            string? sharcbKey = entryNames.FirstOrDefault(k => k == "AglShader.sharcb");
            if (sharcbKey is null)
                throw new FileNotFoundException($"'{sarcPath}' has no \"AglShader.sharcb\" entry (has: {string.Join(", ", entryNames)}) - archive layout changed?");

            var sharc = new SharcfbFile(new MemoryStream(sarc[sharcbKey].ToArray()));
            Run(sharc, "AglShader.sharcb (from ApplicationPackage.Nin_NX_NVN.release.sarc.zs)", outDir, "hdr_compose");

            foreach (string ext in new[] { "vert", "frag" })
            {
                string variant = Path.Combine(outDir, $"agl_hdr_compose_ENABLE_COLOR_CORRECTION_TABLE0.{ext}");
                string canonical = Path.Combine(outDir, $"agl_hdr_compose.{ext}");
                if (File.Exists(variant))
                    File.Copy(variant, canonical, overwrite: true);
            }
        }

        /// <summary>
        /// Extracts the real cloud billboard shader straight from romfs: unpacks
        /// <c>Lib/agl/agl_resource.Nin_NX_NVN.release.sarc.zs</c> (zstd, a completely different
        /// romfs location than every other agl archive - found by searching a personal SDK/exe dump
        /// for the "BAHS" sharcb magic after Ghidra's own string search came up empty) and reads its
        /// <c>agl_technique.sharcb</c> entry, decompiling ONE specific macro combination of the real
        /// <c>cloud</c> program (not all 32 - that's a ~90 second decompile for combinations nothing
        /// uses).
        ///
        /// The combination chosen - <c>TYPE_USE_TEX_BLEND1, TYPE_USE_DEBUG_SUN_DISP0,
        /// TYPE_USE_PROC_TEXTURE0, TYPE_USE_SCATTER1, TYPE_USE_NLD_SOFTPTCL0</c> - is a real,
        /// reasoned choice, not a guess: TEX_BLEND1 is the only combination that actually declares
        /// both `cBaseTexture_Blend`/`cNoiseTexture_Blend` (the outdoor sky clearly cross-fades
        /// between two cloud texture sets, e.g. for weather/season - a `TEX_BLEND0` shader can't do
        /// that at all). DEBUG_SUN_DISP is literally named "debug" - off in any shipping render.
        /// PROC_TEXTURE0 samples a pre-baked texture rather than generating noise inline (PROC_TEXTURE1
        /// variants are ~14x larger and duplicate work the separate `noise_cloud` program in
        /// `agl_technique_proc.sharcb` already exists to do once, offline - consistent with how every
        /// other baked-vs-sampled pair in this engine works, e.g. the Bruneton sky LUT). SCATTER1
        /// keeps the cloud's own atmospheric-scatter tint, matching the "clouds should pick up
        /// sunset colour" goal. NLD_SOFTPTCL (soft-particle depth blending against nearby opaque
        /// geometry) is a near-camera collision softening trick more relevant to local weather
        /// effects than a distant sky dome, so 0. If this turns out not to be the exact combination
        /// TotK selects for the overworld sky at runtime, the fix is changing this one dictionary -
        /// the extraction and decompile machinery is unaffected either way.
        /// </summary>
        public static void ExtractCloudShader(string romfsRoot, string outDir)
        {
            string sarcPath = Path.Combine(romfsRoot, "Lib", "agl", "agl_resource.Nin_NX_NVN.release.sarc.zs");
            byte[] raw = File.ReadAllBytes(sarcPath);
            byte[] decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var sarc = SarcLibrary.Sarc.FromBinary(new ArraySegment<byte>(decompressed));
            var entryNames = sarc.Select(kv => kv.Key).ToList();
            string? sharcbKey = entryNames.FirstOrDefault(k => k == "agl_technique.sharcb");
            if (sharcbKey is null)
                throw new FileNotFoundException($"'{sarcPath}' has no \"agl_technique.sharcb\" entry (has: {string.Join(", ", entryNames)}) - archive layout changed?");

            var sharc = new SharcfbFile(new MemoryStream(sarc[sharcbKey].ToArray()));
            var program = sharc.Programs.FirstOrDefault(p => p.Name == "cloud")
                ?? throw new InvalidOperationException("agl_technique.sharcb has no \"cloud\" program - archive layout changed?");

            var combo = new Dictionary<string, string>
            {
                ["TYPE_USE_TEX_BLEND"] = "1",
                ["TYPE_USE_DEBUG_SUN_DISP"] = "0",
                ["TYPE_USE_PROC_TEXTURE"] = "0",
                ["TYPE_USE_SCATTER"] = "1",
                ["TYPE_USE_NLD_SOFTPTCL"] = "0",
            };
            int idx = program.GetBinaryIndex(program.GetVariationIndex(combo));

            Directory.CreateDirectory(outDir);
            for (int stage = 0; stage < 2; stage++)
            {
                int vi = idx + stage;
                if (vi < 0 || vi >= sharc.Variations.Count)
                    continue;
                var v = sharc.Variations[vi];
                if (v.ByteCode == null || v.ByteCode.Length == 0)
                    continue;

                string glsl = TegraShaderTranslator.Decompile(v.ByteCode);
                if (v.ControlShader != null && v.ControlShader.Length > 0)
                {
                    var control = new ControlShader(v.ControlShader);
                    float[] consts = control.GetConstantsAsFloats(v.ByteCode);
                    if (consts.Length > 0)
                        glsl = ApplyConstants(glsl, consts);
                }

                string kind = v.Type.ToString();
                string ext = kind.ToLowerInvariant().Contains("pixel") || kind.ToLowerInvariant().Contains("frag") ? "frag" : "vert";
                File.WriteAllText(Path.Combine(outDir, $"agl_cloud.{ext}"), glsl);
            }
        }

        /// <summary>
        /// Extracts the real procedural noise generator that bakes the "cloud_noise" texture
        /// <see cref="ExtractCloudShader"/>'s <c>cNoiseTexture</c>/<c>cNoiseTexture_Blend</c> samplers
        /// actually read at runtime (traced via Ghidra: both real <c>CloudParam0</c>/<c>CloudParam1</c>
        /// texture-slot indices for the noise channel resolve, through a generic runtime name->texture
        /// dispatcher, to a dynamically-baked resource literally named <c>"cloud_noise"</c> - not a
        /// romfs file). This is the shading model that bakes it: <c>noise_cloud</c>, in the SAME
        /// <c>Lib/agl/agl_resource.Nin_NX_NVN.release.sarc.zs</c> -&gt; <c>agl_technique_proc.sharcb</c>
        /// archive as the sky LUT precompute chain.
        ///
        /// Macro choice - <c>RENDER_TYPE0, IS_RENDER_CUBEMAP0</c> - is NOT confirmed the way the
        /// `cloud` program's own combo is: cubemap is off because a 2D dome samples a flat texture,
        /// not 6 cube faces, but which of the 3 real `RENDER_TYPE` values the game actually bakes
        /// with wasn't traced (no further Ghidra evidence found before this was deprioritized in
        /// favour of the parts of the real cloud system that ARE confirmed - see <see cref="ExtractCloudShader"/>
        /// and <c>Marrow.Core.Rendering.CloudPostFxLayer</c>'s own remarks). Decompiled here regardless,
        /// same as <c>ExtractCloudShader</c>, so it's on disk and ready once that gets resolved.
        /// </summary>
        public static void ExtractCloudNoiseShader(string romfsRoot, string outDir)
        {
            string sarcPath = Path.Combine(romfsRoot, "Lib", "agl", "agl_resource.Nin_NX_NVN.release.sarc.zs");
            byte[] raw = File.ReadAllBytes(sarcPath);
            byte[] decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var sarc = SarcLibrary.Sarc.FromBinary(new ArraySegment<byte>(decompressed));
            var entryNames = sarc.Select(kv => kv.Key).ToList();
            string? sharcbKey = entryNames.FirstOrDefault(k => k == "agl_technique_proc.sharcb");
            if (sharcbKey is null)
                throw new FileNotFoundException($"'{sarcPath}' has no \"agl_technique_proc.sharcb\" entry (has: {string.Join(", ", entryNames)}) - archive layout changed?");

            var sharc = new SharcfbFile(new MemoryStream(sarc[sharcbKey].ToArray()));
            var program = sharc.Programs.FirstOrDefault(p => p.Name == "noise_cloud")
                ?? throw new InvalidOperationException("agl_technique_proc.sharcb has no \"noise_cloud\" program - archive layout changed?");

            // ALL three RENDER_TYPE values, not just one. The real cloud draw binds two DIFFERENT
            // noise textures (cNoiseTexture and cNoiseTexture_Blend are distinct resources in a
            // capture), and this program's own RENDER_TYPE[0|1|2] is the obvious way one generator
            // produces several - so which variant is which cannot be settled by picking one up
            // front. Extracting all of them is cheap and lets the question be answered by baking
            // each and diffing against the captured textures.
            Directory.CreateDirectory(outDir);
            var typeMacro = program.VariationMacros.FirstOrDefault(m => m.Name == "RENDER_TYPE");
            var renderTypes = typeMacro?.Values ?? new List<string> { "0" };

            foreach (string renderType in renderTypes)
            {
                var combo = new Dictionary<string, string>
                {
                    ["RENDER_TYPE"] = renderType,
                    ["IS_RENDER_CUBEMAP"] = "0",
                };
                int idx = program.GetBinaryIndex(program.GetVariationIndex(combo));

                for (int stage = 0; stage < 2; stage++)
                {
                    int vi = idx + stage;
                    if (vi < 0 || vi >= sharc.Variations.Count)
                        continue;
                    var v = sharc.Variations[vi];
                    if (v.ByteCode == null || v.ByteCode.Length == 0)
                        continue;

                    string glsl = TegraShaderTranslator.Decompile(v.ByteCode);
                    if (v.ControlShader != null && v.ControlShader.Length > 0)
                    {
                        var control = new ControlShader(v.ControlShader);
                        float[] consts = control.GetConstantsAsFloats(v.ByteCode);
                        if (consts.Length > 0)
                            glsl = ApplyConstants(glsl, consts);
                    }

                    string kind = v.Type.ToString();
                    string ext = kind.ToLowerInvariant().Contains("pixel") || kind.ToLowerInvariant().Contains("frag") ? "frag" : "vert";
                    File.WriteAllText(Path.Combine(outDir, $"agl_noise_cloud_type{renderType}.{ext}"), glsl);
                    // Keep the historical unsuffixed name pointing at RENDER_TYPE=0 so anything
                    // already loading "agl_noise_cloud" keeps working.
                    if (renderType == "0")
                        File.WriteAllText(Path.Combine(outDir, $"agl_noise_cloud.{ext}"), glsl);
                }
            }
        }

        /// <summary>
        /// Decompiles ONE macro combination of one program to <c>&lt;baseName&gt;.vert</c>/<c>.frag</c>.
        /// </summary>
        /// <remarks>
        /// The <c>GetVariationIndex</c> -&gt; <c>GetBinaryIndex</c> -&gt; two-stage loop -&gt;
        /// <c>ApplyConstants</c> sequence is identical in every extractor here; the immediate
        /// constant buffer fold is not optional (agl bakes literals into the ISA, and leaving
        /// <c>fp_c1</c> dangling yields GLSL referencing a block nothing binds).
        ///
        /// Throws if the combination names a macro the program does not declare - deliberately,
        /// because <c>SharcUtils.GetVariationIndex</c> is tolerant of unknown keys and would
        /// otherwise silently hand back variation 0. A typo'd macro name producing a
        /// clean-compiling shader that is quietly the WRONG variant is the exact failure this
        /// whole evidence-first approach exists to prevent.
        /// </remarks>
        static void DecompileVariant(SharcfbFile sharc, SharcfbFile.ShaderProgram p,
            Dictionary<string, string> combo, string outDir, string baseName)
        {
            foreach (var kv in combo)
            {
                var macro = p.VariationMacros.FirstOrDefault(m => m.Name == kv.Key)
                    ?? throw new InvalidOperationException(
                        $"program \"{p.Name}\" declares no macro \"{kv.Key}\" (has: {string.Join(", ", p.VariationMacros.Select(m => m.Name))}) - archive layout changed?");
                if (!macro.Values.Contains(kv.Value))
                    throw new InvalidOperationException(
                        $"program \"{p.Name}\" macro \"{kv.Key}\" has no value \"{kv.Value}\" (has: {string.Join("|", macro.Values)}) - archive layout changed?");
            }

            int idx = p.GetBinaryIndex(p.GetVariationIndex(combo));
            Directory.CreateDirectory(outDir);

            bool wroteAny = false;
            for (int stage = 0; stage < 2; stage++)
            {
                int vi = idx + stage;
                if (vi < 0 || vi >= sharc.Variations.Count)
                    continue;
                var v = sharc.Variations[vi];
                if (v.ByteCode == null || v.ByteCode.Length == 0)
                    continue;

                string glsl = TegraShaderTranslator.Decompile(v.ByteCode);
                if (v.ControlShader != null && v.ControlShader.Length > 0)
                {
                    var control = new ControlShader(v.ControlShader);
                    float[] consts = control.GetConstantsAsFloats(v.ByteCode);
                    if (consts.Length > 0)
                        glsl = ApplyConstants(glsl, consts);
                }

                string kind = v.Type.ToString().ToLowerInvariant();
                string ext = kind.Contains("pixel") || kind.Contains("frag") ? "frag" : "vert";
                File.WriteAllText(Path.Combine(outDir, $"{baseName}.{ext}"), glsl);
                wroteAny = true;
            }

            if (!wroteAny)
                throw new InvalidOperationException(
                    $"program \"{p.Name}\" combination [{string.Join(" ", combo.Select(kv => $"{kv.Key}={kv.Value}"))}] yielded no bytecode - archive layout changed?");
        }

        /// <summary>
        /// The two romfs SARCs that hold every agl <c>.sharcb</c> archive, relative to romfs root.
        /// </summary>
        static readonly string[] AglArchiveSarcs =
        {
            Path.Combine("Lib", "agl", "agl_resource.Nin_NX_NVN.release.sarc.zs"),
            Path.Combine("Shader", "ApplicationPackage.Nin_NX_NVN.release.sarc.zs"),
        };

        /// <summary>
        /// Opens one <c>.sharcb</c> out of a zstd-compressed romfs SARC entirely in memory - the
        /// unpack half that <see cref="ExtractHdrCompose"/>/<see cref="ExtractCloudShader"/>/
        /// <see cref="ExtractCloudNoiseShader"/> each previously open-coded identically.
        /// </summary>
        static SharcfbFile OpenArchive(string romfsRoot, string sarcRelativePath, string sharcbName)
        {
            string sarcPath = Path.Combine(romfsRoot, sarcRelativePath);
            byte[] raw = File.ReadAllBytes(sarcPath);
            byte[] decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var sarc = SarcLibrary.Sarc.FromBinary(new ArraySegment<byte>(decompressed));
            var entryNames = sarc.Select(kv => kv.Key).ToList();
            if (!entryNames.Contains(sharcbName))
                throw new FileNotFoundException($"'{sarcPath}' has no \"{sharcbName}\" entry (has: {string.Join(", ", entryNames)}) - archive layout changed?");
            return new SharcfbFile(new MemoryStream(sarc[sharcbName].ToArray()));
        }

        /// <summary>
        /// Lists every program in every agl <c>.sharcb</c>, with its real macro matrix, samplers and
        /// uniform blocks - all read straight from romfs, nothing decompiled.
        /// </summary>
        /// <remarks>
        /// This is the discovery step that has to come BEFORE writing any new
        /// <c>Extract*Shader</c>: those methods each hardcode a program name and one macro
        /// combination, and both have to be real. A wrong macro key silently resolves to variation
        /// 0 rather than failing, so guessing produces a shader that decompiles cleanly and is
        /// quietly the wrong variant - which is exactly the unresolved weakness
        /// <see cref="ExtractCloudNoiseShader"/> documents about its own <c>RENDER_TYPE</c> choice.
        ///
        /// Sampler and block lists are printed alongside because they are how you tell which of
        /// several similarly-named programs is the one that consumes the inputs you can supply -
        /// the same reasoning <see cref="ExtractHdrCompose"/> used to pick TABLE0 over TABLE1. With
        /// <paramref name="allCombos"/> they are printed per macro combination, which is what makes
        /// a macro choice evidence-based: a macro that adds a sampler Marrow cannot bind, or drops
        /// one it needs, rules itself out without anything being decompiled or rendered.
        /// </remarks>
        /// <param name="nameFilter">Substring-matched against program names; null lists everything.</param>
        /// <param name="allCombos">Print samplers/blocks for every macro combination, not just variation 0.</param>
        public static void ListAglPrograms(string romfsRoot, string? nameFilter = null, bool allCombos = false)
        {
            foreach (string rel in AglArchiveSarcs)
            {
                string sarcPath = Path.Combine(romfsRoot, rel);
                if (!File.Exists(sarcPath))
                {
                    Console.WriteLine($"-- missing (skipped): {rel}");
                    continue;
                }

                byte[] raw = File.ReadAllBytes(sarcPath);
                byte[] dec = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
                var sarc = SarcLibrary.Sarc.FromBinary(new ArraySegment<byte>(dec));

                foreach (string key in sarc.Select(kv => kv.Key).OrderBy(k => k))
                {
                    if (!key.EndsWith(".sharcb", StringComparison.OrdinalIgnoreCase))
                        continue;

                    SharcfbFile sharc;
                    try
                    {
                        sharc = new SharcfbFile(new MemoryStream(sarc[key].ToArray()));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"=== {rel} -> {key} : UNREADABLE ({ex.GetType().Name}: {ex.Message})");
                        continue;
                    }

                    var shown = sharc.Programs
                        .Where(p => nameFilter is null || p.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (shown.Count == 0)
                        continue;

                    Console.WriteLine();
                    Console.WriteLine($"=== {rel} -> {key}  ({shown.Count}/{sharc.Programs.Count} programs shown) ===");
                    foreach (var p in shown)
                    {
                        Console.WriteLine($"   {p.Name}   (vtx={p.HasVertexShader()} pix={p.HasPixelShader()})");
                        if (p.VariationMacros.Count > 0)
                            foreach (var m in p.VariationMacros)
                                Console.WriteLine($"        macro {m.Name}[{string.Join("|", m.Values)}]");

                        var combos = allCombos && p.VariationMacros.Count > 0
                            ? ShaderLibrary.Sharc.SharcUtils.GetAllVariationCombinations(p.VariationMacros).ToList()
                            : new List<Dictionary<string, string>> { new Dictionary<string, string>() };

                        foreach (var combo in combos)
                        {
                            int combIndex = combo.Count > 0
                                ? ShaderLibrary.Sharc.SharcUtils.GetVariationIndex(p.VariationMacros, combo)
                                : 0;
                            if (combo.Count > 0)
                                Console.WriteLine($"      + {string.Join(" ", combo.Select(kv => $"{kv.Key}={kv.Value}"))}");

                            int idx = p.GetBinaryIndex(combIndex);
                            for (int stage = 0; stage < 2; stage++)
                            {
                                int vi = idx + stage;
                                if (vi < 0 || vi >= sharc.Variations.Count)
                                    continue;
                                var v = sharc.Variations[vi];
                                // The archive's sampler Location is a 0-based index, while the
                                // decompiled GLSL names each sampler fp_t_tcb_<hex constant-buffer
                                // slot> - and the two are related by slot = 8 + 2*Location, NOT by
                                // the archive's list order. Getting this wrong silently swaps
                                // textures rather than failing: verified on three independently
                                // extracted programs (sky_postfx_sky's cTexBakedInscatter@0 ->
                                // fp_t_tcb_8; its RENDER_SUN variant's cTexTransmittance@0 ->
                                // fp_t_tcb_8 with cTexBakedInscatter@1 -> fp_t_tcb_A) and it also
                                // reproduces CloudDomePass's own hand-derived mapping, where
                                // cBaseTexture is fp_t_tcb_A and cNoiseTexture is fp_t_tcb_8.
                                if (v.Samplers.Count > 0)
                                    Console.WriteLine($"        [{v.Type}] samplers: {string.Join(", ", v.Samplers.Select(s => $"{s.Name}@{s.Location}->fp_t_tcb_{8 + 2 * s.Location:X}"))}");
                                if (v.UniformBlocks.Count > 0)
                                    Console.WriteLine($"        [{v.Type}] blocks:   {string.Join(", ", v.UniformBlocks.Select(b => $"{b.Name}@{b.Location}:{b.Size}"))}");
                                Console.WriteLine($"        [{v.Type}] bytecode: {v.ByteCode?.Length ?? 0} bytes");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The real <c>agl::pfx::Sky</c> shaders - TotK's actual sky, which is a Bruneton
        /// precomputed-atmospheric-scattering implementation, not an analytic gradient. Extracted
        /// from <c>Lib/agl/agl_resource.Nin_NX_NVN.release.sarc.zs</c> -&gt;
        /// <c>agl_technique_pfx.sharcb</c>, the same romfs SARC
        /// <see cref="ExtractCloudShader"/> already reads (a different archive inside it).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This replaces nothing on its own. <c>BackgroundPass</c> currently renders a
        /// HAND-WRITTEN Rayleigh+Mie raymarch - the one remaining piece of invented shading in the
        /// sky path, and the leading suspect for the atmosphere being too bright. These are the
        /// real programs it stands in for.
        /// </para>
        /// <para>
        /// <b>Two groups, because the real sky is two-phase.</b> The
        /// <c>sky_postfx_*</c> programs are cheap per-frame passes that just SAMPLE a precomputed
        /// LUT (<c>cTexBakedInscatter</c>); everything expensive happens once, offline, in the
        /// <c>sky_transmittance</c> -&gt; <c>sky_irradiance</c> -&gt; <c>sky_inscatter</c> -&gt;
        /// <c>sky_delta_inscatter</c> -&gt; <c>sky_copy_*</c> -&gt; <c>sky_bake_*</c> chain, which is
        /// the standard Bruneton multiple-scattering iteration. Their <c>LOCAL_STEP</c> macro is
        /// NOT a variant choice to pick between - it names WHICH iteration of the algorithm the
        /// program is, so every declared value is a separate required pass and all of them are
        /// extracted.
        /// </para>
        /// <para>
        /// <b>Every macro choice below is determined by which inputs Marrow can supply</b>, read
        /// off the per-combination sampler/block matrix that <see cref="ListAglPrograms"/> prints -
        /// not guessed the way <see cref="ExtractCloudNoiseShader"/>'s <c>RENDER_TYPE</c> was:
        /// <list type="bullet">
        /// <item><c>USE_TONEMAP=0</c>: setting it adds no sampler and no block, only pixel
        /// bytecode - it is an inline tonemap. Marrow already ends its frame with the real
        /// <c>agl_hdr_compose</c> via <c>TonemapPass</c>, so =1 would tonemap twice.</item>
        /// <item><c>RENDER_CLOUD=0</c>: =1 adds sampler <c>cTexCloud</c>, a screen-space cloud
        /// buffer Marrow does not produce - its clouds are a separate geometric dome
        /// (<c>CloudDomePass</c>), composited independently.</item>
        /// <item><c>USE_ADHOC_FOG=0</c>: adds no inputs either way, so it is an extra authored fog
        /// term rather than a required one; off is the conservative reading.</item>
        /// <item><c>USE_FOG_DENSITY=0</c> (ground only): =1 adds <c>cTexFogDensity</c>, a
        /// volumetric density texture Marrow has no source for.</item>
        /// <item><c>USE_LINEAR_DEPTH=1</c> (ground only): notably this variant needs FEWER inputs -
        /// it drops <c>RenderInfo@2</c> from the vertex stage, since it does not have to carry
        /// projection constants to linearise depth itself.</item>
        /// <item><c>BAKED_SUNVIEW_NON_LINEAR=1</c>: this one is a genuine PAIRING constraint, not a
        /// free choice - it selects how the inscatter LUT is parameterised, and both the program
        /// that BAKES the LUT (<c>sky_bake_inscatter</c>) and the ones that SAMPLE it carry the
        /// same macro. They must agree or the lookup is simply wrong. Set to 1 (the non-linear
        /// sun-view mapping, which exists to spend LUT precision near the horizon where the
        /// gradient is steepest) consistently across all of them here. If it ever needs flipping,
        /// flip it in ALL of them together - that is the whole hazard.</item>
        /// <item><c>RENDER_SUN</c>: extracted BOTH ways, to separate files. =1 additionally samples
        /// <c>cTexTransmittance</c> - which is producible, being the output of
        /// <c>sky_transmittance</c>, the chain's own first step - and it also RELOCATES every
        /// uniform block (Context 0-&gt;1, RenderInfo 1-&gt;2, plus a new SizeInfo@0). That
        /// relocation is exactly the per-stage binding collision <c>CloudDomePass</c> already has
        /// to correct for at link time, so the two are kept as distinct files rather than one
        /// "sky" shader whose block layout silently depends on a flag.</item>
        /// </list>
        /// </para>
        /// </remarks>
        public static void ExtractSkyPostFxShaders(string romfsRoot, string outDir)
        {
            var sharc = OpenArchive(romfsRoot,
                Path.Combine("Lib", "agl", "agl_resource.Nin_NX_NVN.release.sarc.zs"),
                "agl_technique_pfx.sharcb");

            SharcfbFile.ShaderProgram Program(string name) =>
                sharc.Programs.FirstOrDefault(p => p.Name == name)
                ?? throw new InvalidOperationException($"agl_technique_pfx.sharcb has no \"{name}\" program - archive layout changed?");

            const string NonLinear = "1"; // see the pairing note in the remarks - must match the bake

            // ---- per-frame passes ----
            var sky = Program("sky_postfx_sky");
            DecompileVariant(sky, new()
            {
                ["BAKED_SUNVIEW_NON_LINEAR"] = NonLinear,
                ["USE_ADHOC_FOG"] = "0",
                ["RENDER_SUN"] = "0",
                ["RENDER_CLOUD"] = "0",
                ["USE_TONEMAP"] = "0",
            }, outDir, "agl_sky_postfx_sky");

            DecompileVariant(sky, new()
            {
                ["BAKED_SUNVIEW_NON_LINEAR"] = NonLinear,
                ["USE_ADHOC_FOG"] = "0",
                ["RENDER_SUN"] = "1",
                ["RENDER_CLOUD"] = "0",
                ["USE_TONEMAP"] = "0",
            }, outDir, "agl_sky_postfx_sky_sun");

            DecompileVariant(Program("sky_postfx_ground"), new()
            {
                ["BAKED_SUNVIEW_NON_LINEAR"] = NonLinear,
                ["USE_ADHOC_FOG"] = "0",
                ["USE_LINEAR_DEPTH"] = "1",
                ["USE_FOG_DENSITY"] = "0",
                ["USE_TONEMAP"] = "0",
            }, outDir, "agl_sky_postfx_ground");

            // ---- the offline precompute chain ----
            // LOCAL_STEP enumerates iterations of the Bruneton solve, so every declared value is a
            // distinct pass the chain needs, not an alternative to choose between.
            foreach (string name in new[]
            {
                "sky_transmittance", "sky_irradiance", "sky_inscatter", "sky_delta_inscatter",
                "sky_copy_inscatter", "sky_copy_irradiance", "sky_bake_irradiance",
                "sky_bake_range_transmittance",
            })
            {
                var p = Program(name);
                var stepMacro = p.VariationMacros.FirstOrDefault(m => m.Name == "LOCAL_STEP");
                if (stepMacro is null)
                {
                    DecompileVariant(p, new(), outDir, $"agl_{name}");
                    continue;
                }
                foreach (string step in stepMacro.Values)
                    DecompileVariant(p, new() { ["LOCAL_STEP"] = step }, outDir, $"agl_{name}_step{step}");
            }

            // sky_bake_inscatter carries the pairing macro too, plus its own ADHOC_PROC.
            DecompileVariant(Program("sky_bake_inscatter"), new()
            {
                ["LOCAL_STEP"] = "0",
                ["BAKED_SUNVIEW_NON_LINEAR"] = NonLinear,
                ["ADHOC_PROC"] = "0",
            }, outDir, "agl_sky_bake_inscatter");

            void DecompileVariant(SharcfbFile.ShaderProgram p, Dictionary<string, string> combo,
                string dir, string baseName) => TestAglShader.DecompileVariant(sharc, p, combo, dir, baseName);
        }

        static void Run(SharcfbFile sharc, string sourceLabel, string outDir, params string[] programFilter)
        {
            Directory.CreateDirectory(outDir);
            Console.WriteLine("################################################################");
            Console.WriteLine($"# agl shader archive: {sourceLabel}");
            Console.WriteLine("################################################################");

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
