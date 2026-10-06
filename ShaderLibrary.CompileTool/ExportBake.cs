using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BymlLibrary;
using SarcLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// The game's baked lighting for placed static actors: <c>Bake/Scene/*.bkres.zs</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each <c>.bkres</c> is a SARC holding one <c>bkdat.byaml</c> for one bake tile of a map
    /// (<c>MainField_G1_17_7</c>, ~10,300 of them). It names one atlas texture
    /// (<c>TexToGo/RBake_Scene_&lt;tile&gt;_d0_t0.txtg</c>, BC4) and, for every placed actor in the
    /// tile (its <c>Guid</c> is the placement hash in decimal, then <c>_0</c>), every material's
    /// region of that atlas as <c>TexcoordScale</c>/<c>TexcoordOffset</c>, keyed by
    /// <c>OriginalMaterialIndex</c>.
    /// </para>
    /// <para>
    /// That is exactly what the game's static-object shaders read: their <c>aTexCoordBake</c> is
    /// the lightmap UV, scaled and offset either by the material's <c>gsys_bake_st0</c> or - when
    /// the instance's <c>ShpMtx</c> row 8 says so - by a per-instance table entry at
    /// <c>(base + gsys_material_id) * 16</c> bytes of a storage buffer, then sampled from
    /// <c>bake0</c>. Without it every static object samples <c>CmnTex_BakeDefault</c>, a flat 1.0.
    /// </para>
    /// <para>
    /// Which tile a placement's bake is in is not derivable from where it stands, so
    /// <see cref="BuildIndex"/> reads every tile once and records hash -&gt; tile.
    /// <see cref="ExportTile"/> then exports one tile's atlas (its whole mip chain) and table.
    /// </para>
    /// </remarks>
    public static class ExportBake
    {
        const string IndexMagic = "WRSB";
        const int IndexVersion = 1;

        /// <summary>The index file, under the bake directory.</summary>
        public const string IndexFile = "index.bin";

        /// <summary>
        /// Reads every <c>Bake/Scene</c> tile and writes <c>&lt;outDir&gt;/index.bin</c>: the tile names,
        /// then every placement hash sorted with the tile its bake is in. Binary rather than JSON:
        /// it is several hundred thousand entries.
        /// </summary>
        public static void BuildIndex(string romfsRoot, string outDir, int jobs)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;
            string sceneDir = Path.Combine(romfsRoot, "Bake", "Scene");
            string[] files = Directory.GetFiles(sceneDir, "*.bkres.zs");
            Array.Sort(files, StringComparer.Ordinal);
            var tiles = files.Select(TileName).ToArray();

            var entries = new ConcurrentBag<(ulong Hash, int Tile)>();
            Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, i =>
            {
                try
                {
                    foreach (var model in Models(Read(files[i])))
                        if (ParseGuid(model.GetMap()["Guid"].GetString()) is { } hash)
                            entries.Add((hash, i));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ExportBake] skipped {Path.GetFileName(files[i])}: {ex.Message}");
                }
            });

            var sorted = entries.GroupBy(e => e.Hash).Select(g => g.First()).OrderBy(e => e.Hash).ToArray();
            Directory.CreateDirectory(outDir);
            string temp = Path.Combine(outDir, IndexFile + ".tmp");
            using (var w = new BinaryWriter(File.Create(temp), Encoding.UTF8))
            {
                w.Write(Encoding.ASCII.GetBytes(IndexMagic));
                w.Write(IndexVersion);
                w.Write(tiles.Length);
                foreach (string t in tiles)
                    w.Write(t);
                w.Write(sorted.Length);
                foreach (var (hash, tile) in sorted)
                {
                    w.Write(hash);
                    w.Write(tile);
                }
            }
            File.Move(temp, Path.Combine(outDir, IndexFile), overwrite: true);
            Console.WriteLine($"[ExportBake] indexed {sorted.Length} placements over {tiles.Length} tiles");
        }

        /// <summary>
        /// Exports one tile to <c>&lt;outDir&gt;/&lt;tile&gt;.json</c> plus its atlas texture(s) beside it.
        /// The JSON lists the textures, then per placement hash the model and each material's
        /// atlas region: <c>{ "textures": [{ "name", "file", "format", "width", "height" }],
        /// "actors": { "&lt;hash&gt;": { "model", "count", "materials": [{ "name", "index", "texture",
        /// "st": [sx, sy, ox, oy] }] } } }</c>.
        /// </summary>
        public static void ExportTile(string romfsRoot, string tile, string outDir)
        {
            TotkCommon.Totk.Config.GamePath = romfsRoot;
            Directory.CreateDirectory(outDir);
            var root = Read(Path.Combine(romfsRoot, "Bake", "Scene", tile + ".bkres.zs"));

            var textures = new List<(string Name, string File, string Format, int Width, int Height)>();
            var textureIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var actors = new SortedDictionary<ulong, (string Model, int Count, List<(string Name, int Index, int Texture, float[] St)> Materials)>();

            foreach (Byml element in root.GetMap()["DataElements"].GetArray())
            {
                var map = element.GetMap();
                var names = map.TryGetValue("TextureNames", out var tn) ? tn.GetArray().Select(n => n.GetString()).ToList() : new List<string>();
                var local = new int[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    if (!textureIndex.TryGetValue(names[i], out int at))
                    {
                        at = -1;
                        if (TexToGo.Find(romfsRoot, names[i]) is { } path)
                        {
                            var (tex, file) = TexToGo.ExportMipChain(path, names[i], outDir);
                            at = textures.Count;
                            textures.Add((names[i], file, tex.Format.ToString(), tex.Width, tex.Height));
                        }
                        else
                            Console.WriteLine($"[ExportBake] {tile}: texture not found: {names[i]}");
                        textureIndex[names[i]] = at;
                    }
                    local[i] = at;
                }

                if (!map.TryGetValue("ModelElements", out var models))
                    continue;
                foreach (Byml model in models.GetArray())
                {
                    var m = model.GetMap();
                    if (ParseGuid(m["Guid"].GetString()) is not { } hash || actors.ContainsKey(hash))
                        continue;
                    var materials = new List<(string, int, int, float[])>();
                    if (m.TryGetValue("MaterialElements", out var mats))
                    {
                        foreach (Byml mat in mats.GetArray())
                        {
                            var e = mat.GetMap();
                            int texture = e.TryGetValue("TextureIndex", out var ti) ? (int)Num(ti) : 0;
                            var scale = e["TexcoordScale"].GetMap();
                            var offset = e["TexcoordOffset"].GetMap();
                            materials.Add((e["MaterialName"].GetString(), (int)Num(e["OriginalMaterialIndex"]),
                                texture >= 0 && texture < local.Length ? local[texture] : -1,
                                new[] { Num(scale["X"]), Num(scale["Y"]), Num(offset["X"]), Num(offset["Y"]) }));
                        }
                    }
                    int count = m.TryGetValue("OriginalMaterialCount", out var oc) ? (int)Num(oc) : materials.Count;
                    actors[hash] = (m.TryGetValue("ModelName", out var mn) ? mn.GetString() : "", count, materials);
                }
            }

            string temp = Path.Combine(outDir, tile + ".json.tmp");
            using (var stream = File.Create(temp))
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteStartArray("textures");
                foreach (var t in textures)
                {
                    w.WriteStartObject();
                    w.WriteString("name", t.Name);
                    w.WriteString("file", t.File);
                    w.WriteString("format", t.Format);
                    w.WriteNumber("width", t.Width);
                    w.WriteNumber("height", t.Height);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartObject("actors");
                foreach (var (hash, actor) in actors)
                {
                    w.WriteStartObject(hash.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("model", actor.Model);
                    w.WriteNumber("count", actor.Count);
                    w.WriteStartArray("materials");
                    foreach (var mat in actor.Materials)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", mat.Name);
                        w.WriteNumber("index", mat.Index);
                        w.WriteNumber("texture", mat.Texture);
                        w.WriteStartArray("st");
                        foreach (float f in mat.St)
                            w.WriteNumberValue(f);
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            File.Move(temp, Path.Combine(outDir, tile + ".json"), overwrite: true);
        }

        static string TileName(string file) => Path.GetFileName(file)[..^".bkres.zs".Length];

        /// <summary>TotkCommon's shared decompressor is not safe to use from several threads at once.</summary>
        static readonly object ZstdLock = new();

        static Byml Read(string file)
        {
            byte[] raw = File.ReadAllBytes(file);
            byte[] plain;
            lock (ZstdLock)
                plain = TotkCommon.Zstd.IsCompressed(raw) ? TotkCommon.Totk.Zstd.Decompress(raw) : raw;
            var sarc = Sarc.FromBinary(new ArraySegment<byte>(plain));
            return Byml.FromBinary(sarc["bkdat.byaml"].ToArray());
        }

        static IEnumerable<Byml> Models(Byml root)
        {
            foreach (Byml element in root.GetMap()["DataElements"].GetArray())
                if (element.GetMap().TryGetValue("ModelElements", out var models))
                    foreach (Byml model in models.GetArray())
                        yield return model;
        }

        /// <summary>"10033483703293899051_0" -&gt; the placement hash.</summary>
        static ulong? ParseGuid(string guid)
        {
            int cut = guid.IndexOf('_');
            return ulong.TryParse(cut < 0 ? guid : guid[..cut], NumberStyles.None, CultureInfo.InvariantCulture, out ulong hash) ? hash : null;
        }

        static float Num(Byml b) => b.Type switch
        {
            BymlNodeType.Float => b.GetFloat(),
            BymlNodeType.Double => (float)b.GetDouble(),
            BymlNodeType.Int => b.GetInt(),
            BymlNodeType.UInt32 => b.GetUInt32(),
            BymlNodeType.Int64 => b.GetInt64(),
            BymlNodeType.UInt64 => b.GetUInt64(),
            _ => 0f,
        };
    }
}
