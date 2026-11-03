using System;
using System.IO;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Where the exporters hand the files of a model that a host may want stored its own way - vertices, indices, shape blocks, material blocks, texture mips, and the
    /// small text files beside them - once each is built in memory. A host that wants them stored another way (compressed, say, shared between models, or all in one file)
    /// replaces <see cref="WriteBytes"/> and <see cref="WriteText"/>, and <see cref="Exists"/> to say whether one it was handed is there; the defaults use the file as it is.
    /// </summary>
    public static class ExportOutput
    {
        public static Action<string, byte[]> WriteBytes { get; set; } = File.WriteAllBytes;

        public static Action<string, string> WriteText { get; set; } = File.WriteAllText;

        public static Func<string, bool> Exists { get; set; } = File.Exists;
    }
}
