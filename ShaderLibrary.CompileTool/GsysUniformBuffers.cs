using System;
using System.Collections.Generic;
using System.IO;
using BfresLibrary;

namespace ShaderLibrary.CompileTool
{
    /// <summary>
    /// Builds the three non-material UBOs Shaders/TOTK/Pixel.frag declares (GsysContext,
    /// GsysShape, GsysEnvironment) as byte-exact std140 buffers, ready to upload directly to a
    /// GL_UNIFORM_BUFFER at bindings 2/5/6 respectively (matching the shader's own `layout
    /// (binding = N, std140)` declarations).
    ///
    /// Unlike GsysMaterial (built entirely from real per-material ShaderParam data, see
    /// TestMaterialDump.cs), none of these three are material-authored data - they're driven by
    /// the engine per-frame from camera/scene/instance state that doesn't live in any romfs file.
    /// What's REAL here: GsysShape's skin count (Shape.VertexSkinCount) and its identity
    /// transform (representation-independent - see BuildGsysShape remarks). What's a labeled
    /// PLACEHOLDER pending live-capture cross-reference (sub-project A, UboDumpExeMod): the
    /// camera framing in GsysContext and the light/fog values in GsysEnvironment. Both are
    /// internally consistent and byte-correct, but their exact numeric convention (row- vs
    /// column-vector matrices, NDC depth range, real in-game light color/direction) is not
    /// verified against anything the game itself produced.
    /// </summary>
    public static class GsysUniformBuffers
    {
        // ---- std140 writer -------------------------------------------------------------------

        /// <summary>
        /// Minimal std140 buffer writer. Only implements the alignment rules this file's structs
        /// actually use: vec4 (16-byte aligned, 16 bytes), float (4 bytes, no realignment needed
        /// here since every float in these structs is already inside a vec4-sized slot), and
        /// matCxR types as C columns of vec4 (std140 always rounds a matrix column up to vec4
        /// size/alignment regardless of its real row count).
        /// </summary>
        class Std140Writer
        {
            readonly List<byte> _bytes = new();
            public int Position => _bytes.Count;

            public void WriteFloat(float v) => _bytes.AddRange(BitConverter.GetBytes(v));

            public void WriteVec4(float x, float y, float z, float w)
            {
                WriteFloat(x); WriteFloat(y); WriteFloat(z); WriteFloat(w);
            }

            /// <summary>Writes a mat3x4 (GLSL: 3 columns, 4 rows) as 3 vec4 columns = 48 bytes.</summary>
            public void WriteMat3x4Columns(float[] col0, float[] col1, float[] col2)
            {
                WriteVec4(col0[0], col0[1], col0[2], col0[3]);
                WriteVec4(col1[0], col1[1], col1[2], col1[3]);
                WriteVec4(col2[0], col2[1], col2[2], col2[3]);
            }

            /// <summary>Writes a mat4 (4 columns, 4 rows) as 4 vec4 columns = 64 bytes.</summary>
            public void WriteMat4Columns(float[] col0, float[] col1, float[] col2, float[] col3)
            {
                WriteVec4(col0[0], col0[1], col0[2], col0[3]);
                WriteVec4(col1[0], col1[1], col1[2], col1[3]);
                WriteVec4(col2[0], col2[1], col2[2], col2[3]);
                WriteVec4(col3[0], col3[1], col3[2], col3[3]);
            }

            public void Pad(int totalBytes)
            {
                while (_bytes.Count < totalBytes)
                    _bytes.Add(0);
            }

            public byte[] ToArray() => _bytes.ToArray();
        }

        // ---- GsysShape (binding 5) - 128 bytes -------------------------------------------------

        /// <summary>
        /// mat3x4 cTransform; vec4 cParams; vec4 cUnknown1; vec4 cUnknown2; vec4 cUnknown3;
        /// vec4 cTranslation;  = 48 + 16*5 = 128 bytes.
        ///
        /// cTransform is set to identity here, which sidesteps the row-vs-column-vector ambiguity
        /// entirely: a 3x3 identity linear part is its own transpose, so it's the same bytes
        /// under either convention. cParams.x is the shape's real VertexSkinCount from the bfres
        /// (0 = rigid/no skinning, matching what TestMaterialDump.DumpGeometry already printed
        /// for these models). cTranslation is (0,0,0,1) - render the shape at its own local
        /// origin, the sensible default for a standalone viewer with no scene placement.
        /// cUnknown1/2/3 use the values the shader's own comments record having observed
        /// (0, 0, and (1,0,0,0) respectively) rather than blind zero-fill.
        /// </summary>
        public static byte[] BuildGsysShape(Shape shape)
        {
            var w = new Std140Writer();

            w.WriteMat3x4Columns(
                new float[] { 1, 0, 0, 0 },
                new float[] { 0, 1, 0, 0 },
                new float[] { 0, 0, 1, 0 });

            w.WriteVec4(shape.VertexSkinCount, 0, 0, 0); // cParams.x = skin count
            w.WriteVec4(0, 0, 0, 0);                      // cUnknown1
            w.WriteVec4(0, 0, 0, 0);                      // cUnknown2
            w.WriteVec4(1, 0, 0, 0);                      // cUnknown3
            w.WriteVec4(0, 0, 0, 1);                      // cTranslation

            var result = w.ToArray();
            if (result.Length != 128)
                throw new InvalidOperationException($"GsysShape must be 128 bytes, got {result.Length}");
            return result;
        }

