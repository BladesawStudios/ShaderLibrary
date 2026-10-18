using System;
using System.IO;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Where the exporters hand the binary files of a model - vertices, indices, shape blocks, material blocks, texture mips - once each is built in memory.
    /// A host that wants them stored another way (compressed, say, or shared between models) replaces <see cref="WriteBytes"/>; the default writes the file as it is.
    /// </summary>
    public static class ExportOutput
    {
        public static Action<string, byte[]> WriteBytes { get; set; } = File.WriteAllBytes;
    }
}
