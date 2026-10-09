using System.Collections.Generic;

namespace ShaderLibrary.CompileTool.Rom
{
    /// <summary>Breath of the Wild's base game, update and DLC folders as one filesystem.</summary>
    public static class BotwRom
    {
        public static IRomAccess Open(string baseRoot, string? updateRoot = null, string? dlcRoot = null)
        {
            var roots = new List<string> { baseRoot };
            if (!string.IsNullOrEmpty(updateRoot))
                roots.Add(updateRoot);
            if (!string.IsNullOrEmpty(dlcRoot))
                roots.Add(dlcRoot);
            return new LayeredRom(roots);
        }
    }
}
