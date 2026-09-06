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
