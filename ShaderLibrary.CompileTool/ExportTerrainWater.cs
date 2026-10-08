using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BfresLibrary;
using EffectLibraryTest;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// The game's terrain water: <c>Shader/terrain_water</c>'s G-buffer program, the
    /// <c>Model/Terrain.TeraWater</c> material it is drawn with, and that material's textures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Program 98 is the ordinary water surface (<c>o_material_behave</c> 103, lit by
    /// <c>field_water</c>; drawn over a copy of the lit scene, which it refracts). Its vertex stage
    /// is a patch scheme like the terrain's: a unit grid placed by <c>TerrainNode</c> (binding 12),
    /// the water height, flow and water type from <c>tera_water</c>, and per water type six colours
    /// from <c>_a0</c> - <c>WaterAlb</c>, a 14-texel-wide lookup with a slice per water type - when
    /// <c>TerrainSystem[11].z</c> is set. Its texture coordinates are world X/Z through the
    /// material's texture matrices (<c>gsys_material</c> slots 0-11), which the material's own
    /// animation scrolls.
    /// </para>
    /// <para>
    /// Its fragment stage reads <c>WaterNrm</c> - twelve slices, one per water type, through
    /// <c>_s0</c>/<c>_n0</c>/<c>_t0</c>/<c>_a1</c> - and <c>WaterEmm</c> (<c>_e0</c>), at the water
    /// type's slice.
    /// </para>
    /// </remarks>
    public static class ExportTerrainWater
    {
        public const int Program = 98;
        public const string ProgramName = "terrain_water_prog98";
        public const string MaterialName = "TranslucentNear";
        public const string MaterialFile = "terrain_water_material.bin";
        public const string TexturesFile = "terrain_water_textures.json";

        public static bool IsExported(string shadersDir) =>
            File.Exists(Path.Combine(shadersDir, TexturesFile))
            && File.Exists(Path.Combine(shadersDir, ProgramName + "_extracted.frag"));

        public static void Run(string romfsRoot, string shadersDir)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;
            Directory.CreateDirectory(shadersDir);

            string archive = Directory.GetFiles(Path.Combine(romfsRoot, "Shader"), "terrain_water.*bfsha*").First();
            byte[] raw = File.ReadAllBytes(archive);
            byte[] plain = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var bfsha = new BfshaFile(new MemoryStream(plain));
            var sm = bfsha.ShaderModels["terrain_water"];

            var bin = sm.GetVariation(Program).BinaryProgram;
            foreach (var (code, reflect, ext) in new[] { (bin.VertexShader, bin.VertexShaderReflection, "vert"), (bin.FragmentShader, bin.FragmentShaderReflection, "frag") })
            {
                string target = Path.Combine(shadersDir, $"{ProgramName}_extracted.{ext}");
                File.WriteAllText(target + ".tmp", ShaderExtract.GetCode(code, reflect));
                File.Move(target + ".tmp", target, overwrite: true);
            }

            // The material's gsys_material, built against this shading model's own block layout.
            string model = RomfsPaths.ModelFile(romfsRoot, "Terrain.TeraWater")
                ?? throw new FileNotFoundException("Model/Terrain.TeraWater.bfres.mc");
            byte[] fres = TestMaterialDump.DecompressBfresMc(model);
            var resFile = new ResFile(new MemoryStream(fres), false);
            Material material = resFile.Models[0].Materials.Values.First(m => m.Name == MaterialName);
            var block = sm.UniformBlocks["gsys_material"];
            File.WriteAllBytes(Path.Combine(shadersDir, MaterialFile), BuildMaterialUbo.BuildMaterialBlock(block, material));
            BuildMaterialUbo.WriteParamLayout(block, material, Path.Combine(shadersDir, "terrain_water_material.params.json"));

            // Every slice of each texture the program reads, mip 0, back to back.
            var textures = new List<object>();
            foreach (string name in new[] { "WaterAlb", "WaterNrm", "WaterEmm" })
            {
                string? path = TexToGo.Find(romfsRoot, name);
                if (path is null)
                {
                    Console.WriteLine($"[ExportTerrainWater] texture not found: {name}");
                    continue;
                }
                var tex = TexToGo.LoadAllSlices(path);
                string file = $"terrain_water_{name}_{tex.Width}x{tex.Height}x{tex.ArrayCount}_{tex.Format}.bin";
                using (var stream = File.Create(Path.Combine(shadersDir, file)))
                    foreach (var surface in tex.Surfaces)
                        stream.Write(surface.Data);
                textures.Add(new { name, file, format = tex.Format.ToString(), width = tex.Width, height = tex.Height, layers = tex.ArrayCount });
                Console.WriteLine($"[ExportTerrainWater] {name}: {tex.Width}x{tex.Height}, {tex.ArrayCount} slice(s), {tex.Format}");
            }
            File.WriteAllText(Path.Combine(shadersDir, TexturesFile), JsonSerializer.Serialize(textures));
        }
    }
}