        // ---- GsysContext (binding 2) - 240 bytes -----------------------------------------------

        /// <summary>
        /// mat3x4 cView; mat4 cViewProj; mat4 cProj; mat3x4 cViewInv; vec4 cNearFar;
        /// = 48 + 64 + 64 + 48 + 16 = 240 bytes.
        ///
        /// PLACEHOLDER camera: not derived from anything the game produced. Auto-frames the given
        /// bounding sphere (Shape.SubMeshBoundings[0], the whole-shape bounds by convention) from
        /// a fixed angle at a distance that keeps it fully in view for the given vertical FOV.
        /// Uses the standard OpenGL/NVN right-handed convention (column-vectors, v' = M*v,
        /// classic [-1,1] NDC Z range) written directly in column-major order - this avoids
        /// needing to transpose out of System.Numerics.Matrix4x4's DirectX-flavored row-vector
        /// convention, but the row-vs-column convention Nintendo's own engine actually uses for
        /// cView specifically has NOT been confirmed against a live capture. Treat the exact
        /// numbers as "a" reasonable camera, not "the" in-game camera.
        /// </summary>
        public static byte[] BuildGsysContext(Vector3 boundsCenter, float boundsRadius,
            float fovYRadians = 0.9f, float aspect = 16f / 9f, float near = 0.1f, float? far = null)
        {
            if (boundsRadius <= 0) boundsRadius = 1f;
            float distance = boundsRadius / MathF.Sin(fovYRadians / 2f) * 1.2f; // 1.2x margin
            float farPlane = far ?? (distance + boundsRadius) * 4f;

            var eye = new Vector3(boundsCenter.X, boundsCenter.Y + boundsRadius * 0.3f, boundsCenter.Z + distance);
            var up = new Vector3(0, 1, 0);

            var (viewCols, viewInvCols) = LookAtAffineRH(eye, boundsCenter, up);
            var projCols = PerspectiveRH(fovYRadians, aspect, near, farPlane);
            var viewProjCols = MultiplyMat4(projCols, ToMat4(viewCols));

            var w = new Std140Writer();
            w.WriteMat3x4Columns(viewCols[0], viewCols[1], viewCols[2]);
            w.WriteMat4Columns(viewProjCols[0], viewProjCols[1], viewProjCols[2], viewProjCols[3]);
            w.WriteMat4Columns(projCols[0], projCols[1], projCols[2], projCols[3]);
            w.WriteMat3x4Columns(viewInvCols[0], viewInvCols[1], viewInvCols[2]);
            w.WriteVec4(near, farPlane, near / farPlane, farPlane / near); // znear,zfar,ratio,inverse ratio

            var result = w.ToArray();
            if (result.Length != 240)
                throw new InvalidOperationException($"GsysContext must be 240 bytes, got {result.Length}");
            return result;
        }

        // ---- GsysEnvironment (binding 6) - 352 bytes -------------------------------------------

        /// <summary>
        /// vec4 x10 (ambient/hemi/lights) + Fog[4] (each vec4+vec4+float*4=48 bytes)
        /// = 160 + 192 = 352 bytes.
        ///
        /// PLACEHOLDER lighting: a single dim-white key light from above-front, flat gray
        /// ambient/hemi terms, and all 4 fog slots disabled (Start=End so any depth-based
        /// interpolation collapses to 0, Damp=0). Not derived from any real TotK scene - a
        /// starting point for "does the lighting math run at all" before cross-referencing
        /// against a live capture's real GsysEnvironment values.
        /// </summary>
        public static byte[] BuildGsysEnvironment()
        {
            var w = new Std140Writer();

            w.WriteVec4(0.25f, 0.25f, 0.28f, 1f); // cAmbientColor
            w.WriteVec4(0.35f, 0.40f, 0.45f, 1f); // cHemiSkyColor
            w.WriteVec4(0.18f, 0.16f, 0.14f, 1f); // cHemiGroundColor
            w.WriteVec4(0f, 1f, 0f, 0f);          // cHemiDirection (straight up)

            var lightDir = Normalize(new Vector3(-0.4f, -0.7f, -0.5f));
            w.WriteVec4(lightDir.X, lightDir.Y, lightDir.Z, 0f); // cLightDirection0
            w.WriteVec4(1.0f, 0.97f, 0.9f, 1f);                   // cLightColor
            w.WriteVec4(1.0f, 1.0f, 1.0f, 1f);                    // cLightSpecColor

            w.WriteVec4(0f, 1f, 0f, 0f); // cLightDirection1 (unused - zero magnitude-ish, direction irrelevant since color is black)
            w.WriteVec4(0f, 0f, 0f, 1f); // cLightColor1 (off)
            w.WriteVec4(0f, 0f, 0f, 1f); // cLightSpecColor1 (off)

            for (int i = 0; i < 4; i++)
            {
                w.WriteVec4(0.6f, 0.65f, 0.7f, 0f); // Fog.Color
                w.WriteVec4(0f, 0f, -1f, 0f);        // Fog.Direction
                w.WriteFloat(100000f);               // Fog.Start (effectively disabled)
                w.WriteFloat(100000f);               // Fog.End
                w.WriteFloat(0f);                    // Fog.Damp
                w.WriteFloat(0f);                    // Fog.Padding
            }

            var result = w.ToArray();
            if (result.Length != 352)
                throw new InvalidOperationException($"GsysEnvironment must be 352 bytes, got {result.Length}");
            return result;
        }

