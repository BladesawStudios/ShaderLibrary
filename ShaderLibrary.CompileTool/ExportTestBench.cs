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
        /// World-space matrix per bone, from the skeleton's local TRS walked down the hierarchy.
        ///
        /// WHY: a shape's vertex positions are NOT always in model space. BFRES stores them in
        /// the space implied by the shape's binding:
        ///   VertexSkinCount == 0  rigid       - positions are in BONE space (Shape.BoneIndex)
        ///   VertexSkinCount == 1  single-bind - positions are in BONE space (per-vertex index)
        ///   VertexSkinCount >= 2  smooth      - positions are already in model/bind space
        /// The bench treated everything as model space, so every rigid part of a model landed at
        /// the origin's orientation instead of where its bone puts it - horns, eyes and armour
        /// pieces scattered away from the body. Baking the bone matrix into the exported
        /// positions puts every shape in one common space, which is also what makes the viewer's
        /// model rotation apply uniformly.
        /// </summary>
        public static Matrix4x4[] BoneWorldMatrices(Skeleton skel)
        {
            var bones = skel.BoneList;
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

                Matrix4x4 local = Matrix4x4.CreateScale(b.Scale.X, b.Scale.Y, b.Scale.Z)
                                  * rot
                                  * Matrix4x4.CreateTranslation(b.Position.X, b.Position.Y, b.Position.Z);

                // Parents always precede children in a BFRES skeleton, so one forward pass is
                // enough - no recursion needed.
                world[i] = b.ParentIndex >= 0 && b.ParentIndex < i
                    ? local * world[b.ParentIndex]
                    : local;
            }
            return world;
        }

        /// <summary>
        /// Writes <c>&lt;modelName&gt;.skeleton.json</c> - everything Marrow's runtime needs to
        /// build the real gsys_skeleton palette itself (bind-pose local TRS per bone, plus the
        /// smooth/rigid matrix-list tables from <see cref="BfresLibrary.Skeleton"/>) instead of
        /// this exporter baking one fixed pose into vertex positions. Skipped (with a log line, not
        /// a failure) for a model with no bones at all - not every shape needs a skeleton.
        ///
        /// The real per-slot skinning matrix - <c>InverseModelMatrix * BoneWorld</c> for the
        /// smooth segment, <c>BoneWorld</c> directly (no inverse-bind multiply) for the rigid
        /// segment - is built at RUNTIME from this file by
        /// <c>Marrow.Shaders.Profiles.Totk.Ubos.BonePaletteUbo.Build</c> (see its remarks for the
        /// full Ghidra citation); nothing here computes a bone matrix any more.
        /// </summary>
        public static void ExportSkeleton(Skeleton skel, string outPath)
        {
            if (skel.BoneList.Count == 0)
            {
                Console.WriteLine("[ExportTestBench] No skeleton bones - skipping skeleton.json.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"bones\": [");
            for (int i = 0; i < skel.BoneList.Count; i++)
            {
                Bone b = skel.BoneList[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"name\": \"{b.Name}\",");
                sb.AppendLine($"      \"parent_index\": {b.ParentIndex},");
                sb.AppendLine($"      \"smooth_matrix_index\": {b.SmoothMatrixIndex},");
                sb.AppendLine($"      \"rigid_matrix_index\": {b.RigidMatrixIndex},");
                sb.AppendLine($"      \"scale\": [{F(b.Scale.X)}, {F(b.Scale.Y)}, {F(b.Scale.Z)}],");
                sb.AppendLine($"      \"rotation\": [{F(b.Rotation.X)}, {F(b.Rotation.Y)}, {F(b.Rotation.Z)}, {F(b.Rotation.W)}],");
                sb.AppendLine($"      \"rotation_is_quaternion\": {(b.FlagsRotation == BoneFlagsRotation.Quaternion ? "true" : "false")},");
                sb.AppendLine($"      \"position\": [{F(b.Position.X)}, {F(b.Position.Y)}, {F(b.Position.Z)}]");
                sb.AppendLine(i == skel.BoneList.Count - 1 ? "    }" : "    },");
            }
            sb.AppendLine("  ],");

            var matrixToBoneList = skel.MatrixToBoneList ?? [];
            sb.AppendLine($"  \"matrix_to_bone_list\": [{string.Join(", ", matrixToBoneList)}],");

            // Not every BFRES version stores one inverse-bind matrix per smooth slot in
            // Skeleton.InverseModelMatrices (this model's has fewer entries than
            // MatrixToBoneList, or is null outright for a model with no smooth skinning at all) -
            // for any slot missing one, derive it the only way that's always correct: the inverse
            // of that bone's OWN bind-pose world matrix (mathematically identical to what the real
            // InverseModelMatrices entry would be, since it exists specifically to undo that
            // bone's own bind pose).
            var inverseModelMatrices = skel.InverseModelMatrices ?? [];
            Matrix4x4[]? bindWorld = matrixToBoneList.Count > inverseModelMatrices.Count
                ? BoneWorldMatrices(skel) : null;

            sb.AppendLine("  \"inverse_model_matrices\": [");
            for (int i = 0; i < matrixToBoneList.Count; i++)
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
                    int bone = matrixToBoneList[i];
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
                sb.AppendLine(i == matrixToBoneList.Count - 1 ? "" : ",");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"[ExportTestBench] Exported skeleton ({skel.BoneList.Count} bones, {matrixToBoneList.Count} smooth matrices) -> {outPath}");

            static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Writes <c>&lt;modelName&gt;.&lt;AnimName&gt;.anim.json</c> - a faithful transcription of
        /// one embedded <see cref="SkeletalAnim"/>'s curves (already-decoded floats regardless of
        /// on-disk compression - see <c>AnimCurve.FrameType</c>/<c>KeyType</c>'s own remarks), for
        /// Marrow's runtime to evaluate itself every frame rather than this exporter baking one
        /// fixed pose. <c>AnimCurve.AnimDataOffset</c> says which bone TRS component each curve
        /// drives (confirmed via <see cref="BoneAnimDataOffset"/>'s own values: 4/8/12 =
        /// Scale.X/Y/Z, 16/20/24 = Translate.X/Y/Z, 32/36/40/44 = Rotate.X/Y/Z/W) - the curve math
        /// itself (Cubic/Linear/BakedFloat) is confirmed against the running game via Ghidra
        /// (<c>nn::g3d2::ResAnimCurve::Evaluate{Cubic,Linear,BakedFloat}&lt;float&gt;</c>) and lives
        /// in <c>Marrow.Core.Rendering.SkeletonPose</c>, not duplicated here.
        /// </summary>
        public static void ExportSkeletalAnim(SkeletalAnim anim, string outPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{anim.Name}\",");
            sb.AppendLine($"  \"frame_count\": {anim.FrameCount},");
            sb.AppendLine($"  \"loop\": {(anim.Loop ? "true" : "false")},");
            sb.AppendLine("  \"bone_anims\": [");
            for (int bi = 0; bi < anim.BoneAnims.Count; bi++)
            {
                BoneAnim ba = anim.BoneAnims[bi];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"bone_name\": \"{ba.Name}\",");
                sb.AppendLine($"      \"use_scale\": {(ba.UseScale ? "true" : "false")},");
                sb.AppendLine($"      \"use_rotate\": {(ba.UseRotation ? "true" : "false")},");
                sb.AppendLine($"      \"use_translate\": {(ba.UseTranslation ? "true" : "false")},");
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
                    sb.AppendLine($"          \"delta\": {F(c.Delta)},");
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
        static void ExportAnimsFromResFile(ResFile animResFile, string modelName, string outDir, HashSet<string> alreadyExported)
        {
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
        public static void ExportAnimArchives(string romfsRoot, IEnumerable<string> animPackNames, string modelName, string outDir, HashSet<string> alreadyExported)
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
                    ExportAnimsFromResFile(animResFile, modelName, outDir, alreadyExported);
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
        public static void ExportExternalAnims(string romfsRoot, string modelName, string outDir, HashSet<string> alreadyExported)
        {
            string pack = modelName.Split('.')[0];
            string modelDir = Path.Combine(romfsRoot, "Model");
            if (!Directory.Exists(modelDir))
                return;

            TotkCommon.Totk.Config.GamePath = romfsRoot;
            foreach (string path in Directory.EnumerateFiles(modelDir, $"{pack}*.anim.bfres.zs"))
            {
                if (LoadAnimArchive(path) is { } animResFile)
                    ExportAnimsFromResFile(animResFile, modelName, outDir, alreadyExported);
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
            if (animPackNames is { Count: > 0 })
                ExportAnimArchives(romfsRoot, animPackNames, modelName, outDir, exportedAnimNames);
            else
                ExportExternalAnims(romfsRoot, modelName, outDir, exportedAnimNames);

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
                //   skin count 1 (single-bind) - real GPU skin. The shape's own _i0.x is NOT a bone id and NOT an index
                //                                 into Shape.SkinBoneIndices (confirmed by dumping the real per-vertex
                //                                 values: they equal the target bone's own RigidMatrixIndex directly, e.g.
                //                                 464/465 for a model whose Eyeball_L/R bones have RigidMatrixIndex 464/465 -
                //                                 far past SkinBoneIndices' own length, and NOT the bone's raw index either).
                //                                 So the real palette slot is just smoothCount + _i0.x, no bone lookup at
                //                                 all. Using SkinBoneIndices/the bone id here (an earlier, untested
                //                                 assumption) silently fell through to slot 0 for every single-bind vertex,
                //                                 which happens to be near-identity at bind pose for both test models - the
                //                                 actual mechanism behind eyes rendering unrotated/unmoved ("inside the
                //                                 head", "rotated 90") instead of at their real bone.
                //   skin count >= 2 (smooth)   - real GPU skin, real per-vertex weights + PALETTE SLOT indices. The shape's
                //                                 own _i0/_i1 values ARE already smooth-segment slot numbers (MatrixToBoneList's
                //                                 own index space) - used as-is, no remap needed.
                bool rigidBake = shape.VertexSkinCount == 0;
                bool smooth = shape.VertexSkinCount >= 2;
                Matrix4x4 rigid = rigidBake && shape.BoneIndex < world.Length ? world[shape.BoneIndex] : Matrix4x4.Identity;

                int smoothCount = model.Skeleton.MatrixToBoneList?.Count ?? 0;

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

                        // 7-8. Bone indices (ivec4 x 2) - real PALETTE slots for skin count >= 1:
                        // smooth's own _i0/_i1 values ARE already smooth-segment slot numbers, used
                        // as-is; single-bind's own _i0.x is ALREADY that bone's RigidMatrixIndex
                        // directly (confirmed against real vertex data - not a bone id, not an
                        // index into SkinBoneIndices), so just offset by the smooth segment's
                        // length. Unused padding for a baked rigid shape (skin count 0), whose
                        // compiled shader has no _Mtx at all to read this from.
                        int i0x = 0, i0y = 0, i0z = 0, i0w = 0, i1x = 0, i1y = 0, i1z = 0, i1w = 0;
                        if (smooth)
                        {
                            if (boneIdx0 != null) { i0x = (int)boneIdx0[i].X; i0y = (int)boneIdx0[i].Y; i0z = (int)boneIdx0[i].Z; i0w = (int)boneIdx0[i].W; }
                            if (boneIdx1 != null) { i1x = (int)boneIdx1[i].X; i1y = (int)boneIdx1[i].Y; i1z = (int)boneIdx1[i].Z; i1w = (int)boneIdx1[i].W; }
                        }
                        else if (!rigidBake)
                        {
                            i0x = smoothCount + (boneIdx0 != null ? (int)boneIdx0[i].X : 0);
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

            // Textures referenced across all materials
            var texNames = new HashSet<string>();
            foreach (var m in model.Materials.Values)
            {
                foreach (var tr in m.TextureRefs)
                    texNames.Add(tr.Name);
            }

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
