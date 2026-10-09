using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using SarcLibrary;
using Yaz0Sharp;

namespace ShaderLibrary.CompileTool.Rom
{
    /// <summary>
    /// Folders layered over one another (base game, update, DLC: later folders win) read as one filesystem, with SARC archives opened by
    /// path and Yaz0 and zstd understood. Plain files are memory-mapped; archives are decompressed once and kept.
    /// </summary>
    public sealed class LayeredRom : IRomAccess
    {
        const string NestSeparator = "//";

        readonly string[] _roots;
        readonly ConcurrentDictionary<string, MappedFile> _mapped = new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, Archive> _archives = new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, byte[]> _decompressed = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="roots">Lowest priority first.</param>
        public LayeredRom(IEnumerable<string> roots)
        {
            _roots = roots.Where(Directory.Exists).Reverse().ToArray();
            if (_roots.Length == 0)
                throw new DirectoryNotFoundException("None of the ROM folders exist.");
        }

        public bool Exists(string path)
        {
            var (container, inner) = Split(path);
            if (inner is null)
                return Locate(container) is not null;
            return Open(container) is { } archive && archive.Files.ContainsKey(inner);
        }

        public IEnumerable<string> Enumerate(string directory, string searchPattern = "*")
        {
            var (container, inner) = Split(directory);
            if (inner is not null)
            {
                string prefix = inner.Length == 0 || inner.EndsWith('/') ? inner : inner + "/";
                var archive = Open(container);
                return archive is null ? [] : archive.Files.Keys
                    .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !name[prefix.Length..].Contains('/')
                        && Matches(name[prefix.Length..], searchPattern))
                    .Select(name => container + NestSeparator + name);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in _roots)
            {
                string folder = Path.Combine(root, directory);
                if (!Directory.Exists(folder))
                    continue;
                foreach (string file in Directory.EnumerateFiles(folder, searchPattern))
                    seen.Add(directory.TrimEnd('/') + "/" + Path.GetFileName(file));
            }
            return seen;
        }

        public ReadOnlySpan<byte> ReadAllBytesDirectSpan(string path) => Read(path, decompress: false);

        public ReadOnlySpan<byte> ReadAllBytesCompressedSpan(string path)
        {
            ReadOnlySpan<byte> stored = Read(path, decompress: false);
            if (!RomCompression.IsCompressed(stored))
                throw new InvalidDataException($"'{path}' is not in a compressed format.");
            return Decompressed(path, stored);
        }

        public ReadOnlySpan<byte> ReadAllBytesNested(string path) => Read(path, decompress: true);

        public Stream OpenStreamDirect(string path)
        {
            var (container, inner) = Split(path);
            if (inner is null && Locate(container) is { } file)
                return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            ReadOnlySpan<byte> bytes = Read(path, decompress: false);
            return new MemoryStream(bytes.ToArray(), writable: false);
        }

        ReadOnlySpan<byte> Read(string path, bool decompress)
        {
            var (container, inner) = Split(path);
            ReadOnlySpan<byte> stored;
            if (inner is null)
            {
                string file = Locate(container) ?? throw new FileNotFoundException($"'{path}' is in none of the ROM folders.");
                stored = _mapped.GetOrAdd(file, f => new MappedFile(f)).Span;
            }
            else
            {
                var archive = Open(container) ?? throw new FileNotFoundException($"'{container}' is not an archive in the ROM.");
                if (!archive.Files.TryGetValue(inner, out var entry))
                    throw new FileNotFoundException($"'{inner}' is not in '{container}'.");
                stored = entry.AsSpan();
            }
            return decompress && RomCompression.IsCompressed(stored) ? Decompressed(path, stored) : stored;
        }

        byte[] Decompressed(string key, ReadOnlySpan<byte> stored)
        {
            if (_decompressed.TryGetValue(key, out var cached))
                return cached;
            byte[] plain = RomCompression.Decompress(stored);
            return _decompressed.GetOrAdd(key, plain);
        }

        Archive? Open(string path)
        {
            if (_archives.TryGetValue(path, out var cached))
                return cached;
            byte[] bytes;
            try
            {
                ReadOnlySpan<byte> stored = Read(path, decompress: false);
                bytes = RomCompression.IsCompressed(stored) ? Decompressed(path, stored) : stored.ToArray();
            }
            catch (FileNotFoundException) { return null; }
            if (bytes.Length < 4 || bytes[0] != 'S' || bytes[1] != 'A' || bytes[2] != 'R' || bytes[3] != 'C')
                return null;

            return _archives.GetOrAdd(path, new Archive(Sarc.FromBinary(new ArraySegment<byte>(bytes))));
        }

        string? Locate(string path)
        {
            foreach (string root in _roots)
            {
                string file = Path.Combine(root, path);
                if (File.Exists(file))
                    return file;
            }
            return null;
        }

        static (string Container, string? Inner) Split(string path)
        {
            path = path.Replace('\\', '/');
            int at = path.LastIndexOf(NestSeparator, StringComparison.Ordinal);
            return at < 0 ? (path, null) : (path[..at], path[(at + NestSeparator.Length)..]);
        }

        static bool Matches(string name, string pattern) =>
            pattern == "*" || System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

        public void Dispose()
        {
            foreach (var file in _mapped.Values)
                file.Dispose();
            _mapped.Clear();
            _archives.Clear();
            _decompressed.Clear();
        }

        sealed class Archive(Sarc sarc)
        {
            public IReadOnlyDictionary<string, ArraySegment<byte>> Files { get; } = sarc.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        }

        sealed unsafe class MappedFile : IDisposable
        {
            readonly MemoryMappedFile _file;
            readonly MemoryMappedViewAccessor _view;
            readonly byte* _pointer;
            readonly int _length;

            public MappedFile(string path)
            {
                long length = new FileInfo(path).Length;
                if (length > int.MaxValue)
                    throw new IOException($"'{path}' is larger than 2 GB.");
                _length = (int)length;
                if (_length == 0)
                {
                    _file = null!;
                    _view = null!;
                    return;
                }
                _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            }

            public ReadOnlySpan<byte> Span => _length == 0 ? default : new ReadOnlySpan<byte>(_pointer, _length);

            public void Dispose()
            {
                if (_length == 0)
                    return;
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _view.Dispose();
                _file.Dispose();
            }
        }
    }
}
