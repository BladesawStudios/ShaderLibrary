using System;
using System.Collections.Generic;
using System.IO;
using BfresLibrary;
using BntxSharp;
using SarcLibrary;
using Yaz0Sharp;

namespace ShaderLibrary.CompileTool
{
    /// <summary>Breath of the Wild's Switch dump: Yaz0 SARC packs under <c>Pack/</c>, with models and their textures as <c>.sbfres</c> beside them.</summary>
    public sealed class BotwAssets(string romRoot) : IGameAssets
    {
        readonly Dictionary<string, Sarc> _packs = new(StringComparer.OrdinalIgnoreCase);

        public string RomRoot => romRoot;

        public byte[]? ReadModel(string modelName)
        {
            _currentModel = modelName;
            byte[]? data = ReadFile($"Model/{modelName}.sbfres");
            if (data == null)
                Console.WriteLine($"[ExportTestBench] no model '{modelName}' under Model/ or in the packs.");
            return data;
        }

        public TextureHandle? FindTexture(string name)
        {
            foreach (BntxFile bntx in TextureArchives())
            {
                foreach (BntxTexture texture in bntx.Textures)
                    if (texture.Name == name)
                        return new TextureHandle(surfaces => TexToGo.FromBntx(texture, surfaces));
            }
            return null;
        }

        public IEnumerable<ResFile> AnimationArchives(string modelName, IReadOnlyList<string>? packNames) => [];

        /// <summary>Reads <c>Shader/&lt;name&gt;.product.sbfsha</c> from the graphics pack into <paramref name="directory"/> and returns the path.</summary>
        public string ExtractShaderArchive(string name, string directory)
        {
            string path = Path.Combine(directory, name + ".bfsha");
            if (File.Exists(path))
                return path;
            byte[] data = ReadFile($"Shader/{name}.product.sbfsha")
                ?? throw new FileNotFoundException($"Shader/{name}.product.sbfsha is not in the dump");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, data);
            return path;
        }

        readonly Dictionary<string, List<BntxFile>> _textures = new(StringComparer.OrdinalIgnoreCase);
        string _currentModel = "";

        IEnumerable<BntxFile> TextureArchives()
        {
            if (!_textures.TryGetValue(_currentModel, out var list))
            {
                list = [];
                if (ReadFile($"Model/{_currentModel}.Tex.sbfres") is { } data)
                {
                    using var stream = new MemoryStream(data);
                    var res = new ResFile(stream, false);
                    foreach (var external in res.ExternalFiles)
                        if (external.Key.EndsWith(".bntx", StringComparison.OrdinalIgnoreCase))
                            list.Add(BntxFile.Load(external.Value.Data));
                }
                _textures[_currentModel] = list;
            }
            return list;
        }

        byte[]? ReadFile(string relative)
        {
            string loose = Path.Combine(romRoot, relative);
            if (File.Exists(loose))
                return Yaz0.DecompressIfNeeded(File.ReadAllBytes(loose));

            string packName = relative.StartsWith("Shader/", StringComparison.Ordinal) ? "Bootup_Graphics" : "TitleBG";
            if (!_packs.TryGetValue(packName, out var pack))
            {
                string packPath = Path.Combine(romRoot, "Pack", packName + ".pack");
                if (!File.Exists(packPath))
                    return null;
                pack = Sarc.FromBinary(new ArraySegment<byte>(Yaz0.DecompressIfNeeded(File.ReadAllBytes(packPath))));
                _packs[packName] = pack;
            }
            return pack.TryGetValue(relative, out var entry) ? Yaz0.DecompressIfNeeded(entry.ToArray()) : null;
        }
    }
}
