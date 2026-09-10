using System;
using System.IO;
using System.Linq;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Locating a model inside romfs.
    ///
    /// Model files are named &lt;pack&gt;.&lt;model&gt;.bfres.mc. The two halves coincide often enough
    /// (Weapon_Sword_070.Weapon_Sword_070) that the tool used to just double whatever name it was
    /// given - but that is the MINORITY case: of the .bfres.mc files in Romfs121, 1,424 are
    /// doubled and 10,968 are not. Enemy_Chuchu_Junior.Chuchu_Elec_Junior is one of the latter,
    /// and doubling it produced the nonsense path
    /// "Enemy_Chuchu_Junior.Chuchu_Elec_Junior.Enemy_Chuchu_Junior.Chuchu_Elec_Junior.bfres.mc".
    ///
    /// So accept any of the three things a person would reasonably type:
    ///   * the full stem            "Enemy_Chuchu_Junior.Chuchu_Elec_Junior"
    ///   * a doubled short name     "Weapon_Sword_070"
    ///   * just the model half      "Chuchu_Elec_Junior"   (when it is unambiguous)
    /// </summary>
    public static class RomfsPaths
    {
        const string Suffix = ".bfres.mc";

        /// <summary>Resolve a model name to its .bfres.mc path, or null if it cannot be found.</summary>

        /// <summary>
        /// A usable on-disk path for a romfs file that ships zstd-compressed, decompressing it to a
        /// cache directory the first time.
        /// </summary>
        /// <remarks>
        /// Shipped romfs has <c>material.Product.110.product.Nin_NX_NVN.bfsha.zs</c>, not a plain
        /// <c>.bfsha</c> - every one of these under <c>Shader/</c> is compressed. Code that took the
        /// uncompressed path only worked for someone who had decompressed it by hand beforehand,
        /// and failed with "Could not find file" on a clean dump, which is exactly the portability
        /// hole this project is not supposed to have.
        ///
        /// Cached beside Marrow's other derived data rather than written back into romfs: the ROM
        /// is the user's own dump and this must not modify it. Decompressing a ~100MB archive takes
        /// a moment, so the cached copy is reused on every later run.
        /// </remarks>
        public static string ResolveMaybeCompressed(string plainPath)
        {
            if (File.Exists(plainPath))
                return plainPath;

            string compressed = plainPath + ".zs";
            if (!File.Exists(compressed))
                return plainPath;   // let the caller report the missing file as it would anyway

            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Marrow", "cache", "_romfs_decompressed");
            Directory.CreateDirectory(cacheDir);
            string cached = Path.Combine(cacheDir, Path.GetFileName(plainPath));

            if (File.Exists(cached) && new FileInfo(cached).Length > 0)
                return cached;

            Console.WriteLine($"[RomfsPaths] decompressing '{Path.GetFileName(compressed)}' -> {cached}");
            byte[] raw = File.ReadAllBytes(compressed);
            byte[] plain = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            // Written via a temp file so an interrupted run cannot leave a truncated cache entry
            // that every later run would then happily reuse.
            string tmp = cached + ".tmp";
            File.WriteAllBytes(tmp, plain);
            File.Move(tmp, cached, overwrite: true);
            return cached;
        }

        public static string? ModelFile(string romfsRoot, string name)
        {
            string dir = Path.Combine(romfsRoot, "Model");
            if (!Directory.Exists(dir))
                return null;

            // 1. The caller already gave the full "<pack>.<model>" stem.
            string direct = Path.Combine(dir, name + Suffix);
            if (File.Exists(direct))
                return direct;

            // 2. The doubled convention.
            string doubled = Path.Combine(dir, $"{name}.{name}{Suffix}");
            if (File.Exists(doubled))
                return doubled;

            // 3. Just the model half - only if exactly one file ends with it, otherwise the
            //    caller is told about the ambiguity rather than being handed an arbitrary pick.
            var hits = Directory.GetFiles(dir, $"*.{name}{Suffix}");
            return hits.Length == 1 ? hits[0] : null;
        }

        /// <summary>
        /// Human-readable explanation for a failed lookup, including near matches - a wrong name
        /// is the single most common way to fail here, and "no such model" alone does not help.
        /// </summary>
        public static string Explain(string romfsRoot, string name)
        {
            string dir = Path.Combine(romfsRoot, "Model");
            if (!Directory.Exists(dir))
                return $"no Model directory under \"{romfsRoot}\"";

            var ambiguous = Directory.GetFiles(dir, $"*.{name}{Suffix}");
            if (ambiguous.Length > 1)
                return $"\"{name}\" is ambiguous - it matches {ambiguous.Length} files. Use the full stem, e.g.\n"
                       + string.Join("\n", ambiguous.Take(6).Select(
                             f => "    " + Path.GetFileName(f)[..^Suffix.Length]));

            string needle = name.Contains('.') ? name.Split('.').Last() : name;
            var near = Directory.GetFiles(dir, "*" + Suffix)
                                .Select(f => Path.GetFileName(f)[..^Suffix.Length])
                                .Where(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase))
                                .Take(8).ToList();
            if (near.Count > 0)
                return $"no model \"{name}\". Did you mean one of:\n" +
                       string.Join("\n", near.Select(n => "    " + n));

            return $"no model \"{name}\" under {dir}.\n" +
                   "    Model files are <pack>.<model>.bfres.mc - pass either the full stem or, " +
                   "when unique, just the model half.";
        }
    }
}
