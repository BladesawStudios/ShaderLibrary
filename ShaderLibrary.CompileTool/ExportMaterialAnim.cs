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
    /// Writes <c>&lt;modelName&gt;.&lt;AnimName&gt;.matanim.json</c> for every SHADER PARAMETER
    /// animation - which is one mechanism covering what BFRES names three ways: plain shader-param
    /// anims (<c>_fsp</c>), colour anims (<c>_fcl</c>) and TEXTURE SRT anims (<c>_fts</c>). They
    /// differ only in which parameters they happen to drive, and BfresLibrary's own bucketing does
    /// not separate them reliably (Enemy_Dragon_Darkness's <c>_fts</c> anims all arrive in
    /// <c>ResFile.ShaderParamAnims</c>), so all three dictionaries are exported the same way and the
    /// distinction is reported rather than relied on.
    ///
    /// HOW ONE ADDRESSES ITS TARGET, verified against the shipped code and against real data:
    ///   <c>ResPerMaterialAnim::EvaluateShaderParamAnim&lt;false&gt;</c> (Ghidra 0x7100072b44) walks
    ///   each material's <c>ParamAnimInfo</c> entries, evaluating the int curves of each with
    ///   <c>EvaluateInt</c> and the float curves with <c>EvaluateFloat</c>;
    ///   <c>MaterialAnimObj::ApplyTo</c> (0x7100080894) then copies each evaluated 4-byte word into
    ///   the material's parameter block at that curve's <c>AnimDataOffset</c>.
    ///
    ///   <c>AnimDataOffset</c> is the offset WITHIN THE NAMED PARAMETER, not into anything else.
    ///   <c>ParamAnimInfo.Name</c> already names the parameter; the offset picks the byte of it.
    ///   Enemy_Dragon_Darkness proves both halves: <c>Face_Eye_Scroll_fts</c> drives
    ///   <c>p_tex_srt1</c> (a 24-byte TexSrt: mode:int, scaleX, scaleY, rotation, translateX,
    ///   translateY) with one curve at <c>animOffset 20</c> - translateY, i.e. a scrolling eye -
    ///   and a constant at <c>animOffset 0</c> setting the mode. A plain <c>Float</c> parameter like
    ///   <c>p_const_value0</c> only ever has <c>animOffset 0</c>.
    ///
    /// So the target byte in the compiled <c>gsys_material</c> block is
    /// <c>blockOffset[paramName] + animOffset</c>, and <c>blockOffset</c> comes from the sidecar
    /// <c>BuildMaterialUbo.WriteParamLayout</c> writes. Nothing here needs the material's own
    /// packed <c>ShaderParamData</c> layout, which is a different space entirely.
    /// </summary>
    public static class ExportMaterialAnim
    {
        /// <summary>Curve types whose evaluated value is an INTEGER bit pattern rather than a float - <c>AnimCurveType.StepInt</c>/<c>BakedInt</c>/<c>StepBool</c>/<c>BakedBool</c>. A curve's own type is the unambiguous signal, so nothing here has to guess from the parameter's type.</summary>
        static bool IsIntCurve(AnimCurve c) => (int)c.CurveType >= 0x40;

        /// <param name="modelMaterials">
        /// The MODEL's own materials, used only to classify an anim and to warn about a parameter
        /// the material does not declare. BfresLibrary's bucketing cannot be trusted for the
        /// classification (every one of Enemy_Dragon_Darkness's <c>_fts</c> anims arrives in
        /// <c>ShaderParamAnims</c>), so an anim is called a texture SRT anim when the parameters it
        /// actually drives are SRT-typed - which is the only honest signal available.
        /// </param>
        public static void ExportFrom(ResFile animResFile, string modelName, string outDir,
            HashSet<string> alreadyExported, IReadOnlyDictionary<string, Material> modelMaterials)
        {
            var buckets = new (string Kind, BfresLibrary.ResDict<MaterialAnim> Dict)[]
            {
                ("ShaderParam", animResFile.ShaderParamAnims),
                ("Color", animResFile.ColorAnims),
                ("TexSrt", animResFile.TexSrtAnims),
            };

            foreach (var (kind, dict) in buckets)
            foreach (MaterialAnim anim in dict.Values)
            {
                if (anim.MaterialAnimDataList.All(m => m.ParamCount == 0))
                    continue;
                if (!alreadyExported.Add(anim.Name))
                    continue;

                string safeAnim = anim.Name.Replace(":", "_").Replace("/", "_");
                Export(Classify(kind, anim, modelMaterials), anim, Path.Combine(outDir, $"{modelName}.{safeAnim}.matanim.json"), modelMaterials);
            }
        }

        /// <summary>
        /// "TexSrt" when every parameter this anim drives that the model actually declares is an
        /// SRT type, otherwise the bucket BfresLibrary filed it under. Anims whose parameters are
        /// entirely unknown to the model keep the bucket name rather than being guessed at.
        /// </summary>
        static string Classify(string bucket, MaterialAnim anim, IReadOnlyDictionary<string, Material> modelMaterials)
        {
            int srt = 0, known = 0;
            foreach (MaterialAnimData mat in anim.MaterialAnimDataList)
            {
                if (!modelMaterials.TryGetValue(mat.Name, out Material? modelMat))
                    continue;
                foreach (ParamAnimInfo info in mat.ParamAnimInfos)
                {
                    if (!modelMat.ShaderParams.ContainsKey(info.Name))
                        continue;
                    known++;
                    if (IsSrt(modelMat.ShaderParams[info.Name].Type))
                        srt++;
                }
            }
            return known > 0 && srt == known ? "TexSrt" : bucket;
        }

        static bool IsSrt(ShaderParamType t) =>
            t is ShaderParamType.TexSrt or ShaderParamType.TexSrtEx or ShaderParamType.Srt2D or ShaderParamType.Srt3D;

        public static void Export(string kind, MaterialAnim anim, string outPath,
            IReadOnlyDictionary<string, Material> modelMaterials)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{Escape(anim.Name)}\",");
            sb.AppendLine($"  \"kind\": \"{kind}\",");
            sb.AppendLine($"  \"frame_count\": {anim.FrameCount},");
            sb.AppendLine($"  \"loop\": {(anim.Loop ? "true" : "false")},");
            sb.AppendLine("  \"materials\": [");

            var materials = anim.MaterialAnimDataList.Where(m => m.ParamCount > 0).ToList();
            int totalCurves = 0, totalConstants = 0;

            for (int mi = 0; mi < materials.Count; mi++)
            {
                MaterialAnimData mat = materials[mi];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"material\": \"{Escape(mat.Name)}\",");
                sb.AppendLine("      \"targets\": [");

                modelMaterials.TryGetValue(mat.Name, out Material? modelMat);
                var lines = new List<string>();
                foreach (ParamAnimInfo info in mat.ParamAnimInfos)
                {
                    string paramType = modelMat != null && modelMat.ShaderParams.ContainsKey(info.Name)
                        ? modelMat.ShaderParams[info.Name].Type.ToString()
                        : "";
                    if (paramType.Length == 0)
                        Console.WriteLine($"[ExportTestBench] material anim '{anim.Name}' drives '{mat.Name}.{info.Name}', " +
                            "which this model's material does not declare - the runtime will skip it.");

                    int begin = info.BeginCurve;
                    int count = info.FloatCurveCount + info.IntCurveCount;
                    for (int c = begin; c < begin + count && c < mat.Curves.Count; c++)
                    {
                        AnimCurve curve = mat.Curves[c];
                        bool isInt = IsIntCurve(curve);
                        var keys = new List<string>();
                        for (int k = 0; k < curve.Keys.GetLength(0); k++)
                        {
                            var elems = new List<string>();
                            for (int e = 0; e < curve.Keys.GetLength(1); e++)
                                elems.Add(F(curve.Keys[k, e]));
                            keys.Add($"[{string.Join(", ", elems)}]");
                        }
                        // An int curve's Offset is the field's Int32 view; a float curve's is its
                        // Single view. Same 4 bytes, different meaning - see EvaluateInt vs
                        // EvaluateFloat, which read ResAnimCurve[0x24] as int and float respectively.
                        string offset = isInt
                            ? ((int)curve.Offset).ToString(CultureInfo.InvariantCulture)
                            : F(curve.Offset);
                        lines.Add($"        {{ \"param\": \"{Escape(info.Name)}\", \"param_type\": \"{paramType}\", " +
                                  $"\"byte_offset\": {curve.AnimDataOffset}, " +
                                  $"\"is_int\": {(isInt ? "true" : "false")}, \"curve_type\": {(int)curve.CurveType}, " +
                                  $"\"start_frame\": {F(curve.StartFrame)}, \"end_frame\": {F(curve.EndFrame)}, " +
                                  $"\"scale\": {F(curve.Scale)}, \"offset\": {offset}, " +
                                  $"\"frames\": [{string.Join(", ", curve.Frames.Select(F))}], " +
                                  $"\"keys\": [{string.Join(", ", keys)}] }}");
                        totalCurves++;
                    }

                    for (int k = 0; k < info.ConstantCount; k++)
                    {
                        int ci = info.BeginConstant + k;
                        if (mat.Constants == null || ci >= mat.Constants.Count)
                            continue;
                        AnimConstant constant = mat.Constants[ci];
                        // A constant needs no interpretation at all: the same 32 bits are written
                        // whether the parameter reads them as a float or an int, so carry the raw
                        // bit pattern and let the shader decide, exactly as ApplyTo's word copy does.
                        lines.Add($"        {{ \"param\": \"{Escape(info.Name)}\", \"param_type\": \"{paramType}\", " +
                                  $"\"byte_offset\": {constant.AnimDataOffset}, " +
                                  $"\"constant_bits\": {(uint)constant.Value} }}");
                        totalConstants++;
                    }
                }

                for (int i = 0; i < lines.Count; i++)
                    sb.AppendLine(i == lines.Count - 1 ? lines[i] : lines[i] + ",");

                sb.AppendLine("      ]");
                sb.AppendLine(mi == materials.Count - 1 ? "    }" : "    },");
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"[ExportTestBench] Exported {kind} material anim '{anim.Name}' " +
                $"({materials.Count} material(s), {totalCurves} curve(s), {totalConstants} constant(s), " +
                $"{anim.FrameCount} frames) -> {outPath}");
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
