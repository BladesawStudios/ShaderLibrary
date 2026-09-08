#version 450 core
#extension GL_ARB_gpu_shader_int64 : enable
#extension GL_ARB_shader_ballot : enable
#extension GL_ARB_shader_group_vote : enable
#extension GL_EXT_shader_image_load_formatted : enable
#extension GL_EXT_texture_shadow_lod : enable
#extension GL_ARB_fragment_shader_interlock : enable
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

layout (binding = 2, std140) uniform _fp_c1
{
    precise vec4 data[4096];
} fp_c1;

layout (binding = 4, std140) uniform _fp_c3
{
    precise vec4 data[4096];
} fp_c3;

layout (binding = 0) uniform sampler2D fp_t_tcb_8;
layout (binding = 1) uniform sampler2D fp_t_tcb_A;
layout (location = 0) in vec4 in_attr0;

layout (location = 0) out vec4 out_attr0;


void main()
{
    precise float temp_0;
    precise float temp_1;
    precise vec3 temp_2;
    precise vec3 temp_3;
    precise float temp_4;
    precise float temp_5;
    precise float temp_6;
    precise float temp_7;
    precise float temp_8;
    precise float temp_9;
    precise float temp_10;
    precise float temp_11;
    precise float temp_12;
    precise float temp_13;
    uint temp_14;
    precise float temp_15;
    temp_0 = in_attr0.x;
    temp_1 = in_attr0.y;
    temp_2 = texture(fp_t_tcb_8, vec2(temp_0, temp_1)).xyz;
    temp_3 = texture(fp_t_tcb_A, vec2(temp_0, temp_1)).xyz;
    temp_4 = min(temp_2.x, 10000.0) + temp_3.x;
    temp_5 = min(temp_2.y, 10000.0) + temp_3.y;
    temp_6 = min(temp_2.z, 10000.0) + temp_3.z;
    temp_7 = fma(temp_6, 0.1144, fma(temp_5, 0.5866, temp_4 * 0.298900008));
    temp_8 = 0.0 - exp2(temp_7 * -1.44269502) + 1.0;
    temp_9 = temp_8 * (1.0 / temp_7);
    temp_10 = temp_8 * temp_8;
    temp_11 = clamp(fma(fma(temp_4, 0.0 - temp_9, 0.0 - exp2(temp_4 * -1.44269502) + 1.0), temp_10, temp_4 * temp_9), 0.0, 1.0);
    temp_12 = clamp(fma(fma(temp_5, 0.0 - temp_9, 0.0 - exp2(temp_5 * -1.44269502) + 1.0), temp_10, temp_5 * temp_9), 0.0, 1.0);
    temp_13 = clamp(fma(fma(temp_6, 0.0 - temp_9, 0.0 - exp2(temp_6 * -1.44269502) + 1.0), temp_10, temp_6 * temp_9), 0.0, 1.0);
    temp_14 = max(max(floatBitsToUint(temp_12), floatBitsToUint(temp_13)), floatBitsToUint(temp_11));
    temp_15 = fma((fma(temp_13, 0.6666667, fma(temp_12, 0.6666667, temp_11 * 0.666666687)) + -1.0) * (fma(temp_13, 0.6666667, fma(temp_12, 0.6666667, temp_11 * 0.666666687)) + -1.0), 0.0 - fp_c3.data[0].y, fp_c3.data[0].y) + fp_c3.data[0].x;
    out_attr0.x = fma(temp_11 + 0.0 - uintBitsToFloat(temp_14), temp_15, uintBitsToFloat(temp_14));
    out_attr0.y = fma(temp_12 + 0.0 - uintBitsToFloat(temp_14), temp_15, uintBitsToFloat(temp_14));
    out_attr0.z = fma(temp_13 + 0.0 - uintBitsToFloat(temp_14), temp_15, uintBitsToFloat(temp_14));
    out_attr0.w = 0.0;
    return;
}

