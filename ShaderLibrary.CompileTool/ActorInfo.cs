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
            string packPath = Path.Combine(romfsRoot, "Pack", "Actor", $"{actorName}.pack.zs");
            if (!File.Exists(packPath))
                return null;

            TotkCommon.Totk.Config.GamePath = romfsRoot;
            byte[] raw = File.ReadAllBytes(packPath);
            byte[] decompressed = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var sarc = Sarc.FromBinary(new ArraySegment<byte>(decompressed));
            var entryNames = sarc.Select(kv => kv.Key).ToList();

            string? modelInfoKey = entryNames.FirstOrDefault(k => k.StartsWith("Component/ModelInfo/", StringComparison.Ordinal));
            if (modelInfoKey == null)
            {
                Console.WriteLine($"[ActorInfo] '{actorName}' has a pack but no Component/ModelInfo - can't resolve a model from it.");
                return null;
            }

            var modelInfo = Byml.FromBinary(sarc[modelInfoKey].ToArray()).GetMap();
            string modelProjectName = modelInfo["ModelProjectName"].GetString();
            string fmdbName = modelInfo["FmdbName"].GetString();
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
    }
}
