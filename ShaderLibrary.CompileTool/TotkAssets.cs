using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>Tears of the Kingdom's romfs, read through the mod overlay.</summary>
    public sealed class TotkAssets(string romfsRoot) : IGameAssets
    {
        public string RomfsRoot => romfsRoot;

        public byte[]? ReadModel(string modelName)
        {
            string? mcPath = RomfsPaths.ModelFile(romfsRoot, modelName);
            if (mcPath == null)
            {
                Console.WriteLine($"[ExportTestBench] {RomfsPaths.Explain(romfsRoot, modelName)}");
                return null;
            }
            Console.WriteLine($"[ExportTestBench] Decompressing {mcPath}...");
            return TestMaterialDump.DecompressBfresMc(mcPath);
        }

        public TextureHandle? FindTexture(string modelName, string name) =>
            TexToGo.Find(romfsRoot, name) is { } path ? new TextureHandle(surfaces => TexToGo.Load(path, surfaces)) : null;

        public IEnumerable<ResFile> AnimationArchives(string modelName, IReadOnlyList<string>? packNames)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;
            if (packNames is { Count: > 0 })
            {
                foreach (string pack in packNames)
                {
                    string path = RomfsOverlay.Resolve(romfsRoot, "Model", $"{pack}.anim.bfres.zs");
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"[ExportTestBench] Actor named anim archive '{pack}.anim.bfres.zs' not found under Model/ - skipping.");
                        continue;
                    }
                    if (ExportTestBench.LoadAnimArchiveForInspection(path) is { } archive)
                        yield return archive;
                }
                yield break;
            }

            string prefix = modelName.Split('.')[0];
            if (!RomfsOverlay.DirectoryExists(romfsRoot, "Model"))
                yield break;
            foreach (string found in RomfsOverlay.EnumerateFiles(romfsRoot, "Model", $"{prefix}*.anim.bfres.zs").ToList())
            {
                string path = RomfsOverlay.Resolve(romfsRoot, "Model", Path.GetFileName(found));
                if (ExportTestBench.LoadAnimArchiveForInspection(path) is { } archive)
                    yield return archive;
            }
        }
    }
}
