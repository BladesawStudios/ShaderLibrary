using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BymlLibrary;
using SarcLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Resolves an ACTOR name (e.g. "Enemy_Dragon_Darkness") to its real model file stem and the
    /// exact list of skeletal-anim archives it uses - authoritatively, straight from the game's own
    /// actor definition, instead of guessing from file-naming conventions.
    ///
    /// <c>Pack/Actor/&lt;Actor&gt;.pack.zs</c> is a SARC containing (among many other components)
    /// exactly one <c>Component/ModelInfo/*.engine__component__ModelInfo.bgyml</c>, whose
    /// <c>ModelProjectName</c> + <c>FmdbName</c> fields concatenate into the model's real
    /// <c>Model/&lt;ModelProjectName&gt;.&lt;FmdbName&gt;.bfres.mc</c> path (confirmed against two very
    /// differently-named real actors: Enemy_Dragon_Darkness, where both fields happen to be the
    /// actor's own name, and Enemy_Chuchu_Junior, whose FmdbName - "Chuchu_Plain_Junior" - is
    /// nothing like the actor name, exactly the "doubled name" guessing RomfsPaths.ModelFile has
    /// to fall back on for a caller that only has a bare model name to work with).
    ///
    /// The SAME pack's <c>Component/AnimationParam/*.engine__component__AnimationParam.bgyml</c>
    /// lists <c>AnimationResources[].ModelProjectName</c> - each one is the exact pack prefix of a
    /// sibling <c>Model/&lt;ModelProjectName&gt;.anim.bfres.zs</c> archive holding this actor's real
    /// skeletal anims (idle/walk/attack/...), which is where a creature's actually-useful anim set
    /// lives, not the model's own .bfres.mc - see <see cref="ExportTestBench.ExportExternalAnims"/>'s
    /// remarks for why guessing this from the model name alone (a plain pack-prefix glob) is a
    /// weaker fallback, not the primary path, once an actor pack is available to ask instead.
    /// </summary>
    public static class ActorInfo
    {
        public sealed record Resolved(string ModelName, IReadOnlyList<string> AnimPackNames);

        /// <summary>Null if there's no <c>Pack/Actor/&lt;actorName&gt;.pack.zs</c> at all (not every model - props, some creatures - is a full "Actor").</summary>
        public static Resolved? Resolve(string romfsRoot, string actorName)
        {
            string packPath = RomfsOverlay.Resolve(romfsRoot, "Pack", "Actor", $"{actorName}.pack.zs");
            if (!File.Exists(packPath))
                return null;

            TotkCommon.Totk.Config.GamePath = romfsRoot;
            byte[] raw = File.ReadAllBytes(packPath);
            byte[] decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var sarc = Sarc.FromBinary(new ArraySegment<byte>(decompressed));
            var entryNames = sarc.Select(kv => kv.Key).ToList();

            // A pack can hold several ModelInfo files - an armour piece's leggings pack carries its own and the
            // helmet's - so the one to read is the one the actor's ActorParam points at (ModelInfoRef, possibly
            // inherited), not whichever sorts first. Fields a file lacks come from its $parent.
            var entries = entryNames.ToHashSet(StringComparer.Ordinal);
            string? modelInfoKey = null;
            foreach (var actorParam in ParentChain(sarc, entries, $"Actor/{actorName}.engine__actor__ActorParam.bgyml"))
            {
                if (actorParam.TryGetValue("Components", out var components) && components.Type == BymlNodeType.Map
                    && components.GetMap().TryGetValue("ModelInfoRef", out var reference) && reference.Type == BymlNodeType.String
                    && reference.GetString().Length > 0)
                {
                    modelInfoKey = EntryName(reference.GetString());
                    break;
                }
            }
            string named = $"Component/ModelInfo/{actorName}.engine__component__ModelInfo.bgyml";
            if (modelInfoKey == null || !entries.Contains(modelInfoKey))
                modelInfoKey = entries.Contains(named) ? named
                    : entryNames.FirstOrDefault(k => k.StartsWith("Component/ModelInfo/", StringComparison.Ordinal));
            if (modelInfoKey == null)
            {
                Console.WriteLine($"[ActorInfo] '{actorName}' has a pack but no Component/ModelInfo - can't resolve a model from it.");
                return null;
            }

            // A Component/ModelInfo entry existing is NOT by itself proof it names a real model -
            // confirmed on several "Player*" effect/logic actors (e.g. ExpandElectricField_Player):
            // they carry a ModelInfo entry with neither ModelProjectName nor FmdbName at all (some
            // other, narrower schema this class doesn't need), so indexing those keys unconditionally
            // threw a raw KeyNotFoundException straight out of this method instead of the same clean
            // "nothing to resolve" null the missing-modelInfoKey case just above already returns.
            var modelInfoChain = ParentChain(sarc, entries, modelInfoKey);
            Byml? modelProjectNameNode = null, fmdbNameNode = null;
            foreach (var file in modelInfoChain)
            {
                if (modelProjectNameNode == null && file.TryGetValue("ModelProjectName", out var project)) modelProjectNameNode = project;
                if (fmdbNameNode == null && file.TryGetValue("FmdbName", out var fmdb)) fmdbNameNode = fmdb;
            }
            if (modelProjectNameNode == null || fmdbNameNode == null)
            {
                Console.WriteLine($"[ActorInfo] '{actorName}' has a Component/ModelInfo but no ModelProjectName/FmdbName in it - not a real model.");
                return null;
            }
            string modelProjectName = modelProjectNameNode.GetString();
            string fmdbName = fmdbNameNode.GetString();
            string modelName = $"{modelProjectName}.{fmdbName}";

            var animPackNames = new List<string>();
            string? animParamKey = entryNames.FirstOrDefault(k => k.StartsWith("Component/AnimationParam/", StringComparison.Ordinal));
            if (animParamKey != null)
            {
                var animParam = Byml.FromBinary(sarc[animParamKey].ToArray()).GetMap();
                if (animParam.TryGetValue("AnimationResources", out var resources))
                {
                    foreach (Byml entry in resources.GetArray())
                    {
                        var map = entry.GetMap();
                        if (map.TryGetValue("ModelProjectName", out var mpn))
                            animPackNames.Add(mpn.GetString());
                    }
                }
            }

            Console.WriteLine($"[ActorInfo] '{actorName}' -> model '{modelName}', anim archives: [{string.Join(", ", animPackNames)}]");
            return new Resolved(modelName, animPackNames);
        }

        /// <summary>A parameter file and the ones its <c>$parent</c> names in turn, as far as the pack holds them.</summary>
        private static List<IDictionary<string, Byml>> ParentChain(Sarc sarc, HashSet<string> entries, string name)
        {
            var chain = new List<IDictionary<string, Byml>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? at = name;
            while (at != null && seen.Add(at) && entries.Contains(at))
            {
                IDictionary<string, Byml> file;
                try { file = Byml.FromBinary(sarc[at].ToArray()).GetMap(); }
                catch { break; }
                chain.Add(file);
                at = file.TryGetValue("$parent", out var parent) && parent.Type == BymlNodeType.String && parent.GetString().Length > 0
                    ? EntryName(parent.GetString()) : null;
            }
            return chain;
        }

        /// <summary>A reference as a pack entry name: <c>?Component/X.bgyml</c> is the entry itself, and the authoring path <c>Work/Component/X.gyml</c> is the same file compiled.</summary>
        private static string EntryName(string reference)
        {
            string name = reference.TrimStart('?');
            if (name.StartsWith("Work/", StringComparison.Ordinal)) name = name.Substring("Work/".Length);
            if (name.EndsWith(".gyml", StringComparison.Ordinal)) name = name.Substring(0, name.Length - ".gyml".Length) + ".bgyml";
            return name;
        }
    }
}
