using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BfresLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Dumps every Material in a TotK .bfres model: its ShaderAssign (ShadingModelName +
    /// ShaderOptions #define selection) and every ShaderParam decoded out of the raw
    /// ShaderParamData byte blob according to the param's declared ShaderParamType.
    ///
    /// TotK ships models as "<name>.bfres.mc" under romfs/Model/, wrapped in a custom
    /// "MCPK" container - NOT plain zstd. An earlier version of this file reverse-engineered
    /// just enough of that container by hand (12-byte header + a single magicless zstd frame)
    /// to get *something* to decompress, but real models also carry a second, separately-coded
    /// "FMSH" mesh-geometry section appended after the main zstd payload (a completely
    /// different, bespoke bitpacked vertex/index codec - not zstd at all), spliced into the
    /// output at a computed offset. Any decompressor that only handles the outer zstd frame
    /// silently produces truncated/misaligned output for real models - which is exactly why
    /// that version's ShaderParam names all came back empty despite the raw ShaderParamData
    /// bytes being present (see git history for that attempt if curious).
    ///
    /// The genuine, complete container spec lives in a proper reference implementation (its own
    /// source, particularly src/mc_MeshCodec.h's ResMeshCodecPackageHeader and
    /// src/mc_MeshCodec.cpp's DecompressMC, is the authoritative spec - not this comment). Rather
    /// than re-port that C++ (bitpacked vertex/index decode included) into C#, this shells out to
    /// that project's own working CLI tool's prebuilt binary, vendored into this repo at
    /// vendor/MeshCodec/meshcodec_cli.exe, to do the real, complete decompression, then loads the
    /// resulting byte-perfect .bfres normally. See MeshCodecCliPath below for how that tool is
    /// located.
    ///
    /// See BfresLibraryPatches.cs for two BfresLibrary.dll bugs patched at runtime to reach
    /// Model/Material objects without crashing (unrelated to the container format - those bugs
    /// are real, independent of what decompressor produced the .bfres bytes).
    /// </summary>
    public static class TestMaterialDump
    {
        /// <summary>
        /// Path to MeshCodec's built CLI decompressor (see class remarks - this repo is the
        /// authoritative decoder, not reimplemented here). Vendored as a prebuilt binary at
        /// vendor/MeshCodec/meshcodec_cli.exe, resolved relative to THIS source file's own
        /// location (via <c>[CallerFilePath]</c>) rather than a hardcoded absolute path, so it
        /// works from a fresh checkout on any machine regardless of where the repo lives.
        ///
        /// To rebuild it from MeshCodec's own source (github.com/M-Mods/MeshCodec or wherever it
        /// was sourced from):
        ///   (initialize the lib/zstd submodule first: git submodule update --init --recursive)
        ///   cmake -B build -G Ninja -DCMAKE_MAKE_PROGRAM=&lt;path to VS's bundled ninja.exe&gt; ...
        ///   cmake --build build --config Release
        /// (devkitPro's own MSYS2-flavored cmake/ninja pair generates GCC-style flags that
        /// MSVC's cl.exe rejects - use Visual Studio's own bundled cmake+ninja, found under
        /// its install path at Common7\IDE\CommonExtensions\Microsoft\CMake\{CMake,Ninja}\,
        /// invoked from an x64 Developer environment (vcvars64.bat) so cl.exe resolves), then copy
        /// the resulting tests/meshcodec_cli.exe over vendor/MeshCodec/meshcodec_cli.exe.
        /// Override via the MESHCODEC_CLI environment variable to point at a different build.
        /// </summary>
        static string MeshCodecCliPath =>
            Environment.GetEnvironmentVariable("MESHCODEC_CLI")
            ?? Path.Combine(RepoVendorDir(), "MeshCodec", "meshcodec_cli.exe");

        /// <summary>
        /// This file lives at vendor/ShaderLibrary/ShaderLibrary.CompileTool/TestMaterialDump.cs,
        /// so its own directory's great-grandparent is the repo's vendor/ folder - resolved from
        /// the SOURCE file's compile-time location (stable across machines/checkouts), not the
        /// build output directory (which varies by configuration/TFM).
        /// </summary>
        static string RepoVendorDir([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

        /// <summary>
        /// Decompresses a romfs "*.bfres.mc" (MCPK-wrapped) file into a raw BFRES byte array
        /// by shelling out to MeshCodec's real CLI decoder (see class remarks).
        /// </summary>
        public static byte[] DecompressBfresMc(string mcPath)
        {
            string cli = MeshCodecCliPath;
            if (!File.Exists(cli))
                throw new FileNotFoundException(
                    $"meshcodec_cli.exe not found at '{cli}' - build it first (see MeshCodecCliPath's remarks), " +
                    "or set the MESHCODEC_CLI environment variable to its path.", cli);

            string outDir = Path.Combine(Path.GetTempPath(), "mc_decomp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            try
            {
                var psi = new ProcessStartInfo(cli, $"\"{mcPath}\" \"{outDir}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException($"Failed to start {cli}");
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                // meshcodec_cli writes <outDir>/<stem of input, no .mc> - e.g.
                // "Foo.bfres.mc" -> "<outDir>/Foo.bfres".
                string expected = Path.Combine(outDir, Path.GetFileNameWithoutExtension(mcPath));
                if (proc.ExitCode != 0 || !File.Exists(expected))
                {
                    throw new InvalidDataException(
                        $"meshcodec_cli failed on {mcPath} (exit {proc.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
                }

                byte[] fres = File.ReadAllBytes(expected);
                if (fres.Length < 4 || fres[0] != 'F' || fres[1] != 'R' || fres[2] != 'E' || fres[3] != 'S')
                    throw new InvalidDataException($"{mcPath}: decompressed data does not start with 'FRES' - unexpected output from meshcodec_cli");
                return fres;
            }
            finally
            {
                try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Loads a compressed romfs "*.bfres.mc" model and prints a full material report.
        /// </summary>
        public static ResFile LoadAndDumpMc(string mcPath)
        {
            Console.WriteLine();
            Console.WriteLine("################################################################");
            Console.WriteLine($"# {mcPath}");
            Console.WriteLine("################################################################");

            byte[] fres = DecompressBfresMc(mcPath);
            Console.WriteLine($"Decompressed {new FileInfo(mcPath).Length} bytes -> {fres.Length} bytes of BFRES data");

            using var ms = new MemoryStream(fres);
            var resFile = new ResFile(ms, false);
            DumpResFile(resFile);
            return resFile;
        }

        public static void DumpResFile(ResFile resFile)
        {
            foreach (var modelEntry in resFile.Models)
            {
                Model model = modelEntry.Value;
                Console.WriteLine();
                Console.WriteLine($"== Model: {modelEntry.Key} ({model.Materials.Count} material(s), {model.Shapes.Count} shape(s)) ==");

                foreach (var matEntry in model.Materials)
                    DumpMaterial(matEntry.Value);

                DumpGeometry(model);
            }
        }

        /// <summary>
        /// Prints per-shape mesh/vertex-buffer info to confirm real vertex/index geometry survives
        /// the MeshCodec FMSH decode intact - the next thing after material params needed before a
        /// test-bench renderer can draw an accurate TotK model. Checks: does each Shape's
        /// VertexBuffer have plausible, non-empty attribute names (VertexAttrib.Name goes through
        /// the same LoadString path RenderInfo/ShaderParam/Option names did - it happened to already
        /// work for the small in-material AttributeAssign dict, but that's a different on-disk
        /// dict from VertexBuffer's own Attributes dict, so it's not guaranteed to be unaffected
        /// without checking), and does the raw buffer byte size match Stride * VertexCount (proof
        /// the bytes aren't truncated/misaligned).
        /// </summary>
        public static void DumpGeometry(Model model)
        {
            Console.WriteLine($"  Skeleton: {model.Skeleton.Bones.Count} bone(s)");

            for (int shapeIdx = 0; shapeIdx < model.Shapes.Count; shapeIdx++)
            {
                Shape shape = model.Shapes[shapeIdx];
                VertexBuffer vb = model.VertexBuffers[shape.VertexBufferIndex];

                Console.WriteLine();
                Console.WriteLine($"  -- Shape[{shapeIdx}]: {shape.Name} (material #{shape.MaterialIndex}, vtxBuffer #{shape.VertexBufferIndex}, {shape.Meshes.Count} mesh(es)) --");
                Console.WriteLine($"     VertexCount={vb.VertexCount} SkinCount={shape.VertexSkinCount} Attributes({vb.Attributes.Count}):");

                foreach (var attrEntry in vb.Attributes)
                {
                    VertexAttrib attr = attrEntry.Value;
                    string name = attrEntry.Key;
                    string flag = string.IsNullOrEmpty(name) ? "  <<< EMPTY NAME" : "";
                    Console.WriteLine($"       buf{attr.BufferIndex} off={attr.Offset,4} {attr.Format,-30} {name}{flag}");
                }

                for (int b = 0; b < vb.Buffers.Count; b++)
                {
                    BfresLibrary.Buffer buf = vb.Buffers[b];
                    int bufferingCount = buf.Data?.Length ?? 0;
                    long byteLen = bufferingCount > 0 ? buf.Data![0].Length : 0;
                    long expected = (long)buf.Stride * vb.VertexCount;
                    // Real buffers carry 1-8 trailing bytes of GX2 alignment padding after the
                    // last vertex (pads up to the next 8-byte boundary, always >=1 byte even when
                    // already aligned) - not a truncation/corruption signal, so only flag it if
                    // there's LESS data than expected or MORE than one alignment step of slack.
                    long slack = byteLen - expected;
                    string flag = (slack < 0 || slack > 8) ? $"  <<< MISMATCH (expected {expected})" : "  (matches Stride*VertexCount + alignment padding)";
                    Console.WriteLine($"     Buffer[{b}]: stride={buf.Stride} bufferings={bufferingCount} bytes={byteLen}{flag}");
                }

                for (int m = 0; m < shape.Meshes.Count; m++)
                {
                    Mesh mesh = shape.Meshes[m];
                    long idxBytes = mesh.IndexBuffer.Data?.Length > 0 ? mesh.IndexBuffer.Data[0].Length : 0;
                    Console.WriteLine($"     Mesh[{m}]: {mesh.PrimitiveType} {mesh.IndexFormat} IndexCount={mesh.IndexCount} SubMeshes={mesh.SubMeshes.Count} indexBytes={idxBytes}");
                }
            }
        }

        public static void DumpMaterial(Material material)
        {
            Console.WriteLine();
            Console.WriteLine($"---- Material: {material.Name} ----");

            var assign = material.ShaderAssign;
            if (assign == null)
            {
                Console.WriteLine("  <no ShaderAssign>");
            }
            else
            {
                Console.WriteLine($"  ShaderArchiveName : {assign.ShaderArchiveName}");
                Console.WriteLine($"  ShadingModelName  : {assign.ShadingModelName}");
                Console.WriteLine($"  Revision          : {assign.Revision}");
                Console.WriteLine($"  ShaderOptions ({assign.ShaderOptions.Count}):");
                foreach (var op in assign.ShaderOptions)
                    Console.WriteLine($"    {op.Key,-40} = {op.Value}");
            }

            if (material.TextureRefs.Count > 0)
            {
                Console.WriteLine($"  TextureRefs ({material.TextureRefs.Count}):");
                foreach (var texRef in material.TextureRefs)
                    Console.WriteLine($"    {texRef.Name}");
            }

            byte[] data = material.ShaderParamData ?? Array.Empty<byte>();
            Console.WriteLine($"  ShaderParams ({material.ShaderParams.Count}), ShaderParamData = {data.Length} bytes:");
            foreach (var paramEntry in material.ShaderParams)
            {
                ShaderParam param = paramEntry.Value;
                string decoded = DecodeShaderParam(param, data);
                Console.WriteLine($"    [{param.DataOffset,4}:{param.DataSize,3}] {param.Type,-10} {param.Name,-42} = {decoded}");
            }
        }

        /// <summary>
        /// Decodes a single ShaderParam's value out of a material's raw ShaderParamData blob,
        /// dispatching on the param's declared ShaderParamType. Returns a human readable string,
        /// or a clearly-flagged error string (never garbage passed off as real data) if the
        /// offset/size don't fit the buffer.
        /// </summary>
        public static string DecodeShaderParam(ShaderParam param, byte[] data)
        {
            int offset = param.DataOffset;
            int size = (int)param.DataSize;

            if (offset < 0 || size < 0 || offset + size > data.Length)
                return $"<out of bounds: offset={offset} size={size} bufferLen={data.Length}>";

            switch (param.Type)
            {
                case ShaderParamType.Bool:
                    return FormatBools(data, offset, size, 1);
                case ShaderParamType.Bool2:
                    return FormatBools(data, offset, size, 2);
                case ShaderParamType.Bool3:
                    return FormatBools(data, offset, size, 3);
                case ShaderParamType.Bool4:
                    return FormatBools(data, offset, size, 4);

                case ShaderParamType.Int:
                    return FormatInts(data, offset, size, 1);
                case ShaderParamType.Int2:
                    return FormatInts(data, offset, size, 2);
                case ShaderParamType.Int3:
                    return FormatInts(data, offset, size, 3);
                case ShaderParamType.Int4:
                    return FormatInts(data, offset, size, 4);

                case ShaderParamType.UInt:
                    return FormatUInts(data, offset, size, 1);
                case ShaderParamType.UInt2:
                    return FormatUInts(data, offset, size, 2);
                case ShaderParamType.UInt3:
                    return FormatUInts(data, offset, size, 3);
                case ShaderParamType.UInt4:
                    return FormatUInts(data, offset, size, 4);

                case ShaderParamType.Float:
                    return FormatFloats(data, offset, 1);
                case ShaderParamType.Float2:
                    return FormatFloats(data, offset, 2);
                case ShaderParamType.Float3:
                    return FormatFloats(data, offset, 3);
                case ShaderParamType.Float4:
                    return FormatFloats(data, offset, 4);

                case ShaderParamType.Float2x2:
                    return FormatMatrix(data, offset, 2, 2);
                case ShaderParamType.Float2x3:
                    return FormatMatrix(data, offset, 2, 3);
                case ShaderParamType.Float2x4:
                    return FormatMatrix(data, offset, 2, 4);
                case ShaderParamType.Float3x2:
                    return FormatMatrix(data, offset, 3, 2);
                case ShaderParamType.Float3x3:
                    return FormatMatrix(data, offset, 3, 3);
                case ShaderParamType.Float3x4:
                    return FormatMatrix(data, offset, 3, 4);
                case ShaderParamType.Float4x2:
                    return FormatMatrix(data, offset, 4, 2);
                case ShaderParamType.Float4x3:
                    return FormatMatrix(data, offset, 4, 3);
                case ShaderParamType.Float4x4:
                    return FormatMatrix(data, offset, 4, 4);

                // Compact SRT structs. Byte layouts below come from BfresLibrary's own
                // Srt2D/Srt3D/TexSrt classes (Scaling/Rotation/Translation[/Mode] fields,
                // and their SizeInBytes constants: 20 / 36 / 24 respectively) - reflected
                // directly off BfresLibrary.dll rather than guessed.
                case ShaderParamType.Srt2D:
                    return $"scale=({F(data, offset)},{F(data, offset + 4)}) " +
                           $"rot={F(data, offset + 8)} " +
                           $"trans=({F(data, offset + 12)},{F(data, offset + 16)})";
                case ShaderParamType.Srt3D:
                    return $"scale=({F(data, offset)},{F(data, offset + 4)},{F(data, offset + 8)}) " +
                           $"rot=({F(data, offset + 12)},{F(data, offset + 16)},{F(data, offset + 20)}) " +
                           $"trans=({F(data, offset + 24)},{F(data, offset + 28)},{F(data, offset + 32)})";
                case ShaderParamType.TexSrt:
                    return $"mode={I32(data, offset)} " +
                           $"scale=({F(data, offset + 4)},{F(data, offset + 8)}) " +
                           $"rot={F(data, offset + 12)} " +
                           $"trans=({F(data, offset + 16)},{F(data, offset + 20)})";

                case ShaderParamType.TexSrtEx:
                    // No dedicated class for this in BfresLibrary; layout unconfirmed.
                    // Fall back to a raw float dump flagged as such rather than guessing a struct.
                    return "<TexSrtEx: unconfirmed layout, raw floats> " + FormatFloats(data, offset, size / 4);

                case ShaderParamType.Reserved2:
                case ShaderParamType.Reserved3:
                case ShaderParamType.Reserved4:
                    return $"<reserved/internal type, raw hex: {Hex(data, offset, size)}>";

                default:
                    return $"<unhandled type {param.Type}, raw hex: {Hex(data, offset, size)}>";
            }
        }

        static float F(byte[] data, int offset) => BitConverter.ToSingle(data, offset);
        static int I32(byte[] data, int offset) => BitConverter.ToInt32(data, offset);

        static string FormatFloats(byte[] data, int offset, int count)
        {
            var parts = new string[count];
            for (int i = 0; i < count; i++)
                parts[i] = F(data, offset + i * 4).ToString("0.######", CultureInfo.InvariantCulture);
            return count == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")";
        }

        static string FormatInts(byte[] data, int offset, int size, int expectedCount)
        {
            int count = Math.Max(1, size / 4);
            var parts = new string[count];
            for (int i = 0; i < count; i++)
                parts[i] = I32(data, offset + i * 4).ToString(CultureInfo.InvariantCulture);
            string tag = count != expectedCount ? $" [expected {expectedCount} components, size implies {count}]" : "";
            return (count == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")") + tag;
        }

        static string FormatUInts(byte[] data, int offset, int size, int expectedCount)
        {
            int count = Math.Max(1, size / 4);
            var parts = new string[count];
            for (int i = 0; i < count; i++)
                parts[i] = BitConverter.ToUInt32(data, offset + i * 4).ToString(CultureInfo.InvariantCulture);
            string tag = count != expectedCount ? $" [expected {expectedCount} components, size implies {count}]" : "";
            return (count == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")") + tag;
        }

        static string FormatBools(byte[] data, int offset, int size, int expectedCount)
        {
            // Bools are commonly stored 1-byte-per-component in these material blobs; fall
            // back to reading `size` raw bytes (not `expectedCount`) so we report what's
            // actually there instead of silently truncating/over-reading.
            var parts = new string[size];
            for (int i = 0; i < size; i++)
                parts[i] = data[offset + i] != 0 ? "true" : "false";
            string tag = size != expectedCount ? $" [expected {expectedCount} components, {size} bytes present]" : "";
            return (size == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")") + tag;
        }

        static string FormatMatrix(byte[] data, int offset, int cols, int rows)
        {
            int count = cols * rows;
            var parts = new string[count];
            for (int i = 0; i < count; i++)
                parts[i] = F(data, offset + i * 4).ToString("0.###", CultureInfo.InvariantCulture);
            return $"{cols}x{rows}(" + string.Join(", ", parts) + ")";
        }

        static string Hex(byte[] data, int offset, int size)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < size; i++)
                sb.Append(data[offset + i].ToString("X2"));
            return sb.ToString();
        }

        /// <summary>
        /// Demo entry point: decompresses and dumps a handful of real, identifiable romfs
        /// models. Pass the romfs root (the folder containing "Model/", "Pack/" etc).
        /// </summary>
        public static void RunDemo(string romfsRoot)
        {
            ExternalBinaryStringTable.RomfsRoot = romfsRoot;
            BfresLibraryPatches.EnsureApplied();

            string modelDir = Path.Combine(romfsRoot, "Model");

            string[] demoFiles =
            {
                Path.Combine(modelDir, "Weapon_Sword_070.Weapon_Sword_070.bfres.mc"),          // Master Sword
                Path.Combine(modelDir, "Enemy_Bokoblin.Bokoblin_Red.bfres.mc"),                // Red Bokoblin
                Path.Combine(modelDir, "Animal_Bear.Bear.bfres.mc"),                            // Bear (prop/creature)
            };

            foreach (var file in demoFiles)
            {
                if (!File.Exists(file))
                {
                    Console.WriteLine($"[skip] not found: {file}");
                    continue;
                }

                try
                {
                    LoadAndDumpMc(file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FAILED] {file}: {ex}");
                }
            }
        }
    }
}
