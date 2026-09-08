using System;
using System.IO;
using BfresLibrary;
using ShaderLibrary.CompileTool;

namespace ShaderLibrary.CompilerTool
{
    public static class SamplerChecker
    {
        public static void Check(string romfs)
        {
            foreach (string sword in new[] { "Weapon_Sword_070", "Weapon_Sword_071" })
            {
                string mc = Path.Combine(romfs, "Model", $"{sword}.{sword}.bfres.mc");
                if (!File.Exists(mc)) continue;
                byte[] fres = TestMaterialDump.DecompressBfresMc(mc);
                var res = new ResFile(new MemoryStream(fres), false);
                var model = res.Models[0];
                Console.WriteLine("==================== " + sword + " ====================");
                foreach (var m in model.Materials.Values)
                {
                    int spdLen = m.ShaderParamData?.Length ?? 0;
                    Console.WriteLine($"Material: {m.Name} (ShaderParamData: {spdLen} bytes)");
                    if (spdLen > 0 && Environment.GetEnvironmentVariable("TESTBENCH_DIR") is { } testBenchDir) {
                        File.WriteAllBytes(Path.Combine(testBenchDir, "data", $"{m.Name}_paramdata.bin"), m.ShaderParamData);
                    }
                    for (int i = 0; i < m.TextureRefs.Count; i++)
                    {
                        Console.WriteLine($"  TexRef[{i}]: {m.TextureRefs[i].Name}");
                    }
                    if (m.Samplers != null)
                    {
                        for (int i = 0; i < m.Samplers.Count; i++)
                        {
                            Console.WriteLine($"  Sampler[{i}]: {m.Samplers[i].Name}");
                        }
                    }
                    if (m.ShaderAssign != null)
                    {
                        foreach (var kv in m.ShaderAssign.SamplerAssigns)
                        {
                            Console.WriteLine($"  SamplerAssign: {kv.Key} => {kv.Value}");
                        }
                    }
                }
            }
        }
    }
}
