using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Text;
using BfresLibrary;
using BfresLibrary.Helpers;

namespace ShaderLibrary.CompileTool
{
    public static class ExportTestBench
    {
        public static void ExportSword(string romfsRoot, string outDir)
        {
            Directory.CreateDirectory(outDir);
            ExportModel(romfsRoot, "Weapon_Sword_070", outDir);
            ExportModel(romfsRoot, "Weapon_Sword_071", outDir);

            // Environment UBO (352 bytes)
            byte[] envUbo = GsysUniformBuffers.BuildGsysEnvironment();
            File.WriteAllBytes(Path.Combine(outDir, "GsysEnvironment.bin"), envUbo);
        }

        /// <summary>
        /// World-space matrix per bone, from the skeleton's local TRS walked down the hierarchy -
        /// the exact walk <c>nn::g3d2::SkeletonObj::CalculateWorldImpl</c> performs (Ghidra:
        /// NoScale 0x7100080b40, Standard 0x7100080d00, Maya 0x7100080f2c; picked by
        /// <c>CalculateWorldMtx</c> 0x710008246c from <c>(ResSkeleton.flags &gt;&gt; 8) &amp; 3</c>,
        /// i.e. <see cref="Skeleton.FlagsScaling"/>). Row-vector convention throughout:
        /// <c>world = Scale * Rotate * Translate * parentWorld</c>.
        ///
        /// The three modes differ ONLY in how scale propagates:
        ///   None (0)      - the local scale is never applied at all.
        ///   Standard (1)  - scale multiplies world rows 0/1/2 by scale.x/y/z after the parent multiply.
        ///   Maya (2)      - same, plus SEGMENT SCALE COMPENSATE: a child whose bone flag bit 23 is
        ///                   set divides its parent's world rows 0/1/2 by the PARENT'S OWN LOCAL
        ///                   scale before composing, so the parent's scale does not cascade.
        /// Softimage (3) is treated as Maya (TotK ships Standard/Maya only; the game's own dispatch
        /// table at 0x71041da970 has no fourth non-billboard entry).
        ///
        /// WHY the world matrices matter for geometry export: a shape's vertex positions are NOT
        /// always in model space. BFRES stores them in the space implied by the shape's binding:
        ///   VertexSkinCount == 0  rigid       - positions are in BONE space (Shape.BoneIndex)
        ///   VertexSkinCount == 1  single-bind - positions are in BONE space (per-vertex index)
        ///   VertexSkinCount >= 2  smooth      - positions are already in model/bind space
        /// Only skin count 0 is baked here; everything else is skinned on the GPU every frame.
        /// </summary>
        public static Matrix4x4[] BoneWorldMatrices(Skeleton skel)
        {
            var bones = skel.BoneList;
            int mode = ScalingMode(skel);
            var world = new Matrix4x4[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                Bone b = bones[i];
                Matrix4x4 rot = b.FlagsRotation == BoneFlagsRotation.Quaternion
                    ? Matrix4x4.CreateFromQuaternion(
                        new Quaternion(b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W))
                    : Matrix4x4.CreateRotationX(b.Rotation.X)
                      * Matrix4x4.CreateRotationY(b.Rotation.Y)
                      * Matrix4x4.CreateRotationZ(b.Rotation.Z);

                Matrix4x4 translate = Matrix4x4.CreateTranslation(b.Position.X, b.Position.Y, b.Position.Z);
                Matrix4x4 local = mode == 0
                    ? rot * translate
                    : Matrix4x4.CreateScale(b.Scale.X, b.Scale.Y, b.Scale.Z) * rot * translate;

                // Parents always precede children in a BFRES skeleton, so one forward pass is
                // enough - no recursion needed.
                if (b.ParentIndex < 0 || b.ParentIndex >= i)
                {
                    world[i] = local;
                    continue;
                }

                Matrix4x4 parent = world[b.ParentIndex];
                if (mode >= 2 && SegmentScaleCompensate(b))
                {
                    var ps = bones[b.ParentIndex].Scale;
                    parent = DescaleRows(parent, ps.X, ps.Y, ps.Z);
                }
                world[i] = local * parent;
            }
            return world;
        }

        /// <summary>0 = None, 1 = Standard, 2 = Maya, 3 = Softimage - <c>(FSKL.flags &gt;&gt; 8) &amp; 3</c>, the same value the game switches on.</summary>
        public static int ScalingMode(Skeleton skel) => ((int)skel.FlagsScaling >> 8) & 3;

        /// <summary>
        /// Divides a world matrix's three basis rows by <paramref name="sx"/>/<paramref name="sy"/>/
        /// <paramref name="sz"/>, leaving its translation row alone - the segment-scale-compensate
        /// step <c>CalculateWorldImpl&lt;CalculateWorldMaya&gt;</c> applies to a bone's PARENT before
        /// composing (it reciprocates the parent's local scale and scales the parent's world rows).
        /// </summary>
        public static Matrix4x4 DescaleRows(Matrix4x4 m, float sx, float sy, float sz)
        {
            if (sx == 1f && sy == 1f && sz == 1f)
                return m;
            float ix = sx != 0f ? 1f / sx : 0f;
            float iy = sy != 0f ? 1f / sy : 0f;
            float iz = sz != 0f ? 1f / sz : 0f;
            m.M11 *= ix; m.M12 *= ix; m.M13 *= ix;
            m.M21 *= iy; m.M22 *= iy; m.M23 *= iy;
            m.M31 *= iz; m.M32 *= iz; m.M33 *= iz;
            return m;
        }