        // ---- tiny vector/matrix helpers (column-major float[4][]) -----------------------------

        public struct Vector3
        {
            public float X, Y, Z;
            public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Vector3 operator -(Vector3 a) => new(-a.X, -a.Y, -a.Z);
        }

        static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        static Vector3 Cross(Vector3 a, Vector3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);
        static Vector3 Normalize(Vector3 v)
        {
            float len = MathF.Sqrt(Dot(v, v));
            return len > 1e-8f ? new Vector3(v.X / len, v.Y / len, v.Z / len) : v;
        }

        /// <summary>
        /// Standard OpenGL-style right-handed lookAt, returned as (view, viewInverse) each as 3
        /// vec4 columns (the affine mat3x4 shape - no projective row, since it's always (0,0,0,1)
        /// and the shader doesn't store it).
        /// </summary>
        static (float[][] view, float[][] viewInv) LookAtAffineRH(Vector3 eye, Vector3 target, Vector3 up)
        {
            var f = Normalize(target - eye);       // forward (camera looks down -Z in view space -> f maps to -Z)
            var s = Normalize(Cross(f, up));         // right
            var u = Cross(s, f);                     // recomputed up

            // GsysContext's cView is a mat3x4 - 3 columns, 4 rows - so translation must live in
            // the 4th COMPONENT of each column (the "4 rows" part), not a 4th column. Column i
            // is (rotRow_i, dot(rotRow_i, -eye)) - the standard packed affine-view layout.
            var view = new float[][]
            {
                new float[] { s.X, u.X, -f.X, -Dot(s, eye) },
                new float[] { s.Y, u.Y, -f.Y, -Dot(u, eye) },
                new float[] { s.Z, u.Z, -f.Z, Dot(f, eye) },
            };

            // Inverse view (camera's own world transform): columns are the camera's basis
            // vectors (s, u, -f) with the 4th component holding the eye position - this is the
            // orthonormal-rotation inverse (transpose of the linear part) combined with +eye
            // translation, the standard closed-form inverse of an orthonormal lookAt matrix.
            var viewInv = new float[][]
            {
                new float[] { s.X, s.Y, s.Z, eye.X },
                new float[] { u.X, u.Y, u.Z, eye.Y },
                new float[] { -f.X, -f.Y, -f.Z, eye.Z },
            };

            return (view, viewInv);
        }

        /// <summary>Standard OpenGL right-handed perspective projection, [-1,1] NDC Z range, as 4 vec4 columns.</summary>
        static float[][] PerspectiveRH(float fovYRadians, float aspect, float near, float far)
        {
            float f = 1f / MathF.Tan(fovYRadians / 2f);
            return new float[][]
            {
                new float[] { f / aspect, 0, 0, 0 },
                new float[] { 0, f, 0, 0 },
                new float[] { 0, 0, (far + near) / (near - far), -1 },
                new float[] { 0, 0, (2 * far * near) / (near - far), 0 },
            };
        }

        static float[][] ToMat4(float[][] mat3x4Cols)
        {
            // Extend a 3-column affine matrix to a full 4x4 by appending the implicit
            // (0,0,0,1) 4th column, for use in matrix multiplication below.
            return new float[][]
            {
                mat3x4Cols[0], mat3x4Cols[1], mat3x4Cols[2], new float[] { 0, 0, 0, 1 },
            };
        }

        /// <summary>Column-major 4x4 * 4x4 (a * b, applied as a(b(v))).</summary>
        static float[][] MultiplyMat4(float[][] a, float[][] b)
        {
            var result = new float[4][];
            for (int col = 0; col < 4; col++)
            {
                var bc = b[col];
                var rc = new float[4];
                for (int row = 0; row < 4; row++)
                {
                    rc[row] = a[0][row] * bc[0] + a[1][row] * bc[1] + a[2][row] * bc[2] + a[3][row] * bc[3];
                }
                result[col] = rc;
            }
            return result;
        }
    }
}
