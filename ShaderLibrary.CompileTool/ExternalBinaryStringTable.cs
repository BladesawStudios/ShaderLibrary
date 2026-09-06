using System;
using System.Collections.Generic;
using System.IO;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// TotK's V10 materials store RenderInfo/ShaderParam/Option names as a 64-bit key into a
    /// SHARED, file-external string table - not as an offset into the material's own .bfres.
    /// AttributeAssign/SamplerAssign names still use plain in-file string-pool offsets and are
    /// unaffected; this table only comes into play when BfresLibraryPatches' LoadString patch
    /// sees an offset that fails the normal in-file bounds check.
    ///
    /// Found by decompiling TotK's main NSO in Ghidra: nn::g3d2::ResFile::RelocateExternalStrings
    /// is called from engine::resource::ModelResource::initialize_ with a table obtained via
    /// engine::graphics::GraphicsModule::getExternalBinaryString(), which loads exactly one romfs
    /// file: "Shader/ExternalBinaryString.bfres" (shipped compressed as
    /// Shader/ExternalBinaryString.bfres.mc, same MCPK container as every other .bfres.mc). The
    /// relocation function does a straightforward ascending binary search of a 64-bit key against
    /// a sorted array, then reads a string-pool offset out of a parallel array at the same index -
    /// no hashing involved despite the key looking like hash/garbage data at a glance.
    ///
    /// This file's raw layout (verified by direct byte inspection against known-good matches like
    /// key 1198123914443320372 -> "p_indirect_scale0", a real GsysMaterial field from
    /// Shaders/TOTK/Pixel.frag):
    ///   +192: u64 absolute file offset of the entries array (this is what bounds the key array)
    ///   224:  sorted array of u64 keys, ascending, starting with a 0 sentinel key, running up to
    ///           the entries-array offset above - so the element count is (entriesBase-224)/8
    ///   entries array: parallel array of 16-byte entries at the offset from +192; the entry's
    ///           first 8 bytes are a plain absolute file offset into this same file's string pool
    ///   string pool: same u16-length-prefixed-then-inline-chars format BfresLibrary already
    ///           reads correctly for AttributeAssign/SamplerAssign (LoadString handles this once
    ///           given a valid offset - this class only needs to resolve key -> offset)
    /// </summary>
    public static class ExternalBinaryStringTable
    {
        /// <summary>Must be set before the first Lookup call (RunDemo sets it from its romfsRoot arg).</summary>
        public static string? RomfsRoot;

        /// <summary>
        /// First byte of the sorted u64 key array. This is 224, NOT 232: the array's first
        /// element is a 0 sentinel key, and starting the old "scan while the sequence keeps
        /// increasing" heuristic at 232 silently skipped it, so every key resolved to the string
        /// belonging to the PREVIOUS key - an off-by-one on every name this table produces.
        /// See EnsureLoaded for how the boundary is now derived instead of scanned.
        /// </summary>
        const long KeyArrayStart = 224;

        /// <summary>
        /// Offset of the u64 field that holds the entries array's absolute file offset. The key
        /// array runs from <see cref="KeyArrayStart"/> up to exactly that offset, which makes the
        /// element count derivable rather than guessed.
        /// </summary>
        const long EntriesBaseOffsetField = 192;

        const int EntryStride = 16;

        static byte[]? _data;
        static ulong[]? _keys;
        static long _entriesBase;
        static readonly Dictionary<ulong, string?> _cache = new();

        public static string? Lookup(ulong key)
        {
            if (_cache.TryGetValue(key, out string? cached))
                return cached;

            EnsureLoaded();
            string? result = null;
            if (_keys != null)
            {
                int idx = Array.BinarySearch(_keys, key);
                if (idx >= 0)
                {
                    long entryOff = _entriesBase + idx * EntryStride;
                    long strOff = BitConverter.ToInt64(_data!, (int)entryOff);
                    result = ReadPascalString(strOff);
                }
            }
            _cache[key] = result;
            return result;
        }

        static string? ReadPascalString(long offset)
        {
            if (_data == null || offset <= 0 || offset + 2 > _data.Length)
                return null;
            ushort len = BitConverter.ToUInt16(_data, (int)offset);
            long start = offset + 2;
            if (start + len > _data.Length)
                return null;
            return System.Text.Encoding.UTF8.GetString(_data, (int)start, len);
        }

        static void EnsureLoaded()
        {
            if (_data != null)
                return;

            if (string.IsNullOrEmpty(RomfsRoot))
                throw new InvalidOperationException(
                    $"{nameof(ExternalBinaryStringTable)}.{nameof(RomfsRoot)} must be set before resolving TotK V10 material names.");

            string path = Path.Combine(RomfsRoot!, "Shader", "ExternalBinaryString.bfres.mc");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"TotK's shared string table not found at '{path}' - needed to resolve V10 RenderInfo/ShaderParam/Option names.", path);

            byte[] data = TestMaterialDump.DecompressBfresMc(path);

            // The entries array's start is stored in the header, and the key array runs from
            // KeyArrayStart right up to it - so both the boundary and the element count are read
            // from the file rather than inferred.
            //
            // This replaces an earlier "count keys by scanning while the values keep increasing,
            // starting at 232" heuristic. That heuristic produced a count one short (the array's
            // leading 0 sentinel key sits at 224, below where it started looking), which shifted
            // every key/entry pairing by one record: each key resolved to the name belonging to
            // the key before it. Concretely, the key stored for DeferredMain's material option
            // slot 20 resolved to "p_dynamic_water_specular_ao_in_cave" instead of
            // "o_material_behave". Verified against SystemModel.DeferredMain.bfres: with this
            // boundary, all 36 of a deferred material's option names resolve to real
            // system_shading options whose choice lists contain the material's stored values;
            // with the old one, only 2 of 36 did.
            long entriesBase = BitConverter.ToInt64(data, (int)EntriesBaseOffsetField);
            if (entriesBase <= KeyArrayStart || entriesBase > data.Length || (entriesBase - KeyArrayStart) % 8 != 0)
                throw new InvalidDataException(
                    $"ExternalBinaryString entries offset at +{EntriesBaseOffsetField} is {entriesBase}, which is not a valid " +
                    $"key-array end for a {data.Length}-byte table - file layout differs from what this reader expects.");

            int count = (int)((entriesBase - KeyArrayStart) / 8);
            var keys = new ulong[count];
            for (int i = 0; i < count; i++)
                keys[i] = BitConverter.ToUInt64(data, (int)(KeyArrayStart + i * 8));

            _data = data;
            _keys = keys;
            _entriesBase = entriesBase;
        }

        /// <summary>
        /// Diagnostic: dumps the whole resolved table (sorted key index, 64-bit key, string) so a
        /// name that comes back wrong can be checked against what the table actually holds - i.e.
        /// distinguishing "we read the wrong key" from "we resolved the right key to the wrong
        /// string".
        /// </summary>
        public static void DumpAll(string outPath)
        {
            EnsureLoaded();
            using var w = new StreamWriter(outPath);
            w.WriteLine("index,key,string");
            for (int i = 0; i < _keys!.Length; i++)
            {
                long entryOff = _entriesBase + i * EntryStride;
                long strOff = BitConverter.ToInt64(_data!, (int)entryOff);
                w.WriteLine($"{i},{_keys[i]},{ReadPascalString(strOff)}");
            }
            Console.WriteLine($"  -> Wrote external string table ({_keys.Length} keys, entriesBase={_entriesBase}): {outPath}");
        }


        /// <summary>Diagnostic: hex/interpretation dump around the key-array to entries-array boundary.</summary>
        public static void DumpBoundary()
        {
            EnsureLoaded();
            long keyEnd = KeyArrayStart + _keys!.Length * 8;
            Console.WriteLine($"  [ExtStr] file={_data!.Length} bytes, KeyArrayStart={KeyArrayStart}, keys={_keys.Length}, keyArrayEnd={keyEnd}");
            Console.WriteLine($"  [ExtStr] last 3 keys: {_keys[_keys.Length - 3]}, {_keys[_keys.Length - 2]}, {_keys[_keys.Length - 1]}");
            Console.WriteLine($"  [ExtStr] u64 at keyArrayEnd+0/8/16/24: {BitConverter.ToUInt64(_data, (int)keyEnd)}, {BitConverter.ToUInt64(_data, (int)keyEnd + 8)}, {BitConverter.ToUInt64(_data, (int)keyEnd + 16)}, {BitConverter.ToUInt64(_data, (int)keyEnd + 24)}");
            for (int k = 0; k < 4; k++)
            {
                long off = keyEnd + k * 16;
                long strOff = BitConverter.ToInt64(_data, (int)off);
                long extra = BitConverter.ToInt64(_data, (int)off + 8);
                Console.WriteLine($"  [ExtStr] entry-slot {k} @{off}: strOff={strOff} extra={extra} -> \"{ReadPascalString(strOff)}\"");
            }
            // Where does the entries array end if it has _keys.Length records starting at each candidate base?
            foreach (long baseOff in new[] { keyEnd, keyEnd + 16 })
                Console.WriteLine($"  [ExtStr] if entriesBase={baseOff}: array ends at {baseOff + _keys.Length * 16}, first string-pool bytes there = len {BitConverter.ToUInt16(_data, (int)(baseOff + _keys.Length * 16))}");
        }

    }
}