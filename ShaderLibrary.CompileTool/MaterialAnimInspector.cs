using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Dumps every <c>MaterialAnim</c> (BFRES FMAA) an actor can reach - the model's own
    /// <c>.bfres.mc</c> plus each <c>&lt;pack&gt;.anim.bfres.zs</c> its actor names - so the
    /// texture-pattern half of them can be read against the runtime's own evaluator before any of
    /// it is exported.
    ///
    /// WHAT A TEXTURE PATTERN ANIM IS, from the shipped code
    /// (<c>nn::g3d2::ResPerMaterialAnim::EvaluateTexturePatternAnim&lt;false&gt;</c>, Ghidra
    /// 0x7100072d20, and <c>MaterialAnimObj::ApplyTo</c> 0x7100080894):
    ///   - an FMAA carries ONE texture-name list shared by all of its materials
    ///     (<see cref="MaterialAnim.TextureNames"/>; the runtime resolves each to a real texture in
    ///     <c>ResMaterialAnim::BindTexture</c>, 0x7100072e80);
    ///   - each animated material has <c>TexturePatternCount</c> entries, one per SAMPLER, and each
    ///     entry names the sampler it drives (<c>PatternAnimInfo.Name</c>, e.g. "_a0");
    ///   - the entry's curve is an INTEGER curve (<c>ResAnimCurve::EvaluateInt</c>, 0x71009b774c -
    ///     curve types 0x40 StepInt / 0x50 BakedInt, and the result is <c>Offset + key</c> where
    ///     Offset is read as an INT, not the float it is for a TRS curve), and its value is an
    ///     INDEX into that texture-name list;
    ///   - an entry whose <c>CurveIndex</c> is -1 is a constant instead, taken from the material's
    ///     own <c>Constants</c> at <c>BeginConstant</c>;
    ///   - <c>ApplyTo</c> then writes the selected texture into the material object's sampler slot.
    /// So the whole thing is "swap which texture this sampler points at, per frame" - eye blinks,
    /// mouth shapes, flipbook effects.
    /// </summary>
    public static class MaterialAnimInspector
    {
        public static void Inspect(string romfsRoot, string actorOrModelName)
        {
            ExternalBinaryStringTable.RomfsRoot = romfsRoot;
            BfresLibraryPatches.EnsureApplied();

            var actor = ActorInfo.Resolve(romfsRoot, actorOrModelName);
            string modelName = actor?.ModelName ?? actorOrModelName;
            Console.WriteLine($"=== material anims for '{actorOrModelName}' -> model '{modelName}' ===");

            var sources = new List<(string Label, ResFile Res)>();

            string? mc = RomfsPaths.ModelFile(romfsRoot, modelName);
            if (mc != null)
            {
                byte[] fres = TestMaterialDump.DecompressBfresMc(mc);
                sources.Add(($"model {Path.GetFileName(mc)}", new ResFile(new MemoryStream(fres), false)));
            }
            else
            {
                Console.WriteLine($"  {RomfsPaths.Explain(romfsRoot, modelName)}");
            }

            foreach (string pack in CandidateAnimPacks(romfsRoot, modelName, actor))
            {
                string path = Path.Combine(romfsRoot, "Model", $"{pack}.anim.bfres.zs");
                if (!File.Exists(path))
                    continue;
                var res = ExportTestBench.LoadAnimArchiveForInspection(path);
                if (res != null)
                    sources.Add(($"archive {pack}.anim.bfres.zs", res));
            }

            var samplerUse = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int totalAnims = 0, totalPatternEntries = 0;

            foreach (var (label, res) in sources)
            {
                // BfresLibrary files an FMAA under whichever of these dictionaries its flags say it
                // drives; a texture pattern anim lands in TexPatternAnims, but scan all of them so a
                // combined anim (patterns AND shader params in one FMAA) is not missed.
                var buckets = new (string Kind, BfresLibrary.ResDict<MaterialAnim> Dict)[]
                {
                    ("TexPattern", res.TexPatternAnims),
                    ("ShaderParam", res.ShaderParamAnims),
                    ("Color", res.ColorAnims),
                    ("TexSrt", res.TexSrtAnims),
                    ("MatVisibility", res.MatVisibilityAnims),
                };
                int animCount = buckets.Sum(b => b.Dict.Count);
                Console.WriteLine($"\n-- {label}: {animCount} material anim(s) " +
                    $"({string.Join(", ", buckets.Where(b => b.Dict.Count > 0).Select(b => $"{b.Kind}={b.Dict.Count}"))}), {res.SkeletalAnims.Count} skeletal");
                foreach (var (kind, dict) in buckets)
                foreach (MaterialAnim anim in dict.Values)
                {
                    totalAnims++;
                    var textureNames = anim.TextureNames?.Keys.ToList() ?? new List<string>();
                    int patternMaterials = anim.MaterialAnimDataList.Count(m => m.TexturePatternCount > 0);
                    Console.WriteLine($"   [{kind}] '{anim.Name}' frames={anim.FrameCount} loop={anim.Loop} flags={anim.Flags} " +
                        $"| materials={anim.MaterialAnimDataList.Count} (with texture patterns: {patternMaterials}) " +
                        $"| textureNames={textureNames.Count} | bindIndices={anim.BindIndices?.Length ?? 0}");
                    if (textureNames.Count > 0)
                        Console.WriteLine($"      textures: [{string.Join(", ", textureNames)}]");

                    foreach (MaterialAnimData mat in anim.MaterialAnimDataList)
                    {
                        if (mat.TexturePatternCount == 0)
                            continue;
                        Console.WriteLine($"      material '{mat.Name}': patterns={mat.TexturePatternCount} " +
                            $"params={mat.ParamCount} visibility={mat.VisibilyCount} " +
                            $"| curves={mat.Curves.Count} constants={mat.Constants?.Count ?? 0} " +
                            $"| texPatternCurveIndex={mat.TexturePatternCurveIndex}");
                        foreach (PatternAnimInfo info in mat.PatternAnimInfos)
                        {
                            totalPatternEntries++;
                            samplerUse[info.Name] = samplerUse.GetValueOrDefault(info.Name) + 1;
                            if (info.CurveIndex >= 0 && info.CurveIndex < mat.Curves.Count)
                            {
                                AnimCurve c = mat.Curves[info.CurveIndex];
                                var values = IntKeys(c).ToList();
                                var distinct = values.Distinct().OrderBy(v => v).ToList();
                                Console.WriteLine($"         sampler '{info.Name}' curve[{info.CurveIndex}] " +
                                    $"type={c.CurveType} keyType={c.KeyType} frames={c.Frames.Length} " +
                                    $"range=[{c.StartFrame}, {c.EndFrame}] offsetInt={(int)c.Offset} " +
                                    $"subBind={info.SubBindIndex}");
                                Console.WriteLine($"            frames: [{string.Join(", ", c.Frames.Take(12))}{(c.Frames.Length > 12 ? ", ..." : "")}]");
                                Console.WriteLine($"            -> texture indices: [{string.Join(", ", values.Take(12))}{(values.Count > 12 ? ", ..." : "")}]" +
                                    $"  distinct {{{string.Join(", ", distinct.Select(v => v >= 0 && v < textureNames.Count ? $"{v}={textureNames[v]}" : $"{v}=<out of range>"))}}}");
                            }
                            else
                            {
                                int constant = ConstantValue(mat, info.BeginConstant);
                                Console.WriteLine($"         sampler '{info.Name}' CONSTANT beginConstant={info.BeginConstant} " +
                                    $"-> {(constant >= 0 && constant < textureNames.Count ? $"{constant}={textureNames[constant]}" : $"{constant}=<out of range>")} " +
                                    $"subBind={info.SubBindIndex}");
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"\n=== {totalAnims} material anim(s), {totalPatternEntries} texture-pattern entries ===");
            if (samplerUse.Count > 0)
                Console.WriteLine($"samplers driven: {string.Join(", ", samplerUse.Select(kv => $"{kv.Key} x{kv.Value}"))}");
        }

        /// <summary>An integer curve's already-decoded keys. BfresLibrary hands every curve back as floats regardless of on-disk key type, so the int value is the first element of each key rounded, plus the curve's own <c>Offset</c> - which for an int curve is an INT bit pattern, not a float (<c>ResAnimCurve::EvaluateInt</c> reads <c>[0x24]</c> as <c>int</c>).</summary>
        static IEnumerable<int> IntKeys(AnimCurve c)
        {
            int offset = c.Offset;
            for (int k = 0; k < c.Keys.GetLength(0); k++)
                yield return offset + (int)Math.Round(c.Keys[k, 0]);
        }

        static int ConstantValue(MaterialAnimData mat, int index)
        {
            if (mat.Constants == null || index < 0 || index >= mat.Constants.Count)
                return -1;
            return mat.Constants[index].Value;
        }

        static IEnumerable<string> CandidateAnimPacks(string romfsRoot, string modelName, ActorInfo.Resolved? actor)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (actor != null)
            {
                foreach (string pack in actor.AnimPackNames)
                    if (seen.Add(pack))
                        yield return pack;
            }
            // The actor's own AnimationParam is authoritative, but a model whose actor could not be
            // resolved still usually has a sibling archive named after the model or its pack prefix.
            foreach (string guess in new[] { modelName, modelName.Split('.')[0], modelName.Split('.')[0] + "_Animation" })
                if (seen.Add(guess))
                    yield return guess;
        }
    }
}
