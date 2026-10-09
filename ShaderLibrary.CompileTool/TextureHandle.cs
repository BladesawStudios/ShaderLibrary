using System;
using System.IO;

namespace ShaderLibrary.CompileTool
{
    /// <summary>A texture found in a game's files, loadable without knowing which container it came from.</summary>
    public sealed class TextureHandle(Func<int, TxtgTexture> load)
    {
        public TxtgTexture Load(int surfaces = int.MaxValue) => load(surfaces);

        /// <summary>Writes the whole mip chain of a plain 2D texture, or layer 0's top mip of an array, to <c>&lt;name&gt;_&lt;w&gt;x&lt;h&gt;_&lt;format&gt;.bin</c>.</summary>
        public (TxtgTexture Texture, string File) ExportMipChain(string name, string outDir)
        {
            var header = Load(0);
            var tex = Load(header.ArrayCount <= 1 ? header.MipCount : 1);
            string file = $"{name}_{tex.Width}x{tex.Height}_{tex.Format}.bin";
            using var outStream = File.Create(Path.Combine(outDir, file));
            foreach (var surf in tex.Surfaces)
                outStream.Write(surf.Data);
            return (tex, file);
        }
    }
}