        static readonly System.Reflection.FieldInfo? BoneRawFlagsField = typeof(Bone).GetField(
            "_flags", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// Bone flag bit 23 (0x800000). <see cref="BfresLibrary.BoneFlagsTransform"/> masks it out
        /// entirely (it only models 0xF000000), so read the raw flags word the loader kept - the
        /// game masks the very same bit into its per-bone local-matrix flags
        /// (<c>SkeletonObj::ClearLocalMtx</c> 0x7100ad6978: <c>ResBone[0x2C] &amp; 0x0F870000</c>) and
        /// tests it in the Maya walk.
        /// </summary>
        public static bool SegmentScaleCompensate(Bone b) =>
            BoneRawFlagsField != null && ((uint)BoneRawFlagsField.GetValue(b)! & 0x800000) != 0;

        /// <summary>
        /// Writes <c>&lt;modelName&gt;.skeleton.json</c> - everything Marrow's runtime needs to
        /// build the real gsys_skeleton palette itself (bind-pose local TRS per bone, the skeleton's
        /// scaling mode, and the smooth/rigid matrix tables from <see cref="BfresLibrary.Skeleton"/>)
        /// instead of this exporter baking one fixed pose into vertex positions. Skipped (with a log
        /// line, not a failure) for a model with no bones at all.
        ///
        /// THE PALETTE IS TWO SEGMENTS, AND <c>MatrixToBoneList</c> COVERS BOTH. Verified against
        /// <c>nn::g3d2::SkeletonObj::CalculateSkeleton</c> (Ghidra 0x71000824a8) and
        /// <c>SetupBlockBufferImpl</c> (0x7100081dfc):
        ///   - the buffer is <c>(FSKL[0x3A] + FSKL[0x3C]) * 0x30</c> bytes - smoothCount + rigidCount
        ///     mat3x4 entries;
        ///   - <c>FSKL[0x18]</c> (BfresLibrary's <c>Skeleton.MatrixToBoneList</c>) is ONE s16 array of
        ///     smoothCount + rigidCount entries, read as <c>list[i]</c> for the smooth segment and
        ///     <c>list[smoothCount + j]</c> for the rigid one;
        ///   - <c>FSKL[0x20]</c> (<c>Skeleton.InverseModelMatrices</c>) holds exactly smoothCount
        ///     entries and is consumed in lock-step with the smooth half only.
        /// So <c>InverseModelMatrices.Count &lt; MatrixToBoneList.Count</c> is NORMAL, not a truncated
        /// file: the difference IS the rigid segment. Treating the whole list as smooth (which this
        /// exporter used to do) both inflates smoothCount and invents inverse-bind matrices for rigid
        /// slots - the mechanism behind rigid parts sitting still inside an otherwise moving mesh.
        ///
        /// <c>Bone.RigidMatrixIndex</c> is an ABSOLUTE palette index, already offset past the smooth
        /// segment - e.g. Animal_Bass has 4 smooth slots and its Head bone reports
        /// <c>SmoothMatrixIndex 0, RigidMatrixIndex 4</c>, with <c>MatrixToBoneList[4]</c> pointing
        /// back at Head. Nothing may add smoothCount to it a second time.
        ///
        /// The real per-slot skinning matrix - <c>InverseModelMatrix * BoneWorld</c> for the smooth
        /// segment, <c>BoneWorld</c> directly (no inverse-bind multiply) for the rigid segment - is
        /// built at RUNTIME from this file by
        /// <c>Marrow.Shaders.Profiles.Totk.Ubos.BonePaletteUbo.Build</c>.
        /// </summary>
        public static void ExportSkeleton(Skeleton skel, string outPath)
        {
            if (skel.BoneList.Count == 0)
            {
                Console.WriteLine("[ExportTestBench] No skeleton bones - skipping skeleton.json.");
                return;
            }

            var matrixToBoneList = skel.MatrixToBoneList ?? [];
            var inverseModelMatrices = skel.InverseModelMatrices ?? [];

            // A bone carries its own slot number in each segment (-1 = "not in this segment"), so
            // counting them is the most direct read of the two ushort counts the FSKL header stores
            // and the game's CalculateSkeleton loops on.
            int smoothCount = skel.BoneList.Count(b => b.SmoothMatrixIndex >= 0);
            int rigidCount = skel.BoneList.Count(b => b.RigidMatrixIndex >= 0);
            if (smoothCount + rigidCount != matrixToBoneList.Count)
            {
                Console.WriteLine($"[ExportTestBench] WARNING: {skel.BoneList.Count} bones give " +
                    $"{smoothCount} smooth + {rigidCount} rigid slots but MatrixToBoneList has " +
                    $"{matrixToBoneList.Count} entries - falling back to InverseModelMatrices' length for the split.");
                if (inverseModelMatrices.Count > 0 && inverseModelMatrices.Count <= matrixToBoneList.Count)
                    smoothCount = inverseModelMatrices.Count;
                smoothCount = Math.Min(smoothCount, matrixToBoneList.Count);
                rigidCount = matrixToBoneList.Count - smoothCount;
            }

            int scalingMode = ScalingMode(skel);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"scaling_mode\": {scalingMode},");
            sb.AppendLine($"  \"smooth_matrix_count\": {smoothCount},");
            sb.AppendLine($"  \"rigid_matrix_count\": {rigidCount},");
            sb.AppendLine("  \"bones\": [");
            for (int i = 0; i < skel.BoneList.Count; i++)
            {
                Bone b = skel.BoneList[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"name\": \"{b.Name}\",");
                sb.AppendLine($"      \"parent_index\": {b.ParentIndex},");
                sb.AppendLine($"      \"smooth_matrix_index\": {b.SmoothMatrixIndex},");
                sb.AppendLine($"      \"rigid_matrix_index\": {b.RigidMatrixIndex},");
                sb.AppendLine($"      \"billboard_index\": {b.BillboardIndex},");
                sb.AppendLine($"      \"segment_scale_compensate\": {(SegmentScaleCompensate(b) ? "true" : "false")},");
                sb.AppendLine($"      \"scale\": [{F(b.Scale.X)}, {F(b.Scale.Y)}, {F(b.Scale.Z)}],");
                sb.AppendLine($"      \"rotation\": [{F(b.Rotation.X)}, {F(b.Rotation.Y)}, {F(b.Rotation.Z)}, {F(b.Rotation.W)}],");
                sb.AppendLine($"      \"rotation_is_quaternion\": {(b.FlagsRotation == BoneFlagsRotation.Quaternion ? "true" : "false")},");
                sb.AppendLine($"      \"position\": [{F(b.Position.X)}, {F(b.Position.Y)}, {F(b.Position.Z)}]");
                sb.AppendLine(i == skel.BoneList.Count - 1 ? "    }" : "    },");
            }
            sb.AppendLine("  ],");

            sb.AppendLine($"  \"matrix_to_bone_list\": [{string.Join(", ", matrixToBoneList)}],");

            // One inverse-bind matrix per SMOOTH slot, and no more. A BFRES that stores fewer than
            // smoothCount of them gets the only always-correct substitute: the inverse of that bone's
            // own bind-pose world matrix, which is exactly what the stored entry undoes.
            Matrix4x4[]? bindWorld = smoothCount > inverseModelMatrices.Count ? BoneWorldMatrices(skel) : null;

            sb.AppendLine("  \"inverse_model_matrices\": [");
            for (int i = 0; i < smoothCount; i++)
            {
                Matrix4x4 inv;
                if (i < inverseModelMatrices.Count)
                {
                    var m = inverseModelMatrices[i];
                    inv = new Matrix4x4(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                                        m.M31, m.M32, m.M33, m.M34, 0, 0, 0, 1);
                }
                else
                {
                    int bone = i < matrixToBoneList.Count ? matrixToBoneList[i] : -1;
                    if (bone < 0 || bone >= bindWorld!.Length || !Matrix4x4.Invert(bindWorld[bone], out var invNative))
                    {
                        inv = Matrix4x4.Identity;
                    }
                    else
                    {
                        // invNative is a NATIVE row-vector Matrix4x4 (Invert of BoneWorldMatrices'
                        // own native output) - re-pack it into the same GPU-rows-in-Matrix4x4-fields
                        // shape the dump below expects (row i's 4th component = translation i), by
                        // transposing: each dumped "row" is a COLUMN of invNative. Skipping this
                        // silently drops translation from the derived inverse-bind matrix, exactly
                        // like BonePaletteUbo.WriteMatrix's own remarks describe for the same bug.
                        inv = new Matrix4x4(invNative.M11, invNative.M21, invNative.M31, invNative.M41,
                                             invNative.M12, invNative.M22, invNative.M32, invNative.M42,
                                             invNative.M13, invNative.M23, invNative.M33, invNative.M43,
                                             0, 0, 0, 1);
                    }
                }
                sb.Append($"    [{F(inv.M11)}, {F(inv.M12)}, {F(inv.M13)}, {F(inv.M14)}, " +
                          $"{F(inv.M21)}, {F(inv.M22)}, {F(inv.M23)}, {F(inv.M24)}, " +
                          $"{F(inv.M31)}, {F(inv.M32)}, {F(inv.M33)}, {F(inv.M34)}]");
                sb.AppendLine(i == smoothCount - 1 ? "" : ",");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"[ExportTestBench] Exported skeleton ({skel.BoneList.Count} bones, " +
                $"{smoothCount} smooth + {rigidCount} rigid matrices, scaling mode {scalingMode}) -> {outPath}");

            static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Writes <c>&lt;modelName&gt;.&lt;AnimName&gt;.anim.json</c> - a faithful transcription of
        /// one embedded <see cref="SkeletalAnim"/>'s curves (already-decoded floats regardless of
        /// on-disk compression - see <c>AnimCurve.FrameType</c>/<c>KeyType</c>'s own remarks), for
        /// Marrow's runtime to evaluate itself every frame rather than this exporter baking one
        /// fixed pose. The curve math itself (Cubic/Linear/BakedFloat) lives in
        /// <c>Marrow.Core.Rendering.SkeletonPose</c>, not duplicated here.
        ///
        /// Two fields here are easy to get wrong and were, until verified in Ghidra:
        ///
        /// <c>"offset"</c> is <see cref="AnimCurve.Offset"/>, NOT <see cref="AnimCurve.Delta"/>.
        /// <c>nn::g3d2::ResAnimCurve::EvaluateFloat</c> (0x7100073d90) finishes every curve with
        /// <c>ResAnimCurve[0x24] + raw * ResAnimCurve[0x20]</c> - that is Offset + raw * Scale.
        /// <c>Delta</c> is the separate field at 0x28 that only the RELATIVE-REPEAT wrap mode adds
        /// per loop iteration, and it is 0 on essentially every curve. Exporting Delta as if it were
        /// Offset drops each curve's base value: a rotation curve whose quantized keys wobble +-0.07
        /// rad around a -1.49 rad bind angle instead swings the bone to +-0.07 rad absolute, which
        /// reads as limbs snapping to a wrong axis rather than as "the animation is subtly off".
        ///
        /// <c>"rotation_is_quaternion"</c> comes from <see cref="SkeletalAnim.FlagsRotate"/>.
        /// <c>SkeletalAnimObj::ApplyTo</c> (0x710007a93c) dispatches on
        /// <c>(FSKA.flags &gt;&gt; 12) &amp; 7</c>: 0 picks the quaternion path, 1 picks
        /// <c>ApplyToImpl&lt;nn::g3d::EulerToMtx&gt;</c> (0x710007a1a0). For a Euler anim,
        /// <c>base_rotate</c> and curve offsets 32/36/40 are XYZ RADIANS and the W slot is unused -
        /// feeding those three into a quaternion constructor yields a normalized garbage rotation.
        ///
        /// <c>AnimCurve.AnimDataOffset</c> says which bone TRS component each curve drives, and the
        /// mapping is confirmed by the byte offsets <c>ApplyToImpl</c> reads out of its per-bone
        /// result struct: 4/8/12 = Scale.X/Y/Z, 16/20/24 = Translate.X/Y/Z, 32/36/40/44 =
        /// Rotate.X/Y/Z/W.
        /// </summary>
        public static void ExportSkeletalAnim(SkeletalAnim anim, string outPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{anim.Name}\",");
            sb.AppendLine($"  \"frame_count\": {anim.FrameCount},");
            sb.AppendLine($"  \"loop\": {(anim.Loop ? "true" : "false")},");
            sb.AppendLine($"  \"rotation_is_quaternion\": {(anim.FlagsRotate == SkeletalAnimFlagsRotate.Quaternion ? "true" : "false")},");
            sb.AppendLine($"  \"scaling_mode\": {((int)anim.FlagsScale >> 8) & 3},");
            sb.AppendLine("  \"bone_anims\": [");
            for (int bi = 0; bi < anim.BoneAnims.Count; bi++)
            {
                BoneAnim ba = anim.BoneAnims[bi];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"bone_name\": \"{ba.Name}\",");
                sb.AppendLine($"      \"use_scale\": {(ba.UseScale ? "true" : "false")},");
                sb.AppendLine($"      \"use_rotate\": {(ba.UseRotation ? "true" : "false")},");
                sb.AppendLine($"      \"use_translate\": {(ba.UseTranslation ? "true" : "false")},");
                sb.AppendLine($"      \"segment_scale_compensate\": {(ba.ApplySegmentScaleCompensate ? "true" : "false")},");
                sb.AppendLine($"      \"base_scale\": [{F(ba.BaseData.Scale.X)}, {F(ba.BaseData.Scale.Y)}, {F(ba.BaseData.Scale.Z)}],");
                sb.AppendLine($"      \"base_translate\": [{F(ba.BaseData.Translate.X)}, {F(ba.BaseData.Translate.Y)}, {F(ba.BaseData.Translate.Z)}],");
                sb.AppendLine($"      \"base_rotate\": [{F(ba.BaseData.Rotate.X)}, {F(ba.BaseData.Rotate.Y)}, {F(ba.BaseData.Rotate.Z)}, {F(ba.BaseData.Rotate.W)}],");
                sb.AppendLine("      \"curves\": [");
                for (int ci = 0; ci < ba.Curves.Count; ci++)
                {
                    AnimCurve c = ba.Curves[ci];
                    sb.AppendLine("        {");
                    sb.AppendLine($"          \"curve_type\": {(int)c.CurveType},");
                    sb.AppendLine($"          \"target_offset\": {c.AnimDataOffset},");
                    sb.AppendLine($"          \"start_frame\": {F(c.StartFrame)},");
                    sb.AppendLine($"          \"end_frame\": {F(c.EndFrame)},");
                    sb.AppendLine($"          \"scale\": {F(c.Scale)},");
                    sb.AppendLine($"          \"offset\": {F(c.Offset)},");
                    sb.AppendLine($"          \"delta\": {F(c.Delta)},");
                    sb.AppendLine($"          \"pre_wrap\": {(int)c.PreWrap},");
                    sb.AppendLine($"          \"post_wrap\": {(int)c.PostWrap},");
                    sb.AppendLine($"          \"frames\": [{string.Join(", ", c.Frames.Select(F))}],");
                    var keyRows = new List<string>();
                    for (int k = 0; k < c.Keys.GetLength(0); k++)
                    {
                        var elems = new List<string>();
                        for (int e = 0; e < c.Keys.GetLength(1); e++)
                            elems.Add(F(c.Keys[k, e]));
                        keyRows.Add($"[{string.Join(", ", elems)}]");
                    }
                    sb.AppendLine($"          \"keys\": [{string.Join(", ", keyRows)}]");
                    sb.AppendLine(ci == ba.Curves.Count - 1 ? "        }" : "        },");
                }
                sb.AppendLine("      ]");
                sb.AppendLine(bi == anim.BoneAnims.Count - 1 ? "    }" : "    },");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"[ExportTestBench] Exported anim '{anim.Name}' ({anim.BoneAnims.Count} bone anims, {anim.FrameCount} frames) -> {outPath}");

