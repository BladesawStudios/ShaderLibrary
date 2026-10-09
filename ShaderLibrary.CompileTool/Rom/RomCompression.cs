using System;
using Yaz0Sharp;

namespace ShaderLibrary.CompileTool.Rom
{
    /// <summary>The compression formats the Wild games ship their files in.</summary>
    public static class RomCompression
    {
        public static bool IsCompressed(ReadOnlySpan<byte> data) => Yaz0.IsCompressed(data) || IsZstd(data);

        public static byte[] Decompress(ReadOnlySpan<byte> data)
        {
            if (Yaz0.IsCompressed(data))
                return Yaz0.Decompress(data);
            if (IsZstd(data))
                return TotkCommon.Totk.Zstd.Decompress(data.ToArray());
            return data.ToArray();
        }

        static bool IsZstd(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD;
    }
}
