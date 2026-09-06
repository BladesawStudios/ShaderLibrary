// Extracted from Shader/ApplicationPackage.Nin_NX_NVN.release.sarc.zs -> AglLightShader.sharcb
// program "lightprepass_spot", variation SPECULAR_TYPE=1, SHADOW_TYPE=1. Sibling of
// PixelLightPrePassPoint.frag for cone-shaped spot lights (e.g. the CannonSpotlight-style
// objects) instead of point lights; also samples a per-light shadow map (cSamplerDepthShadow)
// to attenuate the contribution before it reaches the shared deferred light pre-pass targets.
//
// Confidence: HIGH for out_attr0 (diffuse, cTex_DeferredLightPrePass array layer 0) -- same MRT
// layout as the point-light shader and the same relationship to PixelDeferred.frag's reads.
// out_attr1 (would-be specular, layer 1) is written as a hardcoded 0 in this particular decompiled
// variation despite SPECULAR_TYPE=1 being requested; not fully understood (possibly SPECULAR_TYPE
// gates a different code path than expected, or a specular term that folds to a constant for this
// combination) -- flagged here rather than silently "fixed".
// out_attr2.z: same MEDIUM-HIGH confidence PreMisc-contribution hypothesis as in the point-light
// shader (see PixelLightPrePassPoint.frag's header comment) -- here it is the shadow-modulated
// cone falloff instead of the point light's distance falloff.
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

layout (binding = 0, std140) uniform _GameContext
{
    precise vec4 data[4096];
} fp_c3;

layout (binding = 1, std140) uniform _LppContext
{
    precise vec4 data[4096];
} fp_c4;

layout (binding = 2, std140) uniform _SpotLight
{
    precise vec4 data[4096];
} fp_c5;

layout (binding = 0) uniform sampler2D cSamplerDepth;
layout (binding = 1) uniform sampler2D cSamplerNormal;
layout (binding = 2) uniform sampler2DShadow cSamplerDepthShadow;
layout (location = 0) noperspective in vec4 in_attr0;
layout (location = 1) noperspective in vec4 in_attr1;

layout (location = 0) out vec4 out_attr0;
layout (location = 1) out vec4 out_attr1;
layout (location = 2) out vec4 out_attr2;


void main()
{
    precise float temp_0;
    precise float temp_1;
    precise float temp_2;
    precise float temp_3;
    precise vec3 temp_4;
    precise float temp_5;
    precise float temp_6;
    precise float temp_7;
    precise float temp_8;
    precise float temp_9;
    precise float temp_10;
    precise float temp_11;
    precise float temp_12;
    precise float temp_13;
    int temp_14;
    precise float temp_15;
    precise float temp_16;
    bool temp_17;
    bool temp_18;
    precise float temp_19;
    precise float temp_20;
    precise float temp_21;
    precise float temp_22;
    precise float temp_23;
    precise float temp_24;
    precise float temp_25;
    precise float temp_26;
    precise float temp_27;
    temp_0 = (gl_FragCoord.x / support_buffer.render_scale[0] + -0.00999999978) * fp_c3.data[15].z;
    temp_1 = (gl_FragCoord.y / support_buffer.render_scale[0] + -0.00999999978) * fp_c3.data[15].w;
    temp_2 = fma(texture(cSamplerDepth, vec2(temp_0, temp_1)).x, fp_c4.data[10].y, fp_c4.data[10].x);
    temp_3 = 1.0 / fma(temp_2, 0.0 - in_attr1.w, fp_c5.data[7].w);
    temp_4 = texture(cSamplerNormal, vec2(temp_0, temp_1)).xyz;
    temp_5 = temp_4.x;
    temp_6 = temp_4.y;
    temp_7 = fma(temp_2, 0.0 - in_attr0.x, 0.0 - fp_c5.data[10].x);
    temp_8 = fma(temp_2, 0.0 - in_attr0.y, 0.0 - fp_c5.data[10].y);
    temp_9 = fma(temp_2, 0.0 - in_attr0.z, 0.0 - fp_c5.data[10].z);
    temp_10 = fma(temp_9, temp_9, fma(temp_8, temp_8, temp_7 * temp_7));
    temp_11 = temp_7 * inversesqrt(temp_10);
    temp_12 = temp_8 * inversesqrt(temp_10);
    temp_13 = temp_9 * inversesqrt(temp_10);
    temp_14 = int(trunc(temp_4.z * 255.0));
    temp_15 = fma(temp_6, temp_6, temp_5 * temp_5);
    temp_16 = 0.0 - temp_15 + 1.0;
    temp_17 = (temp_14 & 1) != 0;
    temp_18 = (temp_14 & 2) != 0;
    temp_19 = fma(temp_12, fp_c5.data[11].y, temp_11 * fp_c5.data[11].x);
    temp_20 = sqrt(max(0.0, temp_16));
    temp_21 = temp_16;
    temp_22 = temp_19;
    if (temp_18)
    {
        temp_21 = 2.0;
    }
    temp_23 = temp_21;
    if (!temp_18)
    {
        temp_23 = -2.0;
    }
    if (temp_17)
    {
        temp_22 = 2.0;
    }
    temp_24 = temp_22;
    if (!temp_17)
    {
        temp_24 = -2.0;
    }
    temp_25 = exp2(log2(0.0 - clamp(sqrt(temp_10) * fp_c5.data[8].w, 0.0, 1.0) + 1.0) * fp_c5.data[10].w);
    temp_26 = exp2(log2(clamp(fma(fma(temp_13, fp_c5.data[11].z, temp_19), fp_c5.data[13].y, 0.0 - fp_c5.data[13].z), 0.0, 1.0)) * fp_c5.data[11].w);
    temp_27 = clamp(texture(cSamplerDepthShadow, vec3(fma(temp_2, 0.0 - in_attr1.x, fp_c5.data[4].w) * temp_3, fma(temp_2, 0.0 - in_attr1.y, fp_c5.data[5].w) * temp_3, fma(temp_2, 0.0 - in_attr1.z, fp_c5.data[6].w) * temp_3)) * temp_25 * clamp(0.0 - fma(fma(temp_15, -2.0, 1.0), temp_13, fma(temp_6 * temp_20 * temp_24, temp_12, temp_5 * temp_20 * temp_23 * temp_11)) + -0.0, 0.0, 1.0) * temp_26, 0.0, 1.0);
    out_attr0.x = temp_27 * fp_c5.data[8].x;
    out_attr0.y = temp_27 * fp_c5.data[8].y;
    out_attr0.z = temp_27 * fp_c5.data[8].z;
    out_attr0.w = 0.0;
    out_attr1.x = 0.0;
    out_attr1.y = 0.0;
    out_attr1.z = 0.0;
    out_attr1.w = 0.0;
    out_attr2.x = 0.0;
    out_attr2.y = 0.0;
    out_attr2.z = clamp(clamp(temp_25 * temp_26, 0.0, 1.0) * clamp(temp_25 * temp_26, 0.0, 1.0), 0.0, 1.0);
    out_attr2.w = 0.0;
    return;
}