            static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Exports every <c>SkeletalAnim</c> from one already-parsed anim archive, skipping any
        /// name already in <paramref name="alreadyExported"/> (the same anim can legitimately
        /// appear in more than one candidate archive for an actor with several
        /// <c>AnimationResources</c> entries - first one found wins).
        /// </summary>
        /// <param name="patternTextures">Collects every texture a texture pattern anim can select, for the caller to export out of <c>TexToGo/</c> - by definition these are NOT the textures a material currently binds, so nothing else pulls them in.</param>
        static void ExportAnimsFromResFile(string romfsRoot, ResFile animResFile, string modelName, string outDir,
            HashSet<string> alreadyExported, HashSet<string> patternTextures)
        {
            ExportTexturePatternAnim.ExportFrom(romfsRoot, animResFile, modelName, outDir, alreadyExported, patternTextures);

            foreach (SkeletalAnim anim in animResFile.SkeletalAnims.Values)
            {
                if (!alreadyExported.Add(anim.Name))
                    continue;
                string safeAnim = anim.Name.Replace(":", "_").Replace("/", "_");
                ExportSkeletalAnim(anim, Path.Combine(outDir, $"{modelName}.{safeAnim}.anim.json"));
            }
        }

        /// <summary>
        /// Decompresses and parses one <c>Model/&lt;pack&gt;.anim.bfres.zs</c> archive, or returns
        /// null (with a log line, not an exception) if it's missing/unreadable. Unlike the model's
        /// own <c>.bfres.mc</c> (a bespoke MCPK/MeshCodec container - see
        /// <see cref="TestMaterialDump"/>), a <c>.anim.bfres.zs</c> is PLAIN zstd (its own
        /// <c>.zs</c> suffix is romfs's usual convention for that), so this decompresses it
        /// directly via <c>TotkCommon</c> rather than shelling out to MeshCodec.
        /// </summary>
        /// <summary>The same archive load <see cref="ExportAnimArchives"/> uses, exposed for <see cref="MaterialAnimInspector"/> - which needs the exact bytes the exporter would see, not a second decompression path that could disagree with it.</summary>
        public static ResFile? LoadAnimArchiveForInspection(string path) => LoadAnimArchive(path);

