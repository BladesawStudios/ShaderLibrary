using Ryujinx.Graphics.Shader.Translation;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace EffectLibraryTest
{
    public class TegraShaderTranslator
    {
        public static string Decompile(Span<byte> bytecode)
        {
            return TranslateShader(bytecode.Slice(48, bytecode.Length - 48).ToArray(), []);
        }

        /// <summary>
        /// Decompiles with the shader's immediate constants (the bytes its control shader locates after the code), which a program that
        /// branches through a jump table reads its targets from; without them the table is zeros and the branch resolves to nothing.
        /// </summary>
        public static string Decompile(Span<byte> bytecode, byte[] immediateConstants)
        {
            return TranslateShader(bytecode.Slice(48, bytecode.Length - 48).ToArray(), immediateConstants);
        }

        static string TranslateShader(byte[] data, byte[] constants)
        {
            TranslationFlags flags = TranslationFlags.DebugMode;

            TranslationOptions translationOptions = new TranslationOptions(TargetLanguage.Glsl, TargetApi.OpenGL, flags);
            ShaderProgram program = Translator.CreateContext(0, new GpuAccessor(data, constants), translationOptions).Translate();
            return program.Code;
        }

        private class GpuAccessor : IGpuAccessor
        {
            private readonly byte[] _data;
            private readonly byte[] _constants;

            public GpuAccessor(byte[] data, byte[] constants)
            {
                _data = data;
                _constants = constants;
            }

            public uint ConstantBuffer1Read(int offset)
            {
                return offset >= 0 && offset + 4 <= _constants.Length ? BitConverter.ToUInt32(_constants, offset) : 0;
            }

            public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
            {
                return MemoryMarshal.Cast<byte, ulong>(new ReadOnlySpan<byte>(_data).Slice((int)address));
            }
        }
    }
}
