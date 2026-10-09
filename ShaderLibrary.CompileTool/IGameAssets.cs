using System.Collections.Generic;
using BfresLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>Where a game keeps the files a model export reads.</summary>
    public interface IGameAssets
    {
        /// <summary>The model's decompressed BFRES, or null with the reason logged.</summary>
        byte[]? ReadModel(string modelName);

        // Tie textures to the model name to avoid any issues I forsee happening
        TextureHandle? FindTexture(string modelName, string name);

        /// <summary>The animation archives that belong to the model; <paramref name="packNames"/> are exact names when the actor lists them.</summary>
        IEnumerable<ResFile> AnimationArchives(string modelName, IReadOnlyList<string>? packNames);
    }
}
