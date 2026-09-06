// Companion vertex shader to PixelLightPrePassSpot.frag. Extracted from
// Shader/ApplicationPackage.Nin_NX_NVN.release.sarc.zs -> AglLightShader.sharcb, program
// "lightprepass_spot", same variation (SPECULAR_TYPE=1, SHADOW_TYPE=1). Transforms a cone-shaped
// light-volume mesh (widened at the apex via the temp_3 conditional trick below) into clip space
// from SpotLight uniform data, and outputs both the screen-space UV and a light-space position
// (out_attr1) the pixel shader uses for the per-light shadow sample.
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

layout (binding = 1, std140) uniform _SpotLight
{
    precise vec4 data[4096];
} vp_c4;


layout (binding = 0, std140) uniform _LppContext
{
    precise vec4 data[4096];
} vp_c3;

layout (location = 0) in vec4 aPosition;

layout (location = 0) out vec4 aPosition;
layout (location = 1) out vec4 out_attr1;


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
    precise float temp_8;
    precise float temp_9;
    precise float temp_10;
    gl_Position.x = 0.0;
    gl_Position.y = 0.0;
    gl_Position.z = 0.0;
    gl_Position.w = 1.0;
    temp_0 = aPosition.z;
    temp_1 = aPosition.x;
    temp_2 = aPosition.y;
    temp_3 = fma(fma(0.0 - abs(0.0 - temp_0 < 0.0 ? 1.0 : 0.0 + (temp_0 > 0.0 ? 1.0 : 0.0)) + 1.0, 0.0 - abs(0.0 - temp_1 < 0.0 ? 1.0 : 0.0 + (temp_1 > 0.0 ? 1.0 : 0.0)) + -0.0, 0.0 - abs(0.0 - temp_0 < 0.0 ? 1.0 : 0.0 + (temp_0 > 0.0 ? 1.0 : 0.0)) + 1.0), (temp_2 + -0.5) * vp_c4.data[13].x, temp_2);
    temp_4 = fma(temp_0, vp_c4.data[3].z, fma(temp_3, vp_c4.data[3].y, temp_1 * vp_c4.data[3].x)) + vp_c4.data[3].w;
    temp_5 = fma(temp_0, vp_c4.data[0].z, fma(temp_3, vp_c4.data[0].y, temp_1 * vp_c4.data[0].x)) + vp_c4.data[0].w;
    gl_Position.w = temp_4;
    temp_6 = fma(temp_0, vp_c4.data[1].z, fma(temp_3, vp_c4.data[1].y, temp_1 * vp_c4.data[1].x)) + vp_c4.data[1].w;
    gl_Position.x = temp_5;
    gl_Position.y = temp_6;
    gl_Position.z = fma(temp_0, vp_c4.data[2].z, fma(temp_3, vp_c4.data[2].y, temp_1 * vp_c4.data[2].x)) + vp_c4.data[2].w;
    temp_7 = temp_4;
    if (abs(temp_4) < 0.001)
    {
        temp_7 = 0.00100000005;
    }
    temp_8 = 1.0 / temp_7;
    aPosition.z = 1.0;
    temp_9 = fma(temp_5 * temp_8, 0.0 - vp_c3.data[11].x, 0.0 - vp_c3.data[11].z);
    temp_10 = fma(temp_6 * temp_8, vp_c3.data[11].y, 0.0 - vp_c3.data[11].w);
    aPosition.x = temp_9;
    aPosition.y = temp_10;
    out_attr1.w = fma(temp_10, vp_c4.data[7].y, temp_9 * vp_c4.data[7].x) + vp_c4.data[7].z;
    out_attr1.z = fma(temp_10, vp_c4.data[6].y, temp_9 * vp_c4.data[6].x) + vp_c4.data[6].z;
    out_attr1.y = fma(temp_10, vp_c4.data[5].y, temp_9 * vp_c4.data[5].x) + vp_c4.data[5].z;
    out_attr1.x = fma(temp_10, vp_c4.data[4].y, temp_9 * vp_c4.data[4].x) + vp_c4.data[4].z;
    return;
}
