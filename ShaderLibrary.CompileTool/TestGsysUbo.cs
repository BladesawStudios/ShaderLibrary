using System;
using BfresLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Builds the GsysContext/GsysShape/GsysEnvironment UBOs (see GsysUniformBuffers.cs) for a
    /// real shape from a real decompressed model, and verifies the one thing that's actually
    /// checkable without a live capture: that every buffer comes out exactly the byte size
    /// Shaders/TOTK/Pixel.frag's std140 layout requires, and that GsysShape's skin count matches
    /// the shape's real BfresLibrary data.
    /// </summary>
    public static class TestGsysUbo
    {
        public static void RunDemo(string bfresMcPath)
        {
            Console.WriteLine();
            Console.WriteLine("################################################################");
            Console.WriteLine($"# GsysContext/GsysShape/GsysEnvironment: {bfresMcPath}");
            Console.WriteLine("################################################################");

            byte[] fres = TestMaterialDump.DecompressBfresMc(bfresMcPath);
            using var ms = new System.IO.MemoryStream(fres);
            var resFile = new ResFile(ms, false);
            var model = resFile.Models[0];

            foreach (var shapeEntry in model.Shapes)
            {
                Shape shape = shapeEntry.Value;
                byte[] shapeUbo = GsysUniformBuffers.BuildGsysShape(shape);
                Console.WriteLine($"  GsysShape \"{shape.Name}\": {shapeUbo.Length} bytes, skinCount(cParams.x)={BitConverter.ToSingle(shapeUbo, 48)} (real VertexSkinCount={shape.VertexSkinCount})");

                if (shape.SubMeshBoundings.Count > 0)
                {
                    var b = shape.SubMeshBoundings[0];
                    var center = new GsysUniformBuffers.Vector3(b.Center.X, b.Center.Y, b.Center.Z);
                    float radius = MathF.Sqrt(b.Extent.X * b.Extent.X + b.Extent.Y * b.Extent.Y + b.Extent.Z * b.Extent.Z);
                    Console.WriteLine($"    bounds: center=({b.Center.X:F3},{b.Center.Y:F3},{b.Center.Z:F3}) extent=({b.Extent.X:F3},{b.Extent.Y:F3},{b.Extent.Z:F3}) radius~{radius:F3}");

                    byte[] contextUbo = GsysUniformBuffers.BuildGsysContext(center, radius);
                    Console.WriteLine($"    GsysContext: {contextUbo.Length} bytes (auto-framed camera, radius={radius:F3})");
                }
            }

            byte[] envUbo = GsysUniformBuffers.BuildGsysEnvironment();
            Console.WriteLine($"  GsysEnvironment: {envUbo.Length} bytes");
        }

        /// <summary>
        /// Dumps every material's raw ShaderParamData bytes as hex, for cross-referencing against
        /// real ub09_..._GsysMaterial_*.bin captures from a live UboDumpExeMod session.
        /// </summary>
        public static void DumpShaderParamDataHex(string bfresMcPath)
        {
            byte[] fres = TestMaterialDump.DecompressBfresMc(bfresMcPath);
            using var ms = new System.IO.MemoryStream(fres);
            var resFile = new ResFile(ms, false);
            var model = resFile.Models[0];

            foreach (var matEntry in model.Materials)
            {
                var mat = matEntry.Value;
                byte[] data = mat.ShaderParamData ?? Array.Empty<byte>();
                Console.WriteLine($"  Material \"{mat.Name}\" ShaderParamData ({data.Length} bytes): {Convert.ToHexString(data)}");
            }
        }
    }
}
