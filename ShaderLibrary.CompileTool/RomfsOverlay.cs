using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Layers mod romfs folders over the base romfs dump, file by file - the same thing an emulator
    /// (Ryujinx/Yuzu <c>mods/.../romfs</c>) or Atmosphere's LayeredFS does at runtime.
    /// </summary>
    /// <remarks>
    /// A lookup of <c>TexToGo/Foo.txtg</c> returns the first mod (highest priority first) that ships
    /// that exact relative path, falling back to the base romfs. Replacement is per FILE, never per
    /// asset group, which is what makes a texture-only mod work against an unmodded model: the model
    /// still resolves to the base <c>Model/*.bfres.mc</c>, while each of its <c>TexToGo/</c> lookups
    /// independently picks up the mod's copy.
    ///
    /// Deliberately NOT routed through here: <c>Shader/*.bfsha</c> and the agl/system archives. The
    /// decompiled-shader cache (<c>_shaders</c>) and <see cref="RomfsPaths.ResolveMaybeCompressed"/>'s
    /// decompressed cache are both keyed on the assumption that those archives are the fixed,
    /// shipped ones; layering a mod's copy under the same keys would silently mix programs from two
    /// different archives. Mods that replace shader archives are vanishingly rare for TotK anyway.
    ///
    /// Every resolution made while a <see cref="Recorder"/> is active is logged, so a prepared model
    /// can remember which layer each of its source files came from and be re-prepared when the mod
    /// set changes what would win (see <c>ModelPreparer</c>).
    /// </remarks>
    public static class RomfsOverlay
    {
        static string[] _modRoots = [];
        static int _version;

        /// <summary>Active mod romfs roots, highest priority first.</summary>
        public static IReadOnlyList<string> ModRoots => Volatile.Read(ref _modRoots);

        /// <summary>Bumped every time <see cref="SetModRoots"/> changes the active set, so long-lived caches built from overlaid files (<see cref="ExternalBinaryStringTable"/>) know to reload.</summary>
        public static int Version => Volatile.Read(ref _version);

        /// <param name="romfsRoots">Each mod's own romfs directory (the folder that directly contains <c>Model/</c>, <c>TexToGo/</c>, ...), highest priority first. Missing directories are dropped.</param>
        public static void SetModRoots(IEnumerable<string> romfsRoots)
        {
            var roots = romfsRoots.Where(Directory.Exists).Select(Path.GetFullPath).ToArray();
            if (roots.SequenceEqual(Volatile.Read(ref _modRoots), StringComparer.OrdinalIgnoreCase))
                return;
            Volatile.Write(ref _modRoots, roots);
            Interlocked.Increment(ref _version);
        }

        /// <summary>
        /// The winning on-disk path for <paramref name="relativePath"/> (e.g. <c>"TexToGo/Foo.txtg"</c>):
        /// the highest-priority mod that has it, else the base romfs path - returned whether or not
        /// the base file exists, so callers keep their own "not found" handling unchanged.
        /// </summary>
        public static string Resolve(string romfsRoot, string relativePath)
        {
            string rel = Normalize(relativePath);
            string? hit = FindInMods(rel);
            string result = hit ?? Path.Combine(romfsRoot, rel);
            Recorder.Current?.Note(rel, File.Exists(result) ? result : null);
            return result;
        }

        /// <summary><see cref="Resolve(string, string)"/> from path segments, mirroring <see cref="Path.Combine(string[])"/>.</summary>
        public static string Resolve(string romfsRoot, params string[] relativeParts) =>
            Resolve(romfsRoot, Path.Combine(relativeParts));

        /// <summary>True if any layer has the file.</summary>
        public static bool Exists(string romfsRoot, string relativePath) =>
            File.Exists(Resolve(romfsRoot, relativePath));

        /// <summary>Which mod root (or null for base) <paramref name="relativePath"/> would currently come from. Does not record.</summary>
        public static string? WinningMod(string relativePath)
        {
            string rel = Normalize(relativePath);
            foreach (string mod in ModRoots)
                if (File.Exists(Path.Combine(mod, rel)))
                    return mod;
            return null;
        }

        /// <summary>Same as <see cref="Resolve(string, string)"/> but never records - for cache-staleness checks that must not pollute an active recording.</summary>
        public static string? Peek(string romfsRoot, string relativePath)
        {
            string rel = Normalize(relativePath);
            string result = FindInMods(rel) ?? Path.Combine(romfsRoot, rel);
            return File.Exists(result) ? result : null;
        }

        /// <summary>
        /// Every file matching <paramref name="searchPattern"/> in <paramref name="relativeDirectory"/>
        /// across all layers, one entry per file NAME with the highest-priority layer's path - so a
        /// mod that ADDS a file (a brand-new actor pack, a new anim archive) shows up alongside the
        /// base ones, and a mod that replaces one shadows it instead of duplicating it.
        /// </summary>
        public static IEnumerable<string> EnumerateFiles(string romfsRoot, string relativeDirectory, string searchPattern)
        {
            string rel = Normalize(relativeDirectory);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string layer in ModRoots.Append(romfsRoot))
            {
                string dir = Path.Combine(layer, rel);
                if (!Directory.Exists(dir))
                    continue;
                foreach (string file in Directory.EnumerateFiles(dir, searchPattern))
                    if (seen.Add(Path.GetFileName(file)))
                        yield return file;
            }
        }

        /// <summary>True if the directory exists in any layer.</summary>
        public static bool DirectoryExists(string romfsRoot, string relativeDirectory)
        {
            string rel = Normalize(relativeDirectory);
            return ModRoots.Append(romfsRoot).Any(l => Directory.Exists(Path.Combine(l, rel)));
        }

        static string? FindInMods(string rel)
        {
            foreach (string mod in ModRoots)
            {
                string candidate = Path.Combine(mod, rel);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        static string Normalize(string relativePath) =>
            relativePath.Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        /// <summary>
        /// Finds the romfs directory inside a mod folder as people actually distribute them:
        /// <c>Mod/romfs</c> or <c>Mod/romfslite</c> (emulator layout / TKMM merge output), <c>Mod/contents/&lt;titleId&gt;/romfs</c> or
        /// <c>Mod/atmosphere/contents/&lt;titleId&gt;/romfs</c> (SD-card layout), or a folder that
        /// already IS a romfs root. Null if none of those match.
        /// </summary>
        public static string? FindModRomfs(string modFolder)
        {
            if (!Directory.Exists(modFolder))
                return null;

            // "romfslite" is TKMM's "Use romfslite" merge output: the same romfs layout, with the
            // folder renamed so Atmosphere skips building a romfs and TotK Optimizer loads the
            // files instead. To Marrow it is just another romfs.
            foreach (string name in RomfsFolderNames)
            {
                string direct = Path.Combine(modFolder, name);
                if (Directory.Exists(direct))
                    return direct;
            }

            foreach (string contents in new[] { Path.Combine(modFolder, "contents"), Path.Combine(modFolder, "atmosphere", "contents") })
            {
                if (!Directory.Exists(contents))
                    continue;
                foreach (string titleDir in Directory.EnumerateDirectories(contents))
                    foreach (string name in RomfsFolderNames)
                    {
                        string romfs = Path.Combine(titleDir, name);
                        if (Directory.Exists(romfs))
                            return romfs;
                    }
            }

            return LooksLikeRomfs(modFolder) ? modFolder : null;
        }

        static readonly string[] RomfsFolderNames = ["romfs", "romfslite"];

        static readonly string[] RomfsTopLevelDirs =
            ["Model", "TexToGo", "Pack", "Actor", "Phive", "Shader", "Banner", "UI", "Mals", "System", "Effect", "Sound", "Event"];

        static bool LooksLikeRomfs(string folder) =>
            RomfsTopLevelDirs.Any(d => Directory.Exists(Path.Combine(folder, d)));

        /// <summary>
        /// Collects every romfs-relative path resolved through the overlay while it is active (on
        /// this async flow only - a background prepare does not pick up the UI thread's lookups),
        /// along with which file won. Dispose to stop.
        /// </summary>
        public sealed class Recorder : IDisposable
        {
            static readonly AsyncLocal<Recorder?> _current = new();
            internal static Recorder? Current => _current.Value;

            readonly Recorder? _previous;
            readonly ConcurrentDictionary<string, string?> _files = new(StringComparer.OrdinalIgnoreCase);

            Recorder()
            {
                _previous = _current.Value;
                _current.Value = this;
            }

            public static Recorder Begin() => new();

            /// <summary>Romfs-relative path -&gt; the full path that won, or null if no layer had it (a missing file is recorded too, so a mod that later ADDS it still invalidates).</summary>
            public IReadOnlyDictionary<string, string?> Files => _files;

            internal void Note(string rel, string? winner) => _files[rel] = winner;

            public void Dispose() => _current.Value = _previous;
        }
    }
}
