using System;
using System.Linq;
using System.Reflection;
using BfresLibrary.Switch;
using HarmonyLib;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Runtime patches for two confirmed bugs in the vendored BfresLibrary.dll
    /// (Libs\Bfres\BfresLibrary.dll) that otherwise block parsing ANY TotK v10 material.
    /// Both were found by decompiling BfresLibrary.dll with ilspycmd and both reproduce on a
    /// fully, legitimately decompressed buffer - neither is related to TotK's MCPK/zstd
    /// wrapping or to this tool's decompression step.
    ///
    /// Bug 1: BfresLibrary.Switch.MaterialParserV10.ShaderInfo.Load reads an absolute file
    /// offset for the material's option-toggle bitflag table:
    ///
    ///   _optionBitFlags = loader.LoadCustom(() => loader.ReadInt64s(numBitflags), (uint)num3);
    ///
    /// BfresLibrary's own LoadCustom() returns null (default(T)) whenever that offset is 0 -
    /// which is a normal, valid value for a material with no boolean-type shader options (a very
    /// common case; every TotK material sampled so far hits it). SetupOptionBooleans() is then
    /// called unconditionally and its first line is `_optionBitFlags.ToArray()`, which throws
    /// (ArgumentNullException from Enumerable.ToArray on a null source) whenever the field is
    /// null - i.e. on essentially any real material.
    ///
    /// Fix: prefix-patch SetupOptionBooleans to treat a null _optionBitFlags the same way the
    /// rest of the class already treats "no data at this offset" elsewhere in the file (as all
    /// options being unset), by substituting a zero-filled array of the correct length before
    /// the original method body runs.
    ///
    /// Bug 2: MaterialParserV10.ReadRenderInfo re-reads each RenderInfo's name from a raw table
    /// via an absolute offset (ShaderAssignV10.renderInfoListOffset) instead of the names already
    /// present in the ShaderAssignV10.RenderInfos ResDict<ResString> it parsed moments earlier.
    /// That raw re-read consistently yields an empty string on every real TotK material sampled,
    /// so mat.RenderInfos.Add(renderInfo.Name, renderInfo) collides on the second entry ("Key
    /// \"\" already exists") and the whole file's parse aborts before ever reaching
    /// ReadShaderParams/LoadShaderOptions - the calls this tool actually needs.
    ///
    /// Fix: patch the generic dictionary insert (BfresLibrary.ResDict.Add) to skip (and log) a
    /// duplicate key instead of throwing, so the loop that calls it keeps going far enough to
    /// reach the ShaderParam/ShaderOptions parsing later in the same method.
    ///
    /// IMPORTANT - what these patches do NOT fix: even with both applied, every material sampled
    /// still comes back with implausibly small ShaderOptions/ShaderParams counts (typically 1,
    /// vs. the dozens of fields Shaders/TOTK/Pixel.frag's GsysMaterial block declares) and every
    /// name/key in those tables resolves to "". Decompiling BfresLibrary.Switch.Core
    /// .ResFileSwitchLoader.LoadString (see the MC_DEBUG_LOADSTRING diagnostic below) shows why:
    /// it reads a genuine absolute 64-bit offset, but has a silent bounds guard -
    /// `if (num < 0 || num > BaseStream.Length) return "";` - meaning these particular string
    /// offsets are landing beyond the end of what this tool decompressed from the .bfres.mc file.
    /// Whether that's because the real content lives in a part of the MCPK stream this tool
    /// hasn't located, or because BfresLibrary mis-locates it via an offset field that's
    /// interpreted differently in TotK's specific v10 sub-format, is NOT resolved - see
    /// TestMaterialDump.cs's remarks and this task's final report for the full writeup. These two
    /// patches only get parsing far enough to reach real Model/Material/ShaderParam objects
    /// without crashing; they do not make the per-field data trustworthy.
    /// </summary>
    public static class BfresLibraryPatches
    {
        static bool _applied;

        public static void EnsureApplied()
        {
            if (_applied)
                return;
            _applied = true;

            var harmony = new Harmony("ShaderLibrary.CompileTool.BfresLibraryPatches");

            Type shaderInfoType = typeof(MaterialParserV10.ShaderInfo);
            MethodInfo target = shaderInfoType.GetMethod("SetupOptionBooleans", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("BfresLibrary.Switch.MaterialParserV10+ShaderInfo.SetupOptionBooleans not found - BfresLibrary.dll layout changed, patch needs updating");

            MethodInfo prefix = typeof(BfresLibraryPatches).GetMethod(nameof(SetupOptionBooleansPrefix), BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(nameof(SetupOptionBooleansPrefix));

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            // Second bug (also confirmed against a fully, legitimately decompressed buffer, so
            // also unrelated to MCPK/zstd): MaterialParserV10.ReadRenderInfo re-reads each
            // RenderInfo's name from a raw table via an absolute offset
            // (ShaderAssignV10.renderInfoListOffset) instead of using the names already present
            // in the ShaderAssignV10.RenderInfos ResDict<ResString> it parsed moments earlier.
            // On every real TotK material sampled, that raw re-read consistently yields an empty
            // string, so mat.RenderInfos.Add(renderInfo.Name, renderInfo) collides on the second
            // entry ("Key \"\" already exists") and the whole file's parse aborts before ever
            // reaching ReadShaderParams/LoadShaderOptions - the calls this tool actually needs.
            // Rather than reverse-engineer BfresLibrary's offset math for a field this tool
            // doesn't use, patch the generic dictionary insert (BfresLibrary.ResDict.Add) to skip
            // (and log) a duplicate key instead of throwing, so the loop that calls it keeps
            // going and reaches the ShaderParam/ShaderOptions parsing later in the same method.
            // This is a narrower, more honest compromise than papering over ReadRenderInfo's own
            // offset math: RenderInfos for affected materials may come out incomplete (only the
            // first entry per colliding name survives), which this tool flags loudly rather than
            // silently presenting as complete - but ShaderAssign/ShaderOptions/ShaderParams
            // (this tool's actual subject) are read afterward, unaffected.
            Type resDictType = typeof(BfresLibrary.ResDict);
            MethodInfo addTarget = resDictType.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(string), typeof(BfresLibrary.Core.IResData) }, null)
                ?? throw new MissingMethodException("BfresLibrary.ResDict.Add(string, IResData) not found - BfresLibrary.dll layout changed, patch needs updating");

            MethodInfo addPrefix = typeof(BfresLibraryPatches).GetMethod(nameof(ResDictAddPrefix), BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(nameof(ResDictAddPrefix));

            harmony.Patch(addTarget, prefix: new HarmonyMethod(addPrefix));

            // Bug 3 (root cause, found via Ghidra against TotK's own NSO - see
            // ExternalBinaryStringTable.cs remarks): RenderInfo/ShaderParam/Option names in V10
            // materials are NOT offsets into the material's own .bfres at all. They're 64-bit keys
            // into a single shared romfs file, Shader/ExternalBinaryString.bfres(.mc), which the
            // game resolves via nn::g3d2::ResFile::RelocateExternalStrings (binary search over a
            // sorted key array, then a parallel string-pool-offset array at the same index).
            // BfresLibrary.Switch.Core.ResFileSwitchLoader.LoadString has no idea this second file
            // exists, so its `num > BaseStream.Length` bounds guard fires and it silently returns
            // "" for every one of these keys - which is also why the ResDict.Add duplicate-skip
            // patch above was needed (all colliding on the same "" key) before this fix existed.
            //
            // Fix: prefix-patch LoadString to peek at the raw offset it's about to read. If it's a
            // plausible in-file offset (0, or in [0, BaseStream.Length]), rewind and let the
            // original method run exactly as before (this covers Model/Material names,
            // AttributeAssign/SamplerAssign, and everything else that already worked). Otherwise
            // treat it as one of these external keys, resolve it via ExternalBinaryStringTable,
            // and skip the original method entirely.
            MethodInfo lsTarget = typeof(BfresLibrary.Switch.Core.ResFileSwitchLoader).GetMethod(
                    "LoadString", BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(System.Text.Encoding) }, null)
                ?? throw new MissingMethodException("BfresLibrary.Switch.Core.ResFileSwitchLoader.LoadString not found - BfresLibrary.dll layout changed, patch needs updating");

            MethodInfo lsPrefix = typeof(BfresLibraryPatches).GetMethod(nameof(LoadStringPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
            harmony.Patch(lsTarget, prefix: new HarmonyMethod(lsPrefix));

            // Opt-in diagnostic (set MC_DEBUG_LOADSTRING=1): logs the first N calls to LoadString
            // and what they resolved to (in-file or external-key), for debugging.
            if (Environment.GetEnvironmentVariable("MC_DEBUG_LOADSTRING") == "1")
            {
                MethodInfo lsPost = typeof(BfresLibraryPatches).GetMethod(nameof(LoadStringPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
                harmony.Patch(lsTarget, postfix: new HarmonyMethod(lsPost));
            }

            if (Environment.GetEnvironmentVariable("MC_DEBUG_SHADERASSIGN") == "1")
            {
                Type saType = typeof(MaterialParserV10.ShaderAssignV10);
                MethodInfo saLoad = saType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.EndsWith(".Load"))
                    ?? throw new MissingMethodException("MaterialParserV10.ShaderAssignV10's explicit IResData.Load not found via reflection - BfresLibrary.dll layout changed, patch needs updating");
                MethodInfo saPost = typeof(BfresLibraryPatches).GetMethod(nameof(ShaderAssignV10LoadPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
                harmony.Patch(saLoad, postfix: new HarmonyMethod(saPost));
            }

            // Opt-in diagnostic (set MC_DEBUG_SHADEROPTIONS=1) for the option-value bug: some
            // boolean-shaped shader options (2 valid choices in the .bfsha) come out of
            // LoadShaderOptions with values like "40"/"2395356617" instead of "True"/"False".
            // Patches ShaderInfo's own explicit IResData.Load (a nested class of
            // MaterialParserV10, same reflection trick as ShaderAssignV10LoadPostfix above) to
            // dump OptionToggles/OptionValues/OptionIndices right after they're populated -
            // MaterialParserV10.LoadShaderOptions itself isn't patchable this way since `info`
            // there is a local variable, not a field Harmony can see from outside the method.
            if (Environment.GetEnvironmentVariable("MC_DEBUG_SHADEROPTIONS") == "1")
            {
                Type siType = typeof(MaterialParserV10.ShaderInfo);
                MethodInfo siLoad = siType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.EndsWith(".Load"))
                    ?? throw new MissingMethodException("MaterialParserV10.ShaderInfo's explicit IResData.Load not found via reflection - BfresLibrary.dll layout changed, patch needs updating");
                MethodInfo siPost = typeof(BfresLibraryPatches).GetMethod(nameof(ShaderInfoLoadPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
                harmony.Patch(siLoad, postfix: new HarmonyMethod(siPost));
            }
        }

        static void ShaderInfoLoadPostfix(object __instance)
        {
            var info = (MaterialParserV10.ShaderInfo)__instance;
            int boolCount = info.OptionToggles?.Length ?? 0;
            int valueCount = info.OptionValues?.Count ?? 0;
            Console.WriteLine($"    [ShaderInfo] OptionToggles({boolCount})=[{string.Join(",", info.OptionToggles ?? Array.Empty<bool>())}] " +
                $"OptionValues({valueCount})=[{string.Join(",", info.OptionValues ?? Array.Empty<string>())}] " +
                $"OptionIndices({info.OptionIndices?.Length ?? -1})=[{string.Join(",", info.OptionIndices ?? Array.Empty<short>())}]");

            if (info.ShaderAssign != null)
            {
                for (int j = 0; j < info.ShaderAssign.Options.Count; j++)
                {
                    short idx = info.OptionIndices != null && info.OptionIndices.Length > 0 ? info.OptionIndices[j] : (short)j;
                    string key = info.ShaderAssign.Options.GetKey(j);
                    Console.WriteLine($"      Options[{j}] name=\"{key}\" -> idx={idx} (boolCount={boolCount}, in-range={idx >= 0 && idx < boolCount + valueCount})");
                }
            }
        }

        static readonly FieldInfo RenderInfoListOffsetField =
            typeof(MaterialParserV10.ShaderAssignV10).GetField("renderInfoListOffset", BindingFlags.NonPublic | BindingFlags.Instance)!;
        static readonly FieldInfo ShaderParamOffsetField =
            typeof(MaterialParserV10.ShaderAssignV10).GetField("shaderParamOffset", BindingFlags.NonPublic | BindingFlags.Instance)!;

        static void ShaderAssignV10LoadPostfix(object __instance, object loader)
        {
            var sa = (MaterialParserV10.ShaderAssignV10)__instance;
            long pos = ((Syroot.BinaryData.BinaryDataReader)loader).Position;
            ulong renderInfoListOffset = (ulong)RenderInfoListOffsetField.GetValue(__instance)!;
            ulong shaderParamOffset = (ulong)ShaderParamOffsetField.GetValue(__instance)!;
            Console.WriteLine($"    [ShaderAssignV10] endPos={pos} ShaderArchiveName=\"{sa.ShaderArchiveName}\" ShadingModelName=\"{sa.ShadingModelName}\" " +
                $"renderInfoListOffset={renderInfoListOffset} shaderParamOffset={shaderParamOffset} " +
                $"RenderInfos.Count={sa.RenderInfos.Count} ShaderParameters.Count={sa.ShaderParameters.Count} " +
                $"AttributeAssign.Count={sa.AttributeAssign.Count} SamplerAssign.Count={sa.SamplerAssign.Count} Options.Count={sa.Options.Count} " +
                $"RenderInfoCount(field)={sa.RenderInfoCount} ParamCount(field)={sa.ParamCount} ShaderParamSize={sa.ShaderParamSize}");
        }

        /// <summary>
        /// Runs before the original LoadString(Encoding). Peeks the raw 8-byte offset it's about
        /// to consume; if it looks like a genuine in-file offset, rewinds and lets the original
        /// method do its normal thing. Otherwise resolves it as an ExternalBinaryString key and
        /// skips the original entirely. See the Bug 3 remarks above EnsureApplied for why this
        /// exists.
        /// </summary>
        static bool LoadStringPrefix(object __instance, ref string? __result)
        {
            var reader = (Syroot.BinaryData.BinaryDataReader)__instance;
            long startPos = reader.Position;
            long offset = reader.ReadInt64();

            if (offset == 0 || (offset > 0 && offset <= reader.BaseStream.Length))
            {
                reader.Position = startPos;
                return true; // run original
            }

            __result = ExternalBinaryStringTable.Lookup(unchecked((ulong)offset)) ?? "";
            return false; // skip original - we already consumed the 8-byte offset field
        }

        static int _loadStringCalls;
        static void LoadStringPostfix(object __instance, string __result)
        {
            if (_loadStringCalls++ >= 400) return;
            long pos = ((Syroot.BinaryData.BinaryDataReader)__instance).Position;
            Console.WriteLine($"    [LoadString#{_loadStringCalls}] pos={pos} result=\"{__result}\"");
        }

        static readonly FieldInfo BitFlagsField =
            typeof(MaterialParserV10.ShaderInfo).GetField("_optionBitFlags", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("BfresLibrary.Switch.MaterialParserV10+ShaderInfo._optionBitFlags not found - BfresLibrary.dll layout changed, patch needs updating");

        /// <summary>
        /// Runs before the original SetupOptionBooleans(int count). If the private
        /// _optionBitFlags field is null (offset-was-zero case described above), replace it with
        /// a correctly-sized all-zero array so the original method's `.ToArray()` call and its
        /// internal consistency check both succeed, producing an all-false OptionToggles array -
        /// i.e. "this material sets none of its boolean shader options", which is what a null/0
        /// offset means everywhere else BfresLibrary uses the same LoadCustom() convention.
        /// </summary>
        static void SetupOptionBooleansPrefix(object __instance, int count)
        {
            if (BitFlagsField.GetValue(__instance) is null)
            {
                int numBitflags = 1 + count / 64;
                BitFlagsField.SetValue(__instance, new long[numBitflags]);
            }
        }

        static readonly MethodInfo ContainsKeyMethod =
            typeof(BfresLibrary.ResDict).GetMethod("ContainsKey", new[] { typeof(string) })
            ?? throw new MissingMethodException("BfresLibrary.ResDict.ContainsKey(string) not found");

        /// <summary>
        /// Runs before ResDict.Add(string key, IResData value). Returning false skips the
        /// original method entirely (no insert, no throw) - used only to survive the
        /// ReadRenderInfo bug described above. Logs so a skipped/lost entry is visible rather
        /// than silently dropped.
        /// </summary>
        static bool ResDictAddPrefix(object __instance, string key, object value)
        {
            bool exists = (bool)ContainsKeyMethod.Invoke(__instance, new object[] { key })!;
            if (exists)
            {
                Console.WriteLine($"    [BfresLibrary patch] skipped duplicate ResDict key \"{key}\" ({value?.GetType().Name}) - see BfresLibraryPatches.cs");
                return false;
            }
            return true;
        }
    }
}
