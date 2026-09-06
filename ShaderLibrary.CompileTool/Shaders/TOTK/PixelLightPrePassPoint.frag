// Extracted from Shader/ApplicationPackage.Nin_NX_NVN.release.sarc.zs -> AglLightShader.sharcb
// program "lightprepass_point", variation IS_ENABLE_SPECULAR=1, IS_POINT_LIGHT=1 (all other
// macros 0). This is a light-VOLUME shader: drawn once per dynamic point light (torches,
// campfires, bomb flashes, etc.) as an additively-blended sphere, using the GBuffer normal/depth
// to reconstruct the shaded point and accumulating into the deferred light pre-pass targets that
// PixelDeferred.frag later reads back as cTex_DeferredLightPrePass (a sampler2DArray: layer 0 =
// diffuse, layer 1 = specular).
//
// Confidence: HIGH for out_attr0 (diffuse, array layer 0) / out_attr1 (specular, array layer 1) --
// matches PixelDeferred.frag's texture(cTex_DeferredLightPrePass, vec3(uv, 0.0)/vec3(uv, 1.0))
// reads exactly, and the diffuse/specular BRDF math (Lambert / (1/pi), a GGX-shaped specular term)
// is self-consistent with a light-volume accumulation pass.
// out_attr2 (location 2, only .z populated) is MEDIUM-HIGH confidence: system_shading program 36
// (o_deferred_shading_type=1, a fog-adjacent composite variant) reads cTex_PreMisc specifically via
// its .z channel, and out_attr2.z here is a similarly-shaped clamped local-light falloff value
// scaled by a "reveal" style light parameter (fp_c4.data[4].w) -- consistent with cTex_PreMisc
// picking up a contribution from each light volume alongside the light pre-pass, but this was not
// independently verified against a full PreMisc generator, so treat out_attr2 as a lead, not
// confirmed ground truth.
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

layout (binding = 1, std140) uniform _GamePointLight
{
    precise vec4 data[4096];
} fp_c4;


layout (binding = 0) uniform sampler2D cSamplerDepth;
layout (binding = 1) uniform sampler2D cSamplerNormal;
layout (location = 0) noperspective in vec4 in_attr0;

layout (location = 0) out vec4 out_attr0;
layout (location = 1) out vec4 out_attr1;
layout (location = 2) out vec4 out_attr2;


