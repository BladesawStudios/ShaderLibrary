using System;
using System.IO;

namespace ShaderLibrary.CompileTool
{
    /// <summary>A texture found in a game's files, loadable without knowing which container it came from.</summary>
    public sealed class TextureHandle(Func<int, TxtgTexture> load, Func<string, string, (TxtgTexture Texture, string File)>? export = null)
    {
        public TxtgTexture Load(int surfaces = int.MaxValue) => load(surfaces);

        /// <summary>Writes the whole mip chain of a plain 2D texture, or layer 0's top mip of an array, to <c>&lt;name&gt;_&lt;w&gt;x&lt;h&gt;_&lt;format&gt;.bin</c>.</summary>
        public (TxtgTexture Texture, string File) ExportMipChain(string name, string outDir) =>
            export is null ? ExportMipChainDirect(name, outDir) : export(name, outDir);

        /// <summary>The export itself, which a handle's own <c>export</c> may call when it has nothing to reuse.</summary>
        public (TxtgTexture Texture, string File) ExportMipChainDirect(string name, string outDir)
        {
            var header = Load(0);
            var tex = Load(header.ArrayCount <= 1 ? header.MipCount : 1);
            string file = $"{name}_{tex.Width}x{tex.Height}_{tex.Format}.bin";
            int total = 0;
            foreach (var surf in tex.Surfaces)
                total += surf.Data.Length;
            var bytes = new byte[total];
            int at = 0;
            foreach (var surf in tex.Surfaces)
            {
                surf.Data.CopyTo(bytes, at);
                at += surf.Data.Length;
            }
            ExportOutput.WriteBytes(Path.Combine(outDir, file), bytes);
            return (tex, file);
        }
    }
}
