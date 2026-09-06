// Companion vertex shader to PixelLightPrePassPoint.frag. Extracted from
// Shader/ApplicationPackage.Nin_NX_NVN.release.sarc.zs -> AglLightShader.sharcb, program
// "lightprepass_point", same variation (IS_ENABLE_SPECULAR=1, IS_POINT_LIGHT=1). Transforms a
// unit light-volume sphere mesh (scaled/positioned from GamePointLight uniform data) into clip
// space and outputs the screen-space UV (aPosition.xy, reused as both the vertex input and the
// interpolated output here per Ryujinx's naming) the pixel shader uses to sample the GBuffer.
// Confidence: HIGH (paired 1:1 with the pixel shader above via the same program/variation).
#version 450 core
#extension GL_ARB_gpu_shader_int64 : enable
#extension GL_ARB_shader_ballot : enable
#extension GL_ARB_shader_group_vote : enable
#extension GL_EXT_shader_image_load_formatted : enable
#extension GL_EXT_texture_shadow_lod : enable
#extension GL_ARB_shader_draw_parameters : enable
#extension GL_ARB_shader_viewport_layer_array : enable
#extension GL_NV_viewport_array2 : enable
#pragma optionNV(fastmath off)

const int undef = 0;

layout (binding = 0, std140) uniform _support_buffer
{
    uint alpha_test;
    uint is_bgra[8];
    precise vec4 viewport_inverse;
    precise vec4 viewport_size;
    int frag_scale_count;
    precise float render_scale[73];
    ivec4 tfe_offset;
    int tfe_vertex_count;
} support_buffer;

layout (binding = 1, std140) uniform _GamePointLight
{
    precise vec4 data[4096];
} vp_c4;

layout (binding = 0, std140) uniform _GameContext
{
    precise vec4 data[4096];
} vp_c3;


layout (location = 0) in vec4 aPosition;

layout (location = 0) out vec4 aPosition;


void main()
{
    precise float temp_0;
    precise float temp_1;
    precise float temp_2;
    precise float temp_3;
    precise float temp_4;
    precise float temp_5;
    precise float temp_6;
    precise float temp_7;
    gl_Position.x = 0.0;
    gl_Position.y = 0.0;
    gl_Position.z = 0.0;
    gl_Position.w = 1.0;
    aPosition.z = 1.0;
    temp_0 = fma(aPosition.x * vp_c4.data[0].x, 2.0, vp_c4.data[4].x);
    temp_1 = fma(aPosition.y * vp_c4.data[0].x, 2.0, vp_c4.data[4].y);
    temp_2 = fma(aPosition.z * vp_c4.data[0].x, 2.0, vp_c4.data[4].z);
    temp_3 = fma(temp_2, vp_c3.data[3].z, fma(temp_1, vp_c3.data[3].y, temp_0 * vp_c3.data[3].x)) + vp_c3.data[3].w;
    temp_4 = fma(temp_2, vp_c3.data[0].z, fma(temp_1, vp_c3.data[0].y, temp_0 * vp_c3.data[0].x)) + vp_c3.data[0].w;
    gl_Position.w = temp_3;
    temp_5 = fma(temp_2, vp_c3.data[1].z, fma(temp_1, vp_c3.data[1].y, temp_0 * vp_c3.data[1].x)) + vp_c3.data[1].w;
    gl_Position.x = temp_4;
    gl_Position.y = temp_5;
    gl_Position.z = fma(temp_2, vp_c3.data[2].z, fma(temp_1, vp_c3.data[2].y, temp_0 * vp_c3.data[2].x)) + vp_c3.data[2].w;
    temp_6 = temp_3;
    if (abs(temp_3) < 0.001)
    {
        temp_6 = 0.00100000005;
    }
    temp_7 = 1.0 / temp_6;
    aPosition.x = fma(temp_4 * temp_7, 0.0 - vp_c3.data[13].x, 0.0 - vp_c3.data[13].z);
    aPosition.y = fma(temp_5 * temp_7, vp_c3.data[13].y, 0.0 - vp_c3.data[13].w);
    return;
}