        static ResFile? LoadAnimArchive(string path)
        {
            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch (IOException) { return null; }

            byte[] decompressed;
            try
            {
                decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExportTestBench] SKIPPED anim archive {path}: decompress failed ({ex.Message})");
                return null;
            }

            try
            {
                using var ms = new MemoryStream(decompressed);
                return new ResFile(ms, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExportTestBench] SKIPPED anim archive {path}: not a readable BFRES ({ex.Message})");
                return null;
            }
        }

        /// <summary>
        /// The AUTHORITATIVE path: export exactly the anim archives an actor's own
        /// <c>Component/AnimationParam</c> names (<see cref="ActorInfo.Resolve"/>'s
        /// <c>AnimPackNames</c>) - no guessing at all.
        /// </summary>
        public static void ExportAnimArchives(string romfsRoot, IEnumerable<string> animPackNames, string modelName, string outDir,
            HashSet<string> alreadyExported, HashSet<string> patternTextures)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;
            foreach (string pack in animPackNames)
            {
                string path = Path.Combine(romfsRoot, "Model", $"{pack}.anim.bfres.zs");
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[ExportTestBench] Actor named anim archive '{pack}.anim.bfres.zs' not found under Model/ - skipping.");
                    continue;
                }
                if (LoadAnimArchive(path) is { } animResFile)
                    ExportAnimsFromResFile(romfsRoot, animResFile, modelName, outDir, alreadyExported, patternTextures);
            }
        }

        /// <summary>
        /// FALLBACK for a model with no actor pack to ask (see <see cref="ActorInfo"/>) - guesses
        /// candidate archives by the model's own pack-name prefix (<c>&lt;Pack&gt;.anim.bfres.zs</c>,
        /// <c>&lt;Pack&gt;_Animation.anim.bfres.zs</c>, ...) under the same <c>Model/</c> folder.
        /// Romfs's per-cutscene <c>Dm_XX_...anim.bfres.zs</c> files are named completely
        /// differently and so are naturally excluded by this prefix match, without needing an
        /// explicit denylist - but a genuinely mis-guessed prefix could still miss real anims a
        /// resolved actor pack would have named exactly, which is why this is the fallback, not
        /// the primary path.
        /// </summary>
        public static void ExportExternalAnims(string romfsRoot, string modelName, string outDir,
            HashSet<string> alreadyExported, HashSet<string> patternTextures)
        {
            string pack = modelName.Split('.')[0];
            string modelDir = Path.Combine(romfsRoot, "Model");
            if (!Directory.Exists(modelDir))
                return;

            TotkCommon.Totk.Config.GamePath = romfsRoot;
            foreach (string path in Directory.EnumerateFiles(modelDir, $"{pack}*.anim.bfres.zs"))
            {
                if (LoadAnimArchive(path) is { } animResFile)
                    ExportAnimsFromResFile(romfsRoot, animResFile, modelName, outDir, alreadyExported, patternTextures);
            }
        }

        /// <param name="animPackNames">
        /// Exact anim archive pack names to export from (see <see cref="ActorInfo.Resolve"/>'s
        /// <c>AnimPackNames</c>) - null or empty falls back to guessing from <paramref
        /// name="modelName"/>'s own pack prefix (<see cref="ExportExternalAnims"/>).
        /// </param>
        public static void ExportModel(string romfsRoot, string modelName, string outDir, IReadOnlyList<string>? animPackNames = null)
        {
            string? mcPath = RomfsPaths.ModelFile(romfsRoot, modelName);
            if (mcPath == null)
            {
                Console.WriteLine($"[ExportTestBench] {RomfsPaths.Explain(romfsRoot, modelName)}");
                return;
            }
            Console.WriteLine($"[ExportTestBench] Decompressing {mcPath}...");
            byte[] fres = TestMaterialDump.DecompressBfresMc(mcPath);

            using var ms = new MemoryStream(fres);
            var resFile = new ResFile(ms, false);
            var model = resFile.Models[0];

            ExportSkeleton(model.Skeleton, Path.Combine(outDir, $"{modelName}.skeleton.json"));
            var exportedAnimNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SkeletalAnim anim in resFile.SkeletalAnims.Values)
            {
                string safeAnim = anim.Name.Replace(":", "_").Replace("/", "_");
                ExportSkeletalAnim(anim, Path.Combine(outDir, $"{modelName}.{safeAnim}.anim.json"));
                exportedAnimNames.Add(anim.Name);
            }
            // A texture pattern anim selects textures the materials do NOT currently bind, so the
            // texture export at the bottom of this method has to be told about them explicitly.
            var patternTextures = new HashSet<string>(StringComparer.Ordinal);
            var exportedPatternNames = new HashSet<string>(StringComparer.Ordinal);
            ExportTexturePatternAnim.ExportFrom(romfsRoot, resFile, modelName, outDir, exportedPatternNames, patternTextures);

            if (animPackNames is { Count: > 0 })
                ExportAnimArchives(romfsRoot, animPackNames, modelName, outDir, exportedAnimNames, patternTextures);
            else
                ExportExternalAnims(romfsRoot, modelName, outDir, exportedAnimNames, patternTextures);

            // Only a genuinely rigid shape (VertexSkinCount == 0 - no per-vertex bone index in
            // BFRES at all, just one Shape.BoneIndex for the whole shape) compiles to a shader
            // with NO _Mtx uniform block and NO blend attributes whatsoever - confirmed via the
            // real decompiled output for both ends of the scale: Weapon_Sword_070 (skin 0) has
            // neither; Animal_Pao's Eyeball (skin 1, index only, no weight - the compiler omits
            // the multiply when there's nothing to blend) and Body (skin 4, index AND weight) both
            // declare _Mtx and read it for real. So skin 0 is the ONLY case with nowhere else for
            // its bone offset to come from - GsysShape.cTransform (ShapeMatrixUbo) is rebuilt once
            // per FRAME from the viewer's own model rotation, never per-shape - and must be BAKED
            // into its exported positions; skin >= 1 always gets a real per-vertex PALETTE-SLOT
            // index (see BonePaletteUbo's remarks) for the GPU to skin itself every frame instead.
            Matrix4x4[] world = BoneWorldMatrices(model.Skeleton);

            for (int s = 0; s < model.Shapes.Count; s++)
            {
                var shape = model.Shapes[s];
                var vb = model.VertexBuffers[shape.VertexBufferIndex];
                var mesh = shape.Meshes[0];
                var mat = model.Materials[shape.MaterialIndex];

                Console.WriteLine($"[ExportTestBench] Shape: {shape.Name} Material: {mat.Name} Vertices: {vb.VertexCount} Indices: {mesh.IndexCount}");

                var helper = new VertexBufferHelper(vb, resFile.ByteOrder);
                var positions = helper["_p0"]?.Data;
                var boneIdx0 = helper.Contains("_i0") ? helper["_i0"].Data : null;
                var boneIdx1 = helper.Contains("_i1") ? helper["_i1"].Data : null;
                var weight0 = helper.Contains("_w0") ? helper["_w0"].Data : null;
                var weight1 = helper.Contains("_w1") ? helper["_w1"].Data : null;

                // How this shape's vertices get posed (see the remark above the `world` build):
                //   skin count 0 (rigid)       - every vertex shares one BAKED matrix: world[Shape.BoneIndex] directly
                //                                 (bone-local positions, no inverse-bind - confirmed via Ghidra).
                //   skin count >= 1            - real GPU skin. _i0/_i1 are ABSOLUTE indices into the combined
                //                                 smooth+rigid palette that CalculateSkeleton fills, in BOTH the
                //                                 single-bind and the smooth case - not bone ids, and not indices
                //                                 into Shape.SkinBoneIndices. A single-bind shape's _i0.x equals the
                //                                 target bone's own Bone.RigidMatrixIndex, and that field is itself
                //                                 already offset past the smooth segment (Animal_Bass: 4 smooth slots,
                //                                 Head reports RigidMatrixIndex 4, MatrixToBoneList[4] == Head). So
                //                                 nothing may add smoothCount to these values - doing so pushed every
                //                                 single-bind vertex past the end of the real palette into whatever the
                //                                 identity fill left there.
                bool rigidBake = shape.VertexSkinCount == 0;
                Matrix4x4 rigid = rigidBake && shape.BoneIndex < world.Length ? world[shape.BoneIndex] : Matrix4x4.Identity;

                var normals = helper.Contains("_n0") ? helper["_n0"].Data : null;
                var uvs = helper.Contains("_u0") ? helper["_u0"].Data : null;
                var tangents = helper.Contains("_t0") ? helper["_t0"].Data : null;

                var colors0 = helper.Contains("_c0") ? helper["_c0"].Data : null;
                var uvs1 = helper.Contains("_u1") ? helper["_u1"].Data : null;

                uint[] indices = mesh.GetIndices().ToArray();

                string safeName = shape.Name.Replace(":", "_").Replace("/", "_");
                string vtxFile = Path.Combine(outDir, $"{safeName}_vtx.bin");
                using (var bw = new BinaryWriter(File.Create(vtxFile)))
                {
                    for (int i = 0; i < vb.VertexCount; i++)
                    {
                        // The bake matrix for THIS vertex - identity unless this is the one skin
                        // count (0, rigid) that actually gets baked; see the per-shape resolution
                        // above for why skin count 1 and >= 2 are never baked.
                        Matrix4x4 m = rigidBake ? rigid : Matrix4x4.Identity;

                        // 1. Position (vec4: x, y, z, 1.0)
                        var p0 = new Vector3(positions != null ? positions[i].X : 0f,
                                             positions != null ? positions[i].Y : 0f,
                                             positions != null ? positions[i].Z : 0f);
                        p0 = Vector3.Transform(p0, m);
                        bw.Write(p0.X); bw.Write(p0.Y); bw.Write(p0.Z); bw.Write(1.0f);

                        // 2. Normal (vec4: x, y, z, 0.0). Direction, so translation must not
                        // apply - TransformNormal uses the upper 3x3 only.
                        var n0 = new Vector3(normals != null ? normals[i].X : 0f,
                                             normals != null ? normals[i].Y : 1f,
                                             normals != null ? normals[i].Z : 0f);
                        n0 = Vector3.Normalize(Vector3.TransformNormal(n0, m));
                        bw.Write(n0.X); bw.Write(n0.Y); bw.Write(n0.Z); bw.Write(0.0f);

                        // 3. Tangent (vec4: x, y, z, w) - w is the bitangent sign, not a coord.
                        var t0 = new Vector3(tangents != null ? tangents[i].X : 1f,
                                             tangents != null ? tangents[i].Y : 0f,
                                             tangents != null ? tangents[i].Z : 0f);
                        t0 = Vector3.Normalize(Vector3.TransformNormal(t0, m));
                        float tw = tangents != null ? tangents[i].W : 1f;
                        bw.Write(t0.X); bw.Write(t0.Y); bw.Write(t0.Z); bw.Write(tw);

                        // 4. u254 (vec4 dummy)
                        bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(0f);

                        // 5-6. Bone weights (vec4 x 2) - real per-vertex weights for a smooth
                        // shape that has them; full weight on slot 0 otherwise (skin count 1's
                        // compiled shader has no weight attribute at all - implicit 1.0 - and a
                        // baked rigid shape's palette slot below is 0/unused, so the value here
                        // doesn't matter but stays a harmless identity-at-bind-pose default).
                        float w0x = weight0 != null ? weight0[i].X : 1f;
                        float w0y = weight0 != null ? weight0[i].Y : 0f;
                        float w0z = weight0 != null ? weight0[i].Z : 0f;
                        float w0w = weight0 != null ? weight0[i].W : 0f;
                        float w1x = weight1 != null ? weight1[i].X : 0f;
                        float w1y = weight1 != null ? weight1[i].Y : 0f;
                        float w1z = weight1 != null ? weight1[i].Z : 0f;
                        float w1w = weight1 != null ? weight1[i].W : 0f;
                        bw.Write(w0x); bw.Write(w0y); bw.Write(w0z); bw.Write(w0w);
                        bw.Write(w1x); bw.Write(w1y); bw.Write(w1z); bw.Write(w1w);

                        // 7-8. Bone indices (ivec4 x 2) - real PALETTE slots for skin count >= 1,
                        // copied through verbatim: BFRES already stores absolute indices into the
                        // combined smooth+rigid palette for both single-bind and smooth shapes (see
                        // the per-shape resolution above). Unused padding for a baked rigid shape
                        // (skin count 0), whose compiled shader has no _Mtx at all to read this from.
                        int i0x = 0, i0y = 0, i0z = 0, i0w = 0, i1x = 0, i1y = 0, i1z = 0, i1w = 0;
                        if (!rigidBake)
                        {
                            if (boneIdx0 != null) { i0x = (int)boneIdx0[i].X; i0y = (int)boneIdx0[i].Y; i0z = (int)boneIdx0[i].Z; i0w = (int)boneIdx0[i].W; }
                            if (boneIdx1 != null) { i1x = (int)boneIdx1[i].X; i1y = (int)boneIdx1[i].Y; i1z = (int)boneIdx1[i].Z; i1w = (int)boneIdx1[i].W; }
                        }
                        bw.Write(i0x); bw.Write(i0y); bw.Write(i0z); bw.Write(i0w);
                        bw.Write(i1x); bw.Write(i1y); bw.Write(i1z); bw.Write(i1w);

                        // 9. vTexCoords0 (vec2)
                        float u0 = uvs != null ? uvs[i].X : 0f;
                        float v0 = uvs != null ? uvs[i].Y : 0f;
                        bw.Write(u0); bw.Write(v0);

                        // 10. vTexCoords1 (vec2)
                        float u1 = uvs1 != null ? uvs1[i].X : u0;
                        float v1 = uvs1 != null ? uvs1[i].Y : v0;
                        bw.Write(u1); bw.Write(v1);

                        // 11. vTexCoords2 (vec2)
                        bw.Write(u0); bw.Write(v0);

                        // 12. vTexCoords3 (vec2)
                        bw.Write(u0); bw.Write(v0);

                        // 13. vColor0 (vec4)
                        float c0r = colors0 != null ? colors0[i].X : 1.0f;
                        float c0g = colors0 != null ? colors0[i].Y : 1.0f;
                        float c0b = colors0 != null ? colors0[i].Z : 1.0f;
                        float c0a = colors0 != null ? colors0[i].W : 1.0f;
                        bw.Write(c0r); bw.Write(c0g); bw.Write(c0b); bw.Write(c0a);

                        // 14. vColor1 (vec4)
                        bw.Write(1.0f); bw.Write(1.0f); bw.Write(1.0f); bw.Write(1.0f);
                    }
                }

                string idxFile = Path.Combine(outDir, $"{safeName}_idx.bin");
                using (var bw = new BinaryWriter(File.Create(idxFile)))
                {
                    foreach (uint idx in indices)
                        bw.Write((uint)idx);
                }

                // Shape UBO (128 bytes)
                byte[] shapeUbo = GsysUniformBuffers.BuildGsysShape(shape);
                File.WriteAllBytes(Path.Combine(outDir, $"{safeName}_shape.bin"), shapeUbo);

                // Context UBO (240 bytes)
                if (shape.SubMeshBoundings.Count > 0)
                {
                    var b = shape.SubMeshBoundings[0];
                    var center = new GsysUniformBuffers.Vector3(b.Center.X, b.Center.Y, b.Center.Z);
                    float radius = MathF.Sqrt(b.Extent.X * b.Extent.X + b.Extent.Y * b.Extent.Y + b.Extent.Z * b.Extent.Z);
                    byte[] ctxUbo = GsysUniformBuffers.BuildGsysContext(center, radius);
                    File.WriteAllBytes(Path.Combine(outDir, $"{safeName}_context.bin"), ctxUbo);
                }

                // Material options dump
                using (var sw = new StreamWriter(Path.Combine(outDir, $"{safeName}_options.txt")))
                {
                    foreach (var opt in mat.ShaderAssign.ShaderOptions)
                        sw.WriteLine($"{opt.Key}={opt.Value}");
                }

                Console.WriteLine($"  Exported {safeName}: {vb.VertexCount} verts -> {vtxFile}, {indices.Length} idxs -> {idxFile}");
            }

            // Textures referenced across all materials, plus every alternate a texture pattern anim
            // can swap in (see ExportTexturePatternAnim) - those are never in a material's own
            // TextureRefs, which is exactly why they need adding here by hand.
            var texNames = new HashSet<string>();
            foreach (var m in model.Materials.Values)
            {
                foreach (var tr in m.TextureRefs)
                    texNames.Add(tr.Name);
            }
            int patternOnly = patternTextures.Count(t => !texNames.Contains(t));
            texNames.UnionWith(patternTextures);
            if (patternOnly > 0)
                Console.WriteLine($"[ExportTestBench] {patternOnly} extra texture(s) needed only by texture pattern anims.");

            foreach (var tname in texNames)
            {
                string txtgPath = Path.Combine(romfsRoot, "TexToGo", tname + ".txtg");
                if (!File.Exists(txtgPath))
                {
                    Console.WriteLine($"[ExportTestBench] Texture not found: {txtgPath}");
                    continue;
                }
                // One texture in an unsupported format must not abort the whole model. The
                // manifest records an empty file for it and the bench simply skips that binding,
                // which costs one texture rather than every shape.
                try
                {
                    var tex = TxtgTexture.Load(txtgPath);
                    var surf = tex.Surfaces[0];
                    string outTex = Path.Combine(outDir, $"{tname}_{tex.Width}x{tex.Height}_{tex.Format}.bin");
                    File.WriteAllBytes(outTex, surf.Data);
                    Console.WriteLine($"[ExportTestBench] Exported texture {tname}: {tex.Width}x{tex.Height} {tex.Format} -> {outTex} ({surf.Data.Length} bytes)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ExportTestBench] SKIPPED texture {tname}: {ex.Message}");
                }
            }
        }
    }
}
