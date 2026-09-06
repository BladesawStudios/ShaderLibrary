using System;
using System.IO;
using System.Linq;
using System.Numerics;
using BfresLibrary;
using BfresLibrary.Helpers;
using ShaderLibrary.CompileTool;

namespace ShaderLibrary.CompilerTool
{
    public static class ModelInspector
    {
        public static void InspectSkeleton(string romfs, string modelName)
        {
            ExternalBinaryStringTable.RomfsRoot = romfs;
            BfresLibraryPatches.EnsureApplied();

            string? mc = RomfsPaths.ModelFile(romfs, modelName);
            if (mc == null)
            {
                Console.WriteLine(RomfsPaths.Explain(romfs, modelName));
                return;
            }
            byte[] fres = TestMaterialDump.DecompressBfresMc(mc);
            var res = new ResFile(new MemoryStream(fres), false);
            Console.WriteLine("==================================================");
            Console.WriteLine($"MODEL: {modelName} (FRES size: {fres.Length})");
            var model = res.Models[0];
            var skel = model.Skeleton;

            Matrix4x4[] world = ExportTestBench.BoneWorldMatrices(skel);
            Console.WriteLine($"\n--- SKELETON ({skel.BoneList.Count} bones, MatrixToBoneList={skel.MatrixToBoneList?.Count ?? 0}) ---");
            for (int i = 0; i < skel.BoneList.Count; i++)
            {
                var b = skel.BoneList[i];
                Console.WriteLine($"  Bone[{i:D2}]: \"{b.Name}\" Parent={b.ParentIndex} SmoothIdx={b.SmoothMatrixIndex} RigidIdx={b.RigidMatrixIndex} Pos=({b.Position.X:F3}, {b.Position.Y:F3}, {b.Position.Z:F3}) Rot=({b.Rotation.X:F3}, {b.Rotation.Y:F3}, {b.Rotation.Z:F3}, {b.Rotation.W:F3}) RotFlag={b.FlagsRotation}");
            }
            if (skel.MatrixToBoneList != null)
                Console.WriteLine($"MatrixToBoneList: [{string.Join(", ", skel.MatrixToBoneList)}]");

            int eyeBone = skel.BoneList.FindIndex(b => b.Name.Contains("Eye", StringComparison.OrdinalIgnoreCase));
            if (eyeBone >= 0)
            {
                Console.WriteLine($"\n--- EYEBALL BONE TRANSFORM ---");
                Console.WriteLine($"Eye bone index: {eyeBone} (\"{skel.BoneList[eyeBone].Name}\")");
                int curr = eyeBone;
                while (curr >= 0)
                {
                    var b = skel.BoneList[curr];
                    Console.WriteLine($"  Bone[{curr:D2}]: \"{b.Name}\" Pos=({b.Position.X:F3}, {b.Position.Y:F3}, {b.Position.Z:F3}) Rot=({b.Rotation.X:F3}, {b.Rotation.Y:F3}, {b.Rotation.Z:F3}, {b.Rotation.W:F3}) RotFlag={b.FlagsRotation}");
                    curr = b.ParentIndex;
                }
                Matrix4x4 eyeMtx = world[eyeBone];
                Console.WriteLine($"Eye World Matrix Translation: ({eyeMtx.M41:F3}, {eyeMtx.M42:F3}, {eyeMtx.M43:F3})");

                var eyeShape = model.Shapes.Values.FirstOrDefault(s => s.Name.Contains("Eye", StringComparison.OrdinalIgnoreCase));
                if (eyeShape != null)
                {
                    var eyeVb = model.VertexBuffers[eyeShape.VertexBufferIndex];
                    var eyeHelper = new VertexBufferHelper(eyeVb, res.ByteOrder);
                    var eyeRawP = eyeHelper["_p0"].Data;
                    var eyeTransformed = eyeRawP.Select(p => Vector3.Transform(new Vector3(p.X, p.Y, p.Z), eyeMtx)).ToList();
                    float eMinX = eyeTransformed.Min(v => v.X), eMaxX = eyeTransformed.Max(v => v.X);
                    float eMinY = eyeTransformed.Min(v => v.Y), eMaxY = eyeTransformed.Max(v => v.Y);
                    float eMinZ = eyeTransformed.Min(v => v.Z), eMaxZ = eyeTransformed.Max(v => v.Z);
                    Console.WriteLine($"Transformed Eyeball with Bone[{eyeBone}]: X=[{eMinX:F3}, {eMaxX:F3}] Y=[{eMinY:F3}, {eMaxY:F3}] Z=[{eMinZ:F3}, {eMaxZ:F3}]");
                }
            }

            Console.WriteLine($"\n--- SHAPES ({model.Shapes.Count} shapes) ---");
            for (int s = 0; s < model.Shapes.Count; s++)
            {
                var shape = model.Shapes[s];
                var vb = model.VertexBuffers[shape.VertexBufferIndex];
                var mat = model.Materials[shape.MaterialIndex];
                var helper = new VertexBufferHelper(vb, res.ByteOrder);

                Console.WriteLine($"Shape[{s}]: \"{shape.Name}\" | Mat: \"{mat.Name}\" | SkinCount: {shape.VertexSkinCount} | BoneIndex: {shape.BoneIndex}");
                if (shape.SkinBoneIndices != null)
                {
                    Console.WriteLine($"  SkinBoneIndices ({shape.SkinBoneIndices.Count}): [{string.Join(", ", shape.SkinBoneIndices)}]");
                }
                if (helper.Contains("_i0"))
                {
                    var i0 = helper["_i0"].Data;
                    var uniqueX = i0.Select(v => (int)v.X).Distinct().OrderBy(x => x).ToList();
                    Console.WriteLine($"  _i0.X unique raw: [{string.Join(", ", uniqueX)}]");
                    if (skel.MatrixToBoneList != null)
                    {
                        var mappedBones = uniqueX.Select(x => (x >= 0 && x < skel.MatrixToBoneList.Count) ? skel.MatrixToBoneList[x] : -1).Distinct().OrderBy(b => b).ToList();
                        Console.WriteLine($"  _i0.X -> MatrixToBoneList -> BoneIndices: [{string.Join(", ", mappedBones)}]");
                        foreach (var b in mappedBones)
                        {
                            string bname = (b >= 0 && b < skel.BoneList.Count) ? skel.BoneList[b].Name : "OUT_OF_RANGE";
                            bool inSkin = shape.SkinBoneIndices != null && shape.SkinBoneIndices.Contains((ushort)b);
                            Console.WriteLine($"    bone[{b}]: \"{bname}\" (in SkinBoneIndices: {inSkin})");
                        }
                    }
                }
                if (helper.Contains("_p0"))
                {
                    var p = helper["_p0"].Data;
                    float minX = p.Min(v => v.X), maxX = p.Max(v => v.X);
                    float minY = p.Min(v => v.Y), maxY = p.Max(v => v.Y);
                    float minZ = p.Min(v => v.Z), maxZ = p.Max(v => v.Z);
                    Console.WriteLine($"  Raw _p0: X=[{minX:F3}, {maxX:F3}] Y=[{minY:F3}, {maxY:F3}] Z=[{minZ:F3}, {maxZ:F3}] Count={p.Length}");
                }
            }
        }
    }
}

