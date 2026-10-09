using System;
using System.Collections.Concurrent;
using System.IO;
using ShaderLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// One parsed copy of each shader archive per process, shared by every model prepared in it.
    /// </summary>
    /// <remarks>
    /// Parsing <c>material.*.bfsha</c> takes ~450 ms, and preparing a model used to parse it twice
    /// (<see cref="BuildMaterialUbo"/>, then <see cref="ExportManifest"/>) - for a typical world
    /// object that was 90% of its whole preparation. The archive is a fixed shipped file (mods are
    /// never layered over by mods), and preparation only
    /// reads it, so one instance serves every model and every thread.
    /// </remarks>
    public static class SharedBfsha
    {
        static readonly ConcurrentDictionary<string, Lazy<BfshaFile>> Files = new(StringComparer.OrdinalIgnoreCase);

        public static BfshaFile Load(string path) =>
            Files.GetOrAdd(Path.GetFullPath(path), p => new Lazy<BfshaFile>(() => new BfshaFile(p))).Value;

        /// <summary>Drops every cached archive, for a long-lived host that is done preparing.</summary>
        public static void Clear() => Files.Clear();
    }
}
