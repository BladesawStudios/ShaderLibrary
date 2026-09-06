using System;
using System.IO;
using System.Numerics;
using System.Linq;
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

        public static void ExportModel(string romfsRoot, string modelName, string outDir)
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
                var boneIdx = helper.Contains("_i0") ? helper["_i0"].Data : null;

                // Per-vertex transform into a common model space (see BoneWorldMatrices).
                Matrix4x4 rigid = Matrix4x4.Identity;
                bool perVertexBind = false;
                if (shape.VertexSkinCount == 0)
                {
                    if (shape.BoneIndex < world.Length)
                        rigid = world[shape.BoneIndex];
                }
                else if (shape.VertexSkinCount == 1)
                {
                    perVertexBind = Environment.GetEnvironmentVariable("NO_BAKE") != "1";
                }
                if (Environment.GetEnvironmentVariable("NO_BAKE") == "1")
                    rigid = Matrix4x4.Identity;
                if (Environment.GetEnvironmentVariable("DIAG_BAKE") == "1")
                {
                    Matrix4x4 dbg = rigid;
                    string via = "rigid";
                    if (perVertexBind)
                    {
                        int local = (boneIdx != null && boneIdx.Length > 0) ? (int)boneIdx[0].X : 0;
                        int bone = (model.Skeleton.MatrixToBoneList != null && local >= 0 && local < model.Skeleton.MatrixToBoneList.Count)
                            ? model.Skeleton.MatrixToBoneList[local]
                            : ((shape.SkinBoneIndices != null && local < shape.SkinBoneIndices.Count) ? shape.SkinBoneIndices[local] : local);
                        if (bone >= 0 && bone < world.Length) { dbg = world[bone]; via = $"matrix[{local}]->bone[{bone}]"; }
                    }
                    Console.WriteLine($"    [bake] skin={shape.VertexSkinCount} " +
                                      $"skinBones={(shape.SkinBoneIndices == null ? 0 : shape.SkinBoneIndices.Count)} " +
                                      $"via={via} T=({dbg.M41:F2},{dbg.M42:F2},{dbg.M43:F2})");
                }
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
                        // The bind matrix for THIS vertex. Rigid shapes share one; single-bind
                        // shapes carry a per-vertex matrix index in _i0, which indexes the skeleton's
                        // MatrixToBoneList table.
                        Matrix4x4 m = rigid;
                        if (perVertexBind)
                        {
                            int local = boneIdx != null ? (int)boneIdx[i].X : 0;
                            int bone = (model.Skeleton.MatrixToBoneList != null && local >= 0 && local < model.Skeleton.MatrixToBoneList.Count)
                                ? model.Skeleton.MatrixToBoneList[local]
                                : ((shape.SkinBoneIndices != null && local < shape.SkinBoneIndices.Count)
                                    ? shape.SkinBoneIndices[local] : local);
                            if (bone >= 0 && bone < world.Length)
                                m = world[bone];
                        }

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

                        // 5-6. Bone weights (vec4 x 2)
                        bw.Write(1f); bw.Write(0f); bw.Write(0f); bw.Write(0f);
                        bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(0f);

                        // 7-8. Bone indices (ivec4 x 2)
                        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

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
