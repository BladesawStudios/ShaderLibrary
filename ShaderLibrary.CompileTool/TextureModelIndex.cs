using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Works out which models reference which <c>TexToGo/</c> textures - the reverse of what a
    /// model file says, needed so a texture (e.g. one a mod replaces) can be loaded "by texture":
    /// nothing in romfs records texture -&gt; model, only model -&gt; texture.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT parse each model with BfresLibrary. A model's material texture
    /// references are stored as ordinary entries in the BFRES string pool (confirmed on
    /// <c>Weapon_Sword_070</c>: its decompressed bytes contain <c>Weapon_Sword_070_Alb</c>/<c>_Nrm</c>/
    /// <c>_Spm</c> verbatim), so scanning the decompressed bytes for NUL-terminated identifier runs
    /// and keeping the ones that are real TexToGo names finds them without a full parse - an
    /// order of magnitude cheaper across ~13k models, and immune to the parser crashes a full load
    /// of every model in the game would inevitably hit. The cost is the MeshCodec decompress
    /// itself (one process per model), which is why callers cache the result.
    ///
    /// A false positive needs a non-texture string that is byte-identical to a real texture name;
    /// TotK's naming makes that essentially impossible, and the worst case is one extra model
    /// offered for a texture.
    /// </remarks>
    public static class TextureModelIndex
    {
        /// <summary>
        /// Scans each model and returns model stem (<c>&lt;pack&gt;.&lt;model&gt;</c>) -&gt; the texture
        /// names from <paramref name="textureNames"/> it references. Models that fail to
        /// decompress are skipped (and reported through <paramref name="log"/>), never fatal.
        /// </summary>
        public static Dictionary<string, List<string>> ScanModels(
            IReadOnlyList<string> mcPaths, IReadOnlySet<string> textureNames,
            Action<int, int>? progress = null, Action<string>? log = null, CancellationToken cancel = default)
        {
            var result = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int done = 0;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                CancellationToken = cancel,
            };

            Parallel.ForEach(mcPaths, options, path =>
            {
                string stem = ModelStem(path);
                try
                {
                    byte[] data = TestMaterialDump.DecompressBfresMc(path);
                    var found = FindNames(data, textureNames);
                    if (found.Count > 0)
                        result[stem] = found;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[TextureModelIndex] skipped {Path.GetFileName(path)}: {ex.Message}");
                }
                progress?.Invoke(Interlocked.Increment(ref done), mcPaths.Count);
            });

            return new Dictionary<string, List<string>>(result, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary><c>Model/Foo.Bar.bfres.mc</c> -&gt; <c>Foo.Bar</c>.</summary>
        public static string ModelStem(string mcPath)
        {
            string name = Path.GetFileName(mcPath);
            const string suffix = ".bfres.mc";
            return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name;
        }

        /// <summary>Every NUL-terminated identifier-ish run in <paramref name="data"/> that is in <paramref name="names"/>, de-duplicated, in first-seen order.</summary>
        public static List<string> FindNames(byte[] data, IReadOnlySet<string> names)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int start = -1;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                bool ident = (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '_' || b == '-' || b == '.';
                if (ident)
                {
                    if (start < 0)
                        start = i;
                    continue;
                }
                if (b == 0 && start >= 0 && i - start >= 3 && i - start <= 128)
                {
                    string token = System.Text.Encoding.ASCII.GetString(data, start, i - start);
                    if (names.Contains(token) && seen.Add(token))
                        found.Add(token);
                }
                start = -1;
            }
            return found;
        }
    }
}