void main()
{
    precise float temp_0;
    precise float temp_1;
    precise vec4 temp_2;
    precise float temp_3;
    precise float temp_4;
    precise float temp_5;
    precise float temp_6;
    precise float temp_7;
    precise float temp_8;
    precise float temp_9;
    int temp_10;
    precise float temp_11;
    precise float temp_12;
    precise float temp_13;
    precise float temp_14;
    bool temp_15;
    bool temp_16;
    precise float temp_17;
    int temp_18;
    precise float temp_19;
    precise float temp_20;
    precise float temp_21;
    precise float temp_22;
    precise float temp_23;
    precise float temp_24;
    precise float temp_25;
    precise float temp_26;
    precise float temp_27;
    precise float temp_28;
    precise float temp_29;
    precise float temp_30;
    precise float temp_31;
    precise float temp_32;
    precise float temp_33;
    precise float temp_34;
    precise float temp_35;
    precise float temp_36;
    precise float temp_37;
    precise float temp_38;
    precise float temp_39;
    precise float temp_40;
    precise float temp_41;
    precise float temp_42;
    precise float temp_43;
    precise float temp_44;
    precise float temp_45;
    precise float temp_46;
    precise float temp_47;
    precise float temp_48;
    precise float temp_49;
    precise float temp_50;
    precise float temp_51;
    precise float temp_52;
    precise float temp_53;
    precise float temp_54;
    precise float temp_55;
    precise float temp_56;
    precise float temp_57;
    precise float temp_58;
    precise float temp_59;
    precise float temp_60;
    temp_0 = (gl_FragCoord.x / support_buffer.render_scale[0] + -0.00999999978) * fp_c3.data[15].z;
    temp_1 = (gl_FragCoord.y / support_buffer.render_scale[0] + -0.00999999978) * fp_c3.data[15].w;
    temp_2 = texture(cSamplerNormal, vec2(temp_0, temp_1)).xyzw;
    temp_3 = temp_2.x;
    temp_4 = temp_2.y;
    temp_5 = temp_2.w;
    temp_6 = in_attr0.x;
    temp_7 = in_attr0.y;
    temp_8 = in_attr0.z;
    temp_9 = fma(texture(cSamplerDepth, vec2(temp_0, temp_1)).x, fp_c3.data[12].y, fp_c3.data[12].x);
    temp_10 = int(trunc(temp_2.z * 255.0));
    temp_11 = fma(temp_9, 0.0 - temp_6, 0.0 - fp_c4.data[5].x);
    temp_12 = fma(temp_4, temp_4, temp_3 * temp_3);
    temp_13 = fma(temp_9, 0.0 - temp_7, 0.0 - fp_c4.data[5].y);
    temp_14 = fma(temp_9, 0.0 - temp_8, 0.0 - fp_c4.data[5].z);
    temp_15 = (temp_10 & 1) != 0;
    temp_16 = (temp_10 & 2) != 0;
    temp_17 = sqrt(max(0.0, 0.0 - temp_12 + 1.0));
    temp_18 = int(trunc(temp_5 * 255.0));
    temp_19 = max(fma(temp_14, temp_14, fma(temp_13, temp_13, temp_11 * temp_11)), 0.001);
    temp_20 = intBitsToFloat(undef);
    if (temp_16)
    {
        temp_20 = 2.0;
    }
    temp_21 = intBitsToFloat(undef);
    temp_22 = temp_20;
    if (temp_15)
    {
        temp_21 = 2.0;
    }
    temp_23 = temp_21;
    if (!temp_16)
    {
        temp_22 = -2.0;
    }
    if (!temp_15)
    {
        temp_23 = -2.0;
    }
    temp_24 = clamp(temp_19 * fp_c4.data[0].y, 0.0, 1.0);
    temp_25 = temp_3 * temp_17 * temp_22;
    temp_26 = temp_11 * 0.0 - inversesqrt(temp_19);
    temp_27 = temp_4 * temp_17 * temp_23;
    temp_28 = temp_13 * 0.0 - inversesqrt(temp_19);
    temp_29 = temp_14 * 0.0 - inversesqrt(temp_19);
    temp_30 = fma(temp_12, -2.0, 1.0);
    temp_31 = fma(temp_24, 0.0 - temp_24, 1) * fma(temp_24, 0.0 - temp_24, 1);
    temp_32 = clamp(fma(temp_30, temp_29, fma(temp_28, temp_27, temp_25 * temp_26)), 0.0, 1.0);
    if ((temp_18 & 2) != 0 && !((temp_18 & 1) != 0))
    {
        temp_33 = temp_32 * temp_32 * temp_32;
        if (!(temp_32 > 0.0))
        {
            temp_33 = 0.0;
        }
        temp_34 = temp_33;
    }
    else
    {
        temp_34 = temp_32;
    }
    temp_35 = 1.0 / temp_19;
    temp_36 = 0.0 - temp_5 + 1.0;
    temp_37 = temp_35 * temp_34;
    temp_38 = inversesqrt(fma(temp_6, temp_6, temp_7 * temp_7) + 1.0);
    temp_39 = temp_6 * temp_38;
    temp_40 = temp_7 * temp_38;
    temp_41 = temp_26 + temp_39;
    temp_42 = temp_8 * temp_38;
    temp_43 = temp_28 + temp_40;
    temp_44 = temp_29 + temp_42;
    temp_45 = inversesqrt(fma(temp_44, temp_44, fma(temp_43, temp_43, temp_41 * temp_41)));
    temp_46 = temp_41 * temp_45;
    temp_47 = temp_43 * temp_45;
    temp_48 = temp_44 * temp_45;
    temp_49 = clamp(fma(temp_36, temp_36, 0.05), 0.0, 1.0) * clamp(fma(temp_36, temp_36, 0.05), 0.0, 1.0);
    temp_50 = clamp(fma(temp_30, temp_48, fma(temp_27, temp_47, temp_25 * temp_46)), 0.0, 1.0) * clamp(fma(temp_30, temp_48, fma(temp_27, temp_47, temp_25 * temp_46)), 0.0, 1.0);
    temp_51 = 0.0 - clamp(fma(temp_42, temp_48, fma(temp_40, temp_47, temp_39 * temp_46)), 0.0, 1.0) + 1.0;
    temp_52 = temp_51 * temp_51;
    temp_53 = fma(temp_36, 0.5, 0.5) * fma(temp_36, 0.5, 0.5);
    temp_54 = temp_53 * 0.5;
    temp_55 = fma(temp_53, -0.5, 1.0);
    temp_56 = clamp(fma(fma(temp_6 + 0.0 - fp_c4.data[6].x, temp_6 + 0.0 - fp_c4.data[6].x, (temp_7 + 0.0 - fp_c4.data[6].y) * (temp_7 + 0.0 - fp_c4.data[6].y)), 0.0 - fp_c4.data[6].z, 1.0), 0.0, 1.0);
    temp_57 = clamp(0.0 - temp_29 + -0.0, 0.0, 1.0);
    temp_58 = temp_31 + fma(temp_31, 0.0 - temp_57, temp_57);
    temp_59 = 1.0 / fma(temp_55, temp_32, temp_54) * (1.0 / fma(clamp(fma(temp_30, temp_42, fma(temp_27, temp_40, temp_25 * temp_39)), 0.0, 1.0), temp_55, temp_54)) * fma(temp_52 * temp_51 * temp_52, 0.96, 0.0399999991) * temp_49 * (1.0 / ((fma(temp_49, temp_50, 0.0 - temp_50) + 1.0) * (fma(temp_49, temp_50, 0.0 - temp_50) + 1.0))) * temp_32 * temp_35;
    temp_60 = fma(-1.0 + fp_c4.data[7].z, 0.0 - clamp((clamp(fma(float(temp_10 & 252) * 0.0039682542, float(temp_10 & 252) * 0.0039682542, float(temp_18 & 2) * 0.5), 0.0, 1.0) + 0.0 - fp_c4.data[7].y) * (1.0 / (0.0 - fp_c4.data[7].y + fp_c4.data[7].x)), 0.0, 1.0), -1.0 + fp_c4.data[7].z);
    out_attr0.x = min(fma(temp_60, temp_31 * temp_37 * fp_c4.data[3].x * 0.318309873, temp_31 * temp_37 * fp_c4.data[3].x * 0.318309873), 32.0);
    out_attr0.y = min(fma(temp_60, temp_31 * temp_37 * fp_c4.data[3].y * 0.318309873, temp_31 * temp_37 * fp_c4.data[3].y * 0.318309873), 32.0);
    out_attr0.z = min(fma(temp_60, temp_31 * temp_37 * fp_c4.data[3].z * 0.318309873, temp_31 * temp_37 * fp_c4.data[3].z * 0.318309873), 32.0);
    out_attr0.w = 0.0;
    out_attr1.x = min(fma(temp_60, temp_59 * fp_c4.data[3].x * temp_56 * temp_58 * 0.0795774683, temp_59 * fp_c4.data[3].x * temp_56 * temp_58 * 0.0795774683), 32.0);
    out_attr1.y = min(fma(temp_60, temp_59 * fp_c4.data[3].y * temp_56 * temp_58 * 0.0795774683, temp_59 * fp_c4.data[3].y * temp_56 * temp_58 * 0.0795774683), 32.0);
    out_attr1.z = min(fma(temp_60, temp_59 * fp_c4.data[3].z * temp_56 * temp_58 * 0.0795774683, temp_59 * fp_c4.data[3].z * temp_56 * temp_58 * 0.0795774683), 32.0);
    out_attr1.w = 0.0;
    out_attr2.x = 0.0;
    out_attr2.y = 0.0;
    out_attr2.z = clamp(clamp(0.0 - temp_24 + 1.0, 0.0, 1.0) * clamp(0.0 - temp_24 + 1.0, 0.0, 1.0) * fp_c4.data[4].w, 0.0, 1.0);
    out_attr2.w = 0.0;
    return;
}
