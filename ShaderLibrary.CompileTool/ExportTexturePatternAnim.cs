using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BfresLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Writes <c>&lt;modelName&gt;.&lt;AnimName&gt;.texpat.json</c> for every BFRES texture pattern
    /// animation (an FMAA whose materials carry <c>TexturePatternCount &gt; 0</c>; BfresLibrary
    /// files those under <c>ResFile.TexPatternAnims</c>), and reports every texture they can select
    /// so <see cref="ExportTestBench"/> exports those too - a pattern anim's alternate textures are
    /// by definition NOT the ones the material currently binds, so nothing else would pull them in.
    ///
    /// HOW A TEXTURE PATTERN ANIM WORKS, read out of the shipped code rather than assumed:
    ///   <c>nn::g3d2::ResPerMaterialAnim::EvaluateTexturePatternAnim&lt;false&gt;</c> (Ghidra
    ///   0x7100072d20) walks the material's <c>TexturePatternCount</c> entries; each entry that is
    ///   bound and has a curve is evaluated with <c>ResAnimCurve::EvaluateInt</c> (0x71009b774c) and
    ///   the resulting INTEGER is stored per entry. <c>EvaluateInt</c> returns
    ///   <c>curve.Offset + step(frame)</c> - Offset read as an <c>int</c> at <c>ResAnimCurve[0x24]</c>,
    ///   not the float the same field holds for a TRS curve - and the step evaluator (0x7100073a68,
    ///   the first entry of the EvaluateInt dispatch table at 0x71041da528) does
    ///   <c>keys[FindFrame(frame)]</c> with NO interpolation: a key's value is held until the next
    ///   key's frame. That integer indexes the FMAA's own texture-name list
    ///   (<c>ResMaterialAnim::BindTexture</c>, 0x7100072e80, resolves each name to a real texture),
    ///   and <c>MaterialAnimObj::ApplyTo</c> (0x7100080894) writes the selected texture into the
    ///   material object's sampler slot. An entry with no curve is a constant instead, taken from
    ///   the material's <c>Constants</c> at its <c>BeginConstant</c>.
    ///
    /// So: per material, per SAMPLER (<c>PatternAnimInfo.Name</c> is the sampler key, e.g. "_e0"),
    /// a step curve of texture indices. Enemy_Dragon_Darkness uses it for exactly this - its
    /// <c>Weakness_0N_Death_ftp</c> anims swap <c>Mt_Weaknesses_0N</c>'s <c>_e0</c> from
    /// <c>..._Weaknesses_Emc.0</c> to <c>..._Weaknesses_Emc.1</c> at frame 5, and
    /// <c>Dm_HighResolutionTex</c> swaps Mt_Face's <c>_e0</c> to a cutscene-resolution variant.
    /// </summary>
    public static class ExportTexturePatternAnim
    {
        /// <summary>
        /// Exports every texture pattern anim in <paramref name="animResFile"/> that this model can
        /// actually use, skipping names already written (the same anim can appear in more than one
        /// candidate archive). Texture names the exported anims can select are added to
        /// <paramref name="texturesToExport"/> for the caller to pull out of <c>TexToGo/</c>.
        /// </summary>
        public static void ExportFrom(string romfsRoot, ResFile animResFile, string modelName, string outDir,
            HashSet<string> alreadyExported, HashSet<string> texturesToExport)
        {
            foreach (MaterialAnim anim in animResFile.TexPatternAnims.Values)
            {
                if (anim.MaterialAnimDataList.All(m => m.TexturePatternCount == 0))
                    continue;
                if (!alreadyExported.Add(anim.Name))
                    continue;

                string safeAnim = anim.Name.Replace(":", "_").Replace("/", "_");
                string outPath = Path.Combine(outDir, $"{modelName}.{safeAnim}.texpat.json");
                Export(romfsRoot, anim, outPath, texturesToExport);
            }
        }

        public static void Export(string romfsRoot, MaterialAnim anim, string outPath, HashSet<string> texturesToExport)
        {
            var textureNames = anim.TextureNames?.Keys.ToList() ?? new List<string>();

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{Escape(anim.Name)}\",");
            sb.AppendLine($"  \"frame_count\": {anim.FrameCount},");
            sb.AppendLine($"  \"loop\": {(anim.Loop ? "true" : "false")},");

            // Full binding info per texture, resolved the same way ExportManifest.BuildSamplers
            // resolves a material's own samplers - so a .texpat.json is self-describing and the
            // runtime can hand each entry straight to its existing texture cache.
            sb.AppendLine("  \"textures\": [");
            for (int ti = 0; ti < textureNames.Count; ti++)
            {
                var (texture, file, format, width, height) = ResolveTexture(romfsRoot, textureNames[ti], ti);
                texturesToExport.Add(texture);
                sb.Append($"    {{ \"texture\": \"{Escape(texture)}\", \"file\": \"{Escape(file)}\", " +
                          $"\"format\": \"{format}\", \"width\": {width}, \"height\": {height} }}");
                sb.AppendLine(ti == textureNames.Count - 1 ? "" : ",");
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"materials\": [");

            var materials = anim.MaterialAnimDataList.Where(m => m.TexturePatternCount > 0).ToList();
            for (int mi = 0; mi < materials.Count; mi++)
            {
                MaterialAnimData mat = materials[mi];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"material\": \"{Escape(mat.Name)}\",");
                sb.AppendLine("      \"samplers\": [");

                var infos = mat.PatternAnimInfos.ToList();
                for (int si = 0; si < infos.Count; si++)
                {
                    PatternAnimInfo info = infos[si];
                    sb.AppendLine("        {");
                    sb.AppendLine($"          \"sampler\": \"{Escape(info.Name)}\",");

                    if (info.CurveIndex >= 0 && info.CurveIndex < mat.Curves.Count)
                    {
                        AnimCurve c = mat.Curves[info.CurveIndex];
                        // BfresLibrary hands every curve back as decoded floats regardless of the
                        // on-disk key type (this one is SByte), so the integer index is the first
                        // element of each key rounded, plus the curve's own Offset - which for an
                        // INT curve is the field's Int32 view, not its Single view.
                        int offset = c.Offset;
                        var values = new List<int>();
                        for (int k = 0; k < c.Keys.GetLength(0); k++)
                            values.Add(offset + (int)Math.Round(c.Keys[k, 0]));

                        sb.AppendLine($"          \"start_frame\": {F(c.StartFrame)},");
                        sb.AppendLine($"          \"end_frame\": {F(c.EndFrame)},");
                        sb.AppendLine($"          \"frames\": [{string.Join(", ", c.Frames.Select(F))}],");
                        sb.AppendLine($"          \"values\": [{string.Join(", ", values)}]");
                    }
                    else
                    {
                        // No curve: a constant selection for the whole anim. Emitting it as a
                        // single value with no frames keeps the runtime's evaluator to one shape.
                        int constant = ConstantValue(mat, info.BeginConstant);
                        sb.AppendLine("          \"start_frame\": 0,");
                        sb.AppendLine($"          \"end_frame\": {anim.FrameCount},");
                        sb.AppendLine("          \"frames\": [],");
                        sb.AppendLine($"          \"values\": [{constant}]");
                    }

                    sb.AppendLine(si == infos.Count - 1 ? "        }" : "        },");
                }

                sb.AppendLine("      ]");
                sb.AppendLine(mi == materials.Count - 1 ? "    }" : "    },");
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"[ExportTestBench] Exported texture pattern anim '{anim.Name}' " +
                $"({materials.Count} material(s), {materials.Sum(m => m.PatternAnimInfos.Count)} sampler(s), " +
                $"{textureNames.Count} texture(s), {anim.FrameCount} frames) -> {outPath}");
        }

        /// <summary>The exported .bin name/format/size for one texture, or empty strings and zeroes if <c>TexToGo/</c> has no such entry or it fails to parse - same tolerance <c>ExportManifest.BuildSamplers</c> has, so one bad texture costs that texture and not the whole anim.</summary>
        static (string Texture, string File, string Format, int Width, int Height) ResolveTexture(
            string romfsRoot, string textureName, int index)
        {
            string name = DedupeName(romfsRoot, textureName, index);
            string txtg = Path.Combine(romfsRoot, "TexToGo", name + ".txtg");
            if (!File.Exists(txtg))
            {
                Console.WriteLine($"[ExportTestBench] texture pattern anim references '{textureName}', which TexToGo/ does not have.");
                return (name, "", "", 0, 0);
            }
            try
            {
                var t = TxtgTexture.Load(txtg);
                int w = (int)t.Width, h = (int)t.Height;
                string fmt = t.Format.ToString();
                return (name, $"{name}_{w}x{h}_{fmt}.bin", fmt, w, h);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExportTestBench] texture pattern anim texture '{name}' failed to read: {ex.Message}");
                return (name, "", "", 0, 0);
            }
        }

        /// <summary>
        /// Undoes BfresLibrary's <c>ResDict</c> key de-duplication. An FMAA's texture list is an
        /// ARRAY that may legitimately name the same texture at several positions - the Lynel's
        /// eye anim points both <c>_a0</c> and <c>_e0</c> at one shared blank <c>CmnTex_*</c> for
        /// three of its four variants - but BfresLibrary loads it into a name-keyed dictionary, and
        /// makes the duplicate keys unique by appending each one's own INDEX. So list entry 13 of a
        /// texture that also appears at entry 12 arrives as <c>CmnTex_&lt;hash&gt;13</c>, which
        /// names no file at all.
        ///
        /// Strip that suffix ONLY when all three things hold: the name as given resolves to
        /// nothing, it ends with the decimal form of this entry's own index, and what remains after
        /// removing it does resolve. A texture genuinely named with a trailing digit therefore
        /// survives untouched, because the first condition already failed for it.
        /// </summary>
        static string DedupeName(string romfsRoot, string textureName, int index)
        {
            if (File.Exists(Path.Combine(romfsRoot, "TexToGo", textureName + ".txtg")))
                return textureName;

            string suffix = index.ToString(CultureInfo.InvariantCulture);
            if (!textureName.EndsWith(suffix, StringComparison.Ordinal) || textureName.Length <= suffix.Length)
                return textureName;

            string stripped = textureName[..^suffix.Length];
            if (!File.Exists(Path.Combine(romfsRoot, "TexToGo", stripped + ".txtg")))
                return textureName;

            Console.WriteLine($"[ExportTestBench] texture pattern anim entry {index} '{textureName}' is BfresLibrary's " +
                $"de-duplicated key for '{stripped}' (the same texture listed more than once) - using the real name.");
            return stripped;
        }

        static int ConstantValue(MaterialAnimData mat, int index)
        {
            // AnimConstant.Value is a DWord union; an int curve's constant is its Int32 view.
            if (mat.Constants == null || index < 0 || index >= mat.Constants.Count)
                return -1;
            return mat.Constants[index].Value;
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
