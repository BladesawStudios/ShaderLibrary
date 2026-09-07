using EffectLibraryTest;
using ShaderLibrary.CompileTool;
using ShaderLibrary.Test;
using System;
using System.IO;
using System.Linq;
using BfresLibrary;
using ShaderLibrary;

namespace ShaderLibrary.CompilerTool
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            // "--system-shading" runs only the deferred/system-archive analysis (see
            // TestSystemShading) and skips the material-archive decompile + sword export, which
            // are slow and unrelated to the deferred half of the pipeline.
            bool systemShadingOnly = args.Contains("--system-shading");
            bool aglShader = args.Contains("--agl-shader");
            // "--verify-lookup" runs the real (non-overridden) shader-variant search for every
            // shape in the Master Sword model, which is how a program-index regression shows up.
            bool verifyLookup = args.Contains("--verify-lookup");
            var positional = args.Where(a => !a.StartsWith("--")).ToArray();

            string romfsRoot = positional.Length > 0
                ? positional[0]
                : @"C:\Users\dylan\shenanigans\Personal\Nintendo\Switch\Zelda TotK\RE\Romfs121";

            ExternalBinaryStringTable.RomfsRoot = romfsRoot;
            BfresLibraryPatches.EnsureApplied();

            // "--reexport-geometry <ActorOrModelName> [outDir]" re-runs ONLY step 1 of the prepare
            // pipeline (geometry + textures + skeleton + anims) over an already-prepared model,
            // straight into Marrow's own cache. Steps 2 and 3 (material UBOs, manifest + shader
            // decompile) depend on the material archive, not on the skeleton, so re-running them
            // after a skeleton/anim export fix is pure wasted minutes.
            if (args.Contains("--reexport-geometry"))
            {
                string actorOrModel = positional.Length > 1 ? positional[1] : "Animal_Bass";
                var actor = ActorInfo.Resolve(romfsRoot, actorOrModel);
                string resolved = actor?.ModelName ?? actorOrModel;
                string reexportDir = positional.Length > 2
                    ? positional[2]
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "Marrow", "cache", resolved);
                Directory.CreateDirectory(reexportDir);
                Console.WriteLine($"[reexport] {actorOrModel} -> model '{resolved}' -> {reexportDir}");
                ExportTestBench.ExportModel(romfsRoot, resolved, reexportDir, actor?.AnimPackNames);
                return;
            }

            // "--rebuild-matubo <ActorOrModelName>" re-runs ONLY step 2 of prepare (the
            // gsys_material blocks and their parameter-layout sidecars) into Marrow's own cache -
            // the step a shader parameter anim depends on, and the one --reexport-geometry skips.
            if (args.Contains("--rebuild-matubo"))
            {
                string actorOrModel = positional.Length > 1 ? positional[1] : "Enemy_Dragon_Darkness";
                var matActor = ActorInfo.Resolve(romfsRoot, actorOrModel);
                string matModel = matActor?.ModelName ?? actorOrModel;
                string? matMc = RomfsPaths.ModelFile(romfsRoot, matModel);
                if (matMc == null)
                {
                    Console.WriteLine($"[rebuild-matubo] {RomfsPaths.Explain(romfsRoot, matModel)}");
                    return;
                }
                string matOut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                             "Marrow", "cache", matModel, "matubo");
                Directory.CreateDirectory(matOut);
                BuildMaterialUbo.Run(
                    Path.Combine(romfsRoot, "Shader", "material.Product.110.product.Nin_NX_NVN.bfsha"),
                    matMc, matOut);
                return;
            }

            // "--rebuild-deferred-materials" rebuilds the SHARED deferred-resolve-pass
            // gsys_material blocks (chara_skin, chara_hair, ...) into Marrow's cache - see
            // BuildMaterialUbo.RunSystemDeferred's remarks for why this step was missing from the
            // pipeline entirely until now. One shared archive/model, not per-model like
            // --rebuild-matubo above.
            if (args.Contains("--rebuild-deferred-materials"))
            {
                string deferredOutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                             "Marrow", "cache", "_deferred_materials");
                BuildMaterialUbo.RunSystemDeferred(romfsRoot, deferredOutDir);
                return;
            }

            if (args.Contains("--inspect-matanim"))
            {
                string target = positional.Length > 1 ? positional[1] : "Enemy_Dragon_Darkness";
                MaterialAnimInspector.Inspect(romfsRoot, target);
                return;
            }

            if (args.Contains("--inspect-skeleton"))
            {
                string model = positional.Length > 1 ? positional[1] : "Enemy_Giant";
                ModelInspector.InspectSkeleton(romfsRoot, model);
                return;
            }

            // "--extract-system-textures" pulls real static assets behind the "system" texture
            // names compiled shaders reference (currently just cTex_Proc3DNoise - see
            // SystemTextures.cs's remarks for why the rest are dynamic render targets with no
            // romfs file to extract) into Marrow's shared cache.
            if (args.Contains("--extract-system-textures"))
            {
                string sysOutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                "Marrow", "cache", "_system_textures");
                SystemTextures.ExtractProc3DNoise(romfsRoot, sysOutDir);
                return;
            }

            // "--inspect-bntx <path>" prints every texture in a (plain, already-decompressed)
            // .bntx file's full header - dimensions/depth/array length/mip count/format/dim -
            // for figuring out whether a "system" texture the compiled shaders reference (e.g.
            // cTex_Proc3DNoise, a sampler3D) is a real 3D volume texture asset or something else.
            if (args.Contains("--inspect-bntx"))
            {
                string bntxPath = positional.Length > 0 ? positional[0] : "";
                var bntxFile = new Syroot.NintenTools.NSW.Bntx.BntxFile(bntxPath);
                foreach (var t in bntxFile.Textures)
                {
                    Console.WriteLine($"{t.Name}: {t.Width}x{t.Height}x{t.Depth} array={t.ArrayLength} mips={t.MipCount} format={t.Format} dim={t.Dim} tileMode={t.TileMode} imageSize={t.ImageSize}");
                }
                return;
            }

            if (args.Contains("--test-txtg"))
            {
                string[] names = positional.Length > 1
                    ? positional[1..]
                    : new[] { "Npc_Ganondorf_Miasma_Noise_Gn5", "LeafGray_01_Fca", "Npc_Ganondorf_Miasma_Body_Gn5" };
                TestTxtg.RunDemo(romfsRoot, names);
                return;
            }

            // "--dump-txtg <TextureName> <mip> <outPath>" writes one mip's raw deswizzled block
            // bytes to disk, for offline analysis of a texture's actual channel content.
            if (args.Contains("--dump-txtg"))
            {
                string texName = positional.Length > 1 ? positional[1] : throw new ArgumentException("need a texture name");
                int mip = positional.Length > 2 ? int.Parse(positional[2]) : 0;
                string outPath = positional.Length > 3 ? positional[3] : $"{texName}_mip{mip}.bin";
                TestTxtg.DumpSurface(romfsRoot, texName, mip, outPath);
                return;
            }

            // "--dump-uniform-blocks [bfshaPath] [outDir]" runs TestSystemShading.DumpUniformBlocks
            // against ANY bfsha (not just system.*.bfsha - despite the class name, it's generic
            // BFSHA reflection, nothing system-archive-specific) - defaults to material.*.bfsha,
            // the shared character shading archive every G-buffer/forward program compiles from.
            // Prints every uniform block's real name/offset/size AND its compiled DefaultBuffer
            // value per field - for a "Const"-prefixed field (author-set once, unlike "Dynamic"
            // fields the engine writes by name every frame - see setDynamicShadowParams for that
            // mechanism) this default is very likely the ONLY real value that exists anywhere,
            // since nothing in the executable looks a Const field up by name.
            // "--dump-scene-material" reads the REAL authored values behind gsys_scene_material's
            // "Const" fields (the Blueprint/ghost family among them) straight from
            // Model/SystemModel.SceneMaterial.bfres.mc's own material ShaderParams - see
            // BuildMaterialUbo.DumpSceneMaterial's remarks for how this was traced via Ghidra.
            if (args.Contains("--dump-scene-material"))
            {
                BuildMaterialUbo.DumpSceneMaterial(romfsRoot);
                return;
            }

            if (args.Contains("--dump-uniform-blocks"))
            {
                // positional[0] is already romfsRoot (consumed above) - this flag's own args start at [1].
                string bfshaArg = positional.Length > 1
                    ? positional[1]
                    : Path.Combine(romfsRoot, "Shader", "material.Product.110.product.Nin_NX_NVN.bfsha");
                string outDirArg = positional.Length > 2
                    ? positional[2]
                    : Path.Combine(Path.GetTempPath(), "marrow_uniform_blocks");
                TestSystemShading.Run(bfshaArg, "", outDirArg);
                return;
            }

            if (args.Contains("--dump-bntx"))
            {
                string bntxPath = positional.Length > 0 && File.Exists(positional[0]) ? positional[0] : (positional.Length > 1 ? positional[1] : "temp_icon.bntx");
                var bntx = new Syroot.NintenTools.NSW.Bntx.BntxFile(bntxPath);
                var tex = bntx.Textures[0];
                Console.WriteLine($"Texture: {tex.Name}, {tex.Width}x{tex.Height}, format={tex.Format}");
                byte[] swizzled = tex.TextureData[0][0];
                byte[] deswizzled = TegraX1Deswizzle.Deswizzle(swizzled, TxtgFormat.BC7_UNORM, (int)tex.Height, (int)tex.Width, (int)tex.Height);
                File.WriteAllBytes("vanilla_icon_bc7.bin", deswizzled);
                Console.WriteLine($"Wrote {deswizzled.Length} bytes to vanilla_icon_bc7.bin");
                return;
            }

            // "--material-ubo" rebuilds each material's gsys_material block from romfs (the
            // shader's declared layout + defaults, overlaid with the material's own
            // ShaderParams), replacing the untrustworthy live-captured matsrc_*.bin files.
            if (args.Contains("--material-ubo"))
            {
                string model = positional.Length > 1 ? positional[1] : "Weapon_Sword_070";
                BuildMaterialUbo.Run(
                    Path.Combine(romfsRoot, "Shader", "material.Product.110.product.Nin_NX_NVN.bfsha"),
                    RomfsPaths.ModelFile(romfsRoot, model)
                        ?? throw new FileNotFoundException(RomfsPaths.Explain(romfsRoot, model)),
                    Path.Combine(@"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\data", "matubo"));
                return;
            }

            // "--prepare [ModelName]" is the whole per-model pipeline in one go: geometry and
            // textures, then the material UBOs, then the manifest (which also decompiles each
            // program the model needs). Run this once per model, then view it. The three steps
            // remain available individually for debugging, but they must run in this order -
            // the manifest names files the export produces.
            if (args.Contains("--prepare"))
            {
                string model = positional.Length > 1 ? positional[1] : "Weapon_Sword_070";
                const string benchData = @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\data";
                const string benchShaders = @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\Shaders\Decompiled";
                string materialBfsha = Path.Combine(romfsRoot, "Shader",
                    "material.Product.110.product.Nin_NX_NVN.bfsha");
                string? modelMc = RomfsPaths.ModelFile(romfsRoot, model);
                if (modelMc == null)
                {
                    Console.WriteLine($"[prepare] {RomfsPaths.Explain(romfsRoot, model)}");
                    return;
                }
                Console.WriteLine($"[prepare] resolved to {Path.GetFileName(modelMc)}");

                Console.WriteLine($"[prepare 1/3] geometry + textures for {model}");
                ExportTestBench.ExportModel(romfsRoot, model, benchData);

                Console.WriteLine($"[prepare 2/3] gsys_material UBOs for {model}");
                BuildMaterialUbo.Run(materialBfsha, modelMc, Path.Combine(benchData, "matubo"));

                Console.WriteLine($"[prepare 3/3] manifest + shader decompile for {model}");
                ExportManifest.Run(romfsRoot, materialBfsha, model, benchData, benchShaders);

                Console.WriteLine();
                Console.WriteLine($"[prepare] done. View it with:  python viewer.py {model}");
                return;
            }

            // "--deferred-material-ubo" builds the gsys_material block for each DEFERRED RESOLVE
            // pass, from the DeferredMain system model against system.bfsha. The bench had been
            // feeding those passes an all-zero Mat block; chara_metal survived that, but
            // chara_nonmetal/hair/skin/grossy resolve to pure black without their real constants.
            if (args.Contains("--deferred-material-ubo"))
            {
                const string benchData = @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\data";
                BuildMaterialUbo.Run(
                    Path.Combine(benchData, "system.Product.110.product.Nin_NX_NVN.bfsha"),
                    Path.Combine(benchData, "SystemModel.DeferredMain.bfres"),
                    Path.Combine(benchData, "matubo_deferred"));
                return;
            }

            // "--render-info [ModelName]" dumps the RenderInfo entries that drive shader-variant
            // selection (alpha test, render state) alongside whether the shading model actually
            // declares those options. Alpha cutout lives here, not in ShaderOptions.
            if (args.Contains("--render-info"))
            {
                string model = positional.Length > 1 ? positional[1] : "Weapon_Sword_070";
                string bfsha = Path.Combine(romfsRoot, "Shader", "material.Product.110.product.Nin_NX_NVN.bfsha");
                var arc = new BfshaFile(bfsha);
                string? riPath = RomfsPaths.ModelFile(romfsRoot, model);
                if (riPath == null) { Console.WriteLine(RomfsPaths.Explain(romfsRoot, model)); return; }
                byte[] fres = TestMaterialDump.DecompressBfresMc(riPath);
                using var ms2 = new MemoryStream(fres);
                var rf = new ResFile(ms2, false);
                foreach (var mv in rf.Models[0].Materials)
                {
                    var mat = mv.Value;
                    string sm = mat.ShaderAssign.ShadingModelName;
                    bool hasEnable = arc.ShaderModels.ContainsKey(sm) &&
                        (arc.ShaderModels[sm].StaticOptions.ContainsKey("gsys_alpha_test_enable") ||
                         arc.ShaderModels[sm].DynamicOptions.ContainsKey("gsys_alpha_test_enable"));
                    bool hasFunc = arc.ShaderModels.ContainsKey(sm) &&
                        (arc.ShaderModels[sm].StaticOptions.ContainsKey("gsys_alpha_test_func") ||
                         arc.ShaderModels[sm].DynamicOptions.ContainsKey("gsys_alpha_test_func"));
                    if (args.Contains("--all"))
                    {
                        Console.WriteLine($"== {mat.Name}  (shading {sm})");
                        foreach (var ri in mat.RenderInfos)
                        {
                            object? v = ri.Value.Type switch
                            {
                                RenderInfoType.Int32 => string.Join(",", ri.Value.GetValueInt32s()),
                                RenderInfoType.Single => string.Join(",", ri.Value.GetValueSingles()),
                                _ => string.Join(",", ri.Value.GetValueStrings() ?? new string[0]),
                            };
                            Console.WriteLine($"    {ri.Key,-44} {ri.Value.Type,-7} {v}");
                        }
                        continue;
                    }
                    Console.WriteLine($"{mat.Name,-38} state=\"{mat.GetRenderInfoString("gsys_render_state_mode")}\" " +
                                      $"alphaEnable=\"{mat.GetRenderInfoString("gsys_alpha_test_enable")}\" " +
                                      $"alphaFunc=\"{mat.GetRenderInfoString("gsys_alpha_test_func")}\" " +
                                      $"| shader declares enable={hasEnable} func={hasFunc}");
                }
                return;
            }

            // "--export-model [ModelName]" writes the bench's vertex/index buffers and the
            // textures every material references. Run this before --material-ubo/--manifest for
            // a model the bench has never seen: the manifest names those files but does not
            // produce them.
            if (args.Contains("--export-model"))
            {
                string model = positional.Length > 1 ? positional[1] : "Weapon_Sword_070";
                ExportTestBench.ExportModel(romfsRoot, model,
                    @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\data");
                return;
            }

            // "--manifest [ModelName]" emits the per-shape render manifest (vertex format,
            // sampler units, program indices, deferred pass) so the bench can draw any model.
            if (args.Contains("--manifest"))
            {
                string model = positional.Length > 1 ? positional[1] : "Weapon_Sword_070";
                ExportManifest.Run(romfsRoot,
                    Path.Combine(romfsRoot, "Shader", "material.Product.110.product.Nin_NX_NVN.bfsha"),
                    model,
                    @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\data",
                    @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\Shaders\Decompiled");
                return;
            }

            if (aglShader)
            {
                // agl post-process archive, unpacked from Shader/ApplicationPackage...sarc.zs
                // Positional args after "--agl-shader": [archiveFile] [program names to dump...]
                // With no program names given, this only LISTS what the archive contains -
                // useful for finding a pass by name (e.g. an outline/edge-detect effect) before
                // asking to decompile it.
                string archive = positional.Length > 1 ? positional[1] : "AglShader.sharcb";
                string[] progNames = positional.Length > 2 ? positional[2..] : Array.Empty<string>();
                TestAglShader.Run(Path.Combine(@"C:\Users\dylan\AppData\Local\Temp\claude\C--Users-dylan-repos-Nintending-SHADERS5\cb7a35ce-d20f-4d6a-a8be-626b7e346a83\scratchpad\apppkg", archive),
                    @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\Shaders\Decompiled", progNames);
                return;
            }

            if (verifyLookup)
            {
                TestTOTK.VerifyProgramLookup(
                    Path.Combine(romfsRoot, "Model", "Weapon_Sword_070.Weapon_Sword_070.bfres.mc"),
                    Path.Combine(romfsRoot, "Shader", "material.Product.110.product.Nin_NX_NVN.bfsha"));
                return;
            }

            if (systemShadingOnly)
            {
                const string benchData = @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\data";
                const string benchShaders = @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\Shaders\Decompiled";

                // Optional second positional arg: analyse a different archive instead (e.g. the
                // material archive, to see whether IT names gsys_context/gsys_environment).
                string systemBfsha = positional.Length > 1
                    ? positional[1]
                    : Path.Combine(benchData, "system.Product.110.product.Nin_NX_NVN.bfsha");
                string deferredBfres = Path.Combine(benchData, "SystemModel.DeferredMain.bfres");

                TestSystemShading.Run(systemBfsha, deferredBfres, benchShaders);
                TestSystemShading.DumpDeferredMaterialOptions(systemBfsha, deferredBfres);
                TestSystemShading.DumpProgramOptionMatrix(systemBfsha, "system_shading", benchShaders);
                TestSystemShading.DumpDeferredMaterialOptionVectors(deferredBfres, benchShaders);
                TestSystemShading.DumpOptionOrder(systemBfsha, "system_shading", benchShaders);
                ExternalBinaryStringTable.DumpBoundary();
                ExternalBinaryStringTable.DumpAll(Path.Combine(benchShaders, "external_binary_strings.csv"));
                return;
            }

            string bfshaPath = Path.Combine(romfsRoot, "Shader", "material.Product.110.product.Nin_NX_NVN.bfsha");
            string outDir = @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\Shaders\Decompiled";
            if (File.Exists(bfshaPath))
                DecompilerRunner.Run(romfsRoot, bfshaPath, outDir);

            TestGsysUbo.RunDemo(Path.Combine(romfsRoot, "Model", "Weapon_Sword_070.Weapon_Sword_070.bfres.mc"));

            Console.WriteLine();
            Console.WriteLine("[Exporting Master Sword 3D mesh and textures to TestBench/data]");
            ExportTestBench.ExportSword(romfsRoot, @"C:\Users\dylan\repos\Nintending\SHADERS5\TestBench\data");
        }
    }
}