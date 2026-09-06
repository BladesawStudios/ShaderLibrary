using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using BfresLibrary;
using EffectLibraryTest;
using ShaderLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Builds the gsys_material uniform block (the shader's "Mat" block, binding 5) for every
    /// material of a model, entirely from romfs data.
    ///
    /// WHY THIS EXISTS: the test bench had been feeding the deferred/G-Buffer shaders
    /// matsrc_*.bin files captured live from the running game, and those captures are known to be
    /// wrong - every one is exactly 416 bytes with only 5 distinct hashes across 255 files, which
    /// cannot be per-material data. Everything downstream (specular response, colour constants,
    /// feature toggles) was therefore driven by garbage.
    ///
    /// It is reconstructible offline because both halves are on disk:
    ///
    ///   * The BFSHA's gsys_material block declares the layout - its size, and a name -> byte
    ///     offset for each of its uniforms - plus a DefaultBuffer holding the values a material
    ///     gets when it does not override them.
    ///   * The BFRES material carries ShaderParamData (a raw blob) and a ShaderParams table
    ///     giving, per parameter, its Name, its Type (hence DataSize) and its DataOffset INTO
    ///     THAT BLOB.
    ///
    /// The two use different offsets - the blob is packed per-material, the block is the shader's
    /// layout - so they must be joined BY NAME, not copied wholesale. That is the whole job:
    /// start from the shader's defaults, then overlay each named parameter the material actually
    /// specifies at the offset the shader expects it.
    /// </summary>
    public static class BuildMaterialUbo
    {
        public static void Run(string bfshaPath, string bfresMcPath, string outDir)
        {
            Directory.CreateDirectory(outDir);
            Console.WriteLine("################################################################");
            Console.WriteLine("# Building gsys_material UBOs from romfs");
            Console.WriteLine($"#   shader:  {bfshaPath}");
            Console.WriteLine($"#   model:   {bfresMcPath}");
            Console.WriteLine("################################################################");

            var bfsha = new BfshaFile(bfshaPath);
            // The DeferredMain system model ships as a plain .bfres; only the game's own models
            // are MCPK-compressed .bfres.mc.
            byte[] fres = bfresMcPath.EndsWith(".mc", StringComparison.OrdinalIgnoreCase)
                ? TestMaterialDump.DecompressBfresMc(bfresMcPath)
                : File.ReadAllBytes(bfresMcPath);
            using var ms = new MemoryStream(fres);
            var resFile = new ResFile(ms, false);
            var model = resFile.Models[0];

            foreach (var matEntry in model.Materials)
            {
                Material mat = matEntry.Value;
                string shading = mat.ShaderAssign.ShadingModelName;
                if (!bfsha.ShaderModels.ContainsKey(shading))
                {
                    Console.WriteLine($"[skip] {mat.Name}: shading model \"{shading}\" not in this archive");
                    continue;
                }

                var sm = bfsha.ShaderModels[shading];
                if (!sm.UniformBlocks.ContainsKey("gsys_material"))
                {
                    Console.WriteLine($"[skip] {mat.Name}: shading model has no gsys_material block");
                    continue;
                }

                var block = sm.UniformBlocks["gsys_material"];
                byte[] buffer = BuildBlock(block, mat, out int matched, out int missing, out var missingNames);

                string safe = mat.Name.Replace(":", "_").Replace("/", "_");
                string path = Path.Combine(outDir, $"{safe}.gsys_material.bin");
                File.WriteAllBytes(path, buffer);
                WriteParamLayout(block, mat, Path.Combine(outDir, $"{safe}.params.json"));

                Console.WriteLine();
                Console.WriteLine($"-- {mat.Name} --");
                Console.WriteLine($"   block size {block.Size} B, {block.Uniforms.Count} declared uniforms, " +
                                  $"defaults {(block.DefaultBuffer == null ? "ABSENT" : block.DefaultBuffer.Length + " B")}");
                Console.WriteLine($"   material has {mat.ShaderParams.Count} ShaderParams, " +
                                  $"ShaderParamData {mat.ShaderParamData?.Length ?? 0} B");
                Console.WriteLine($"   -> matched {matched}, unmatched {missing}" +
                                  (missing > 0 ? $" ({string.Join(", ", missingNames.Take(8))}{(missing > 8 ? ", ..." : "")})" : ""));
                Console.WriteLine($"   -> {path}");
            }
        }

        /// <summary>
        /// Writes the sidecar every SHADER PARAMETER ANIMATION needs: which byte of the
        /// <c>gsys_material</c> block each named parameter lives at.
        ///
        /// A material anim addresses its target as (parameter NAME, byte offset WITHIN that
        /// parameter) - <c>ParamAnimInfo.Name</c> plus <c>AnimCurve.AnimDataOffset</c>, verified
        /// against real data: Enemy_Dragon_Darkness's <c>Face_Eye_Scroll_fts</c> drives
        /// <c>p_tex_srt1</c> at <c>animOffset 20</c>, which is byte 20 of a 24-byte TexSrt
        /// (mode:int, scaleX, scaleY, rotation, translateX, translateY) - the translate Y that
        /// scrolls the eye - with a constant at animOffset 0 setting the mode. So the anim never
        /// needs the material's own packed <c>ShaderParamData</c> layout at all; it needs THIS
        /// table, which is the only thing that knows where <c>p_tex_srt1</c> sits in the compiled
        /// block. Emitting the block's whole uniform table (not just the parameters this material
        /// happens to set) means an anim can drive a uniform the material left at its default.
        /// </summary>
        static void WriteParamLayout(BfshaUniformBlock block, Material mat, string outPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"material\": \"{mat.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\",");
            sb.AppendLine($"  \"block_size\": {block.Size},");
            sb.AppendLine("  \"uniforms\": [");
            var names = block.Uniforms.Keys.ToList();
            for (int i = 0; i < names.Count; i++)
            {
                var u = block.Uniforms[names[i]];
                int off = u.DataOffset == 0 ? u.Index * 4 : u.DataOffset - 1;
                sb.Append($"    {{ \"name\": \"{names[i]}\", \"offset\": {off} }}");
                sb.AppendLine(i == names.Count - 1 ? "" : ",");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(outPath, sb.ToString());
        }

        /// <summary>
        /// Start from the shader's DefaultBuffer, then overlay every material ShaderParam whose
        /// name the block declares. Parameters the block does not declare are skipped and
        /// reported rather than written somewhere arbitrary - a material can legitimately carry
        /// author-side parameters the compiled variant does not consume.
        /// </summary>
        static byte[] BuildBlock(BfshaUniformBlock block, Material mat,
                                 out int matched, out int missing, out List<string> missingNames)
        {
            int size = block.Size;
            byte[] buffer = new byte[size];
            if (block.DefaultBuffer != null && block.DefaultBuffer.Length > 0)
                Array.Copy(block.DefaultBuffer, buffer, Math.Min(block.DefaultBuffer.Length, size));

            // BFSHA stores DataOffset 1-based; 0 means "derive from Index * 4". Same convention
            // TestSystemShading.DumpUniformBlocks and ShaderLabelUtil.GetUniformLabels use.
            var blockOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var u in block.Uniforms)
            {
                int off = u.Value.DataOffset == 0 ? u.Value.Index * 4 : u.Value.DataOffset - 1;
                blockOffsets[u.Key] = off;
            }

            matched = 0;
            missing = 0;
            missingNames = new List<string>();
            byte[] src = mat.ShaderParamData ?? Array.Empty<byte>();

            foreach (var pEntry in mat.ShaderParams)
            {
                ShaderParam p = pEntry.Value;
                string name = pEntry.Key;

                if (!blockOffsets.TryGetValue(name, out int dstOff))
                {
                    missing++;
                    missingNames.Add(name);
                    continue;
                }

                int len = (int)p.DataSize;
                int srcOff = p.DataOffset;
                if (srcOff < 0 || srcOff + len > src.Length || dstOff < 0 || dstOff + len > size)
                {
                    // Out of range either side - report rather than write a partial value.
                    missing++;
                    missingNames.Add($"{name}(range)");
                    continue;
                }

                Array.Copy(src, srcOff, buffer, dstOff, len);
                matched++;
            }

            return buffer;
        }
    }
}
