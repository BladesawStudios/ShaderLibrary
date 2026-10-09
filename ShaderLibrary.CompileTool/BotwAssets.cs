using System;
using System.Collections.Generic;
using System.IO;
using BfresLibrary;
using BntxSharp;
using ShaderLibrary.CompileTool.Rom;

namespace ShaderLibrary.CompileTool
{
    /// <summary>Breath of the Wild's models, textures and shader archives, read through whatever <see cref="IRomAccess"/> the host supplies.</summary>
    public sealed class BotwAssets(IRomAccess rom) : IGameAssets
    {
        const string GraphicsPack = "Pack/Bootup_Graphics.pack";
        static readonly string[] ModelPacks = ["Pack/TitleBG.pack"];

        readonly Dictionary<string, List<BntxFile>> _textures = new(StringComparer.OrdinalIgnoreCase);
        string _currentModel = "";

        public IRomAccess Rom => rom;

        public byte[]? ReadModel(string modelName)
        {
            _currentModel = modelName;
            byte[]? data = ReadModelFile($"{modelName}.sbfres");
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

        /// <summary>Writes <c>Shader/&lt;name&gt;.product.sbfsha</c> from the graphics pack into <paramref name="directory"/>, where the shader tools can open it by path.</summary>
        public string ExtractShaderArchive(string name, string directory)
        {
            string path = Path.Combine(directory, name + ".bfsha");
            if (File.Exists(path))
                return path;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, rom.ReadAllBytesNested($"{GraphicsPack}//Shader/{name}.product.sbfsha").ToArray());
            return path;
        }

        IEnumerable<BntxFile> TextureArchives()
        {
            if (!_textures.TryGetValue(_currentModel, out var list))
            {
                list = [];
                if (ReadModelFile($"{_currentModel}.Tex.sbfres") is { } data)
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

        // A model file sits loose under Model/ in some dumps and inside a pack in others.
        byte[]? ReadModelFile(string fileName)
        {
            string loose = $"Model/{fileName}";
            if (rom.Exists(loose))
                return rom.ReadAllBytesNested(loose).ToArray();
            foreach (string pack in ModelPacks)
            {
                string nested = $"{pack}//{loose}";
                if (rom.Exists(nested))
                    return rom.ReadAllBytesNested(nested).ToArray();
            }
            return null;
        }
    }
}
