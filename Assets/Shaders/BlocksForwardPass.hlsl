// The forward pass of the blocks: URP's LitForwardPass.hlsl (render-pipelines.universal 17,
// Unity 6000.6) with these changes: the pixels the block atlas marks as tinted (alpha below 1:
// grass) take the colour of the climate where they stand, an ore's overlay is laid over its rock,
// and the vertices' sky light darkens what is out of the sky's reach. To update for another
// URP version, copy LitForwardPass.hlsl again and reapply the parts marked "ECO".
#ifndef ECO_BLOCKS_FORWARD_PASS_INCLUDED
#define ECO_BLOCKS_FORWARD_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// ECO: the climate's grass colour, one pixel per cell of the world's geography, set by
// ClimateTint. xy: scale from world x/z to texture coordinates, zw: offset.
TEXTURE2D(_ClimateTint);
SAMPLER(sampler_ClimateTint);
float4 _ClimateTintTransform;

half3 ClimateTintAt(float3 positionWS)
{
    float2 uv = positionWS.xz * _ClimateTintTransform.xy + _ClimateTintTransform.zw;
    return SAMPLE_TEXTURE2D(_ClimateTint, sampler_ClimateTint, uv).rgb;
}

// ECO: the sky light, 0 to 1 in the red channel of the vertices' colour (ChunkMesher), darkens
// everything the block receives, sun and ambient alike: shaded under a roof, dark underground.
// The curve gives open sky its full brightness and falls quickly in the last levels.
// _DarkestSkyLight is the brightness kept where no sky light reaches (set by VoxelWorld).
float _DarkestSkyLight;

half SkyBrightness(half skyLight)
{
    half curve = skyLight / (3.0h * (1.0h - skyLight) + 1.0h);
    return lerp((half)_DarkestSkyLight, 1.0h, curve);
}

// ECO: the light of torches (BlockLight), 0 to 1 in the green channel of the vertices' colour,
// adds a warm glow to the surface's own colour, bright by the torch and falling by about a fifth
// per block (VoxelWorld.TorchGlow is the same curve).
half TorchGlow(half torchLight)
{
    if (torchLight <= 0.0h) return 0.0h;
    return (pow(0.8h, (1.0h - torchLight) * 14.0h) - 0.044h) / 0.956h;
}

static const half3 TorchColour = half3(0.95h, 0.6h, 0.3h);

// ECO: the ores' overlays (BlockAtlas.CreateOverlayTexture, set by VoxelWorld), a row of tiles with
// real transparency laid over the rock an ore lies in. An ore's faces carry their place in it on the
// fourth texture coordinates and a full blue in their vertex colour (ChunkMesher); only that blue is
// trusted, since a mesh without those coordinates may hand the shader anything for them (the
// atlas's own coordinates were seen there, which laid overlays on grass).
TEXTURE2D(_OreOverlay);
SAMPLER(sampler_OreOverlay);

#if defined(_ECO_SMOOTH)
// ECO: the smoothed ground (SmoothTerrain, ChunkMesher.SmoothSubmesh, the terrain material). Its faces
// have no texture coordinates: each pixel is textured from the side its normal faces most (x, y or z,
// one tile per block as the blocks), the choice dithered texel by texel where two sides are close, so
// that it stays pixel art instead of blurring two pictures together. Each vertex carries the
// materials (block types) of its quad's four corners and which corner it is: each pixel shows one of
// them, picked by their shares there, dithered too. A material's tile depends on how much its ground
// faces up (BlockAtlas.SetSmoothTables), its variant on the block cell the pixel lies in, as
// TileVariants weighs them. The blue of the vertex colour is the ore of the quad's own block, its
// alpha that block's material, which shows over the face but near the corners.
TEXTURE2D(_SmoothMaterials);
TEXTURE2D(_SmoothVariants);
float4 _SmoothAtlas; // x: the atlas's slots, y: the overlays' slots, z: the row of the first overlay

static const float SmoothUvInset = 0.01; // ChunkMesher.UvInset

uint EcoHash(int3 cell, uint salt)
{
    uint h = (uint)cell.x * 0x8DA6B343u ^ (uint)cell.y * 0xD8163841u ^ (uint)cell.z * 0xCB1AB31Fu ^ (salt + 1u) * 0x165667B1u;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 12;
    h *= 0x297A2D39u;
    h ^= h >> 15;
    return h;
}

float EcoRandom(int3 cell, uint salt)
{
    return (EcoHash(cell, salt) & 0xFFFFFFu) / 16777216.0;
}

// The slot of one of a tile's variants (a row of _SmoothVariants) picked by the hash, and whether and
// how it turns (only when mayTurn and the tile turns).
float EcoPickSlot(int row, uint hash, bool mayTurn, out uint turn, out bool mirror)
{
    float4 head = LOAD_TEXTURE2D(_SmoothVariants, int2(0, row));
    turn = 0u;
    mirror = false;
    if (mayTurn && head.z > 0.5)
    {
        turn = (hash >> 24) & 3u;
        mirror = ((hash >> 26) & 1u) != 0u;
    }

    uint count = (uint)round(head.x);
    uint total = (uint)max(round(head.y), 1.0);
    float roll = (float)((hash & 0xFFFFFFu) % total);
    for (uint v = 0u; v < count; v++)
    {
        float4 entry = LOAD_TEXTURE2D(_SmoothVariants, int2(1 + v, row));
        if (roll < entry.y) return entry.x;
    }

    return LOAD_TEXTURE2D(_SmoothVariants, int2(1, row)).x;
}

float2 EcoTurn(float2 f, uint turn, bool mirror)
{
    if ((turn & 1u) != 0u) f = float2(f.y, 1.0 - f.x);
    if ((turn & 2u) != 0u) f = 1.0 - f;
    if (mirror) f.x = 1.0 - f.x;
    return f;
}

// Within a slot of a row of tiles, as ChunkMesher places a tile: inset a little from its edges.
float2 EcoSlotUv(float slot, float slots, float2 f)
{
    return float2((slot + lerp(SmoothUvInset, 1.0 - SmoothUvInset, f.x)) / slots, lerp(SmoothUvInset, 1.0 - SmoothUvInset, f.y));
}
#endif

#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

#if defined(_PARALLAXMAP)
#define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
#endif

#if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR))) || defined(_DETAIL)
#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
#endif

// keep this file in sync with LitGBufferPass.hlsl

struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 tangentOS    : TANGENT;
    float2 texcoord     : TEXCOORD0;
#if defined(_ECO_SMOOTH)
    float4 smoothMaterials    : TEXCOORD1; // ECO: the materials of the quad's four corners
    float4 smoothWeights      : TEXCOORD2; // ECO: which corner this vertex is
#else
    float2 staticLightmapUV   : TEXCOORD1;
    float2 dynamicLightmapUV  : TEXCOORD2;
#endif
    float2 overlayUV          : TEXCOORD3; // ECO: an ore's overlay, its place (only where color.b is full)
    half4 color               : COLOR; // ECO: r is the sky light, g the torches', b full on an ore's faces (on smoothed ground, the ore; a its own block's material)
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv                       : TEXCOORD0;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    float3 positionWS               : TEXCOORD1;
#endif

    float3 normalWS                 : TEXCOORD2;
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    half4 tangentWS                : TEXCOORD3;    // xyz: tangent, w: sign
#endif

    float4 voxelLight               : TEXCOORD4; // ECO: x the sky light, y the torches', zw an ore's overlay

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half4 fogFactorAndVertexLight   : TEXCOORD5; // x: fogFactor, yzw: vertex light
#else
    half  fogFactor                 : TEXCOORD5;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord              : TEXCOORD6;
#endif

#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS                : TEXCOORD7;
#endif

    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);
#ifdef DYNAMICLIGHTMAP_ON
    float2  dynamicLightmapUV : TEXCOORD9; // Dynamic lightmap UVs
#endif

#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion : TEXCOORD10;
#endif

#if defined(_ECO_SMOOTH)
    float4 smoothMaterials          : TEXCOORD11; // ECO
    float4 smoothWeights            : TEXCOORD12; // ECO
#endif

    float4 positionCS               : SV_POSITION;

    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

void InitializeInputData(Varyings input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    inputData.positionWS = input.positionWS;
#endif

#if defined(DEBUG_DISPLAY)
    inputData.positionCS = input.positionCS;
#endif

    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
#if defined(_NORMALMAP) || defined(_DETAIL)
    float sgn = input.tangentWS.w;      // should be either +1 or -1
    float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
    half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz);

    #if defined(_NORMALMAP)
    inputData.tangentToWorld = tangentToWorld;
    #endif
    inputData.normalWS = TransformTangentToWorld(normalTS, tangentToWorld);
#else
    inputData.normalWS = input.normalWS;
#endif

    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
    inputData.viewDirectionWS = viewDirWS;

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif
#ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
#else
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
#endif
                           
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);  

    #if defined(DEBUG_DISPLAY)
    #if defined(DYNAMICLIGHTMAP_ON)
    inputData.dynamicLightmapUV = input.dynamicLightmapUV;
    #endif
    #if defined(LIGHTMAP_ON)
    inputData.staticLightmapUV = input.staticLightmapUV;
    #else
    inputData.vertexSH = input.vertexSH;
    #endif
    #if defined(USE_APV_PROBE_OCCLUSION)
    inputData.probeOcclusion = input.probeOcclusion;
    #endif
    #endif
}

void InitializeBakedGIData(Varyings input, inout InputData inputData)
{
    #if defined(_SCREEN_SPACE_IRRADIANCE)
    inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, input.positionCS.xy, inputData.normalWS);
    #elif defined(DYNAMICLIGHTMAP_ON)
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
    #elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(input.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        input.positionCS.xy,
        input.probeOcclusion,
        inputData.shadowMask);
    #else
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
    #endif
}

///////////////////////////////////////////////////////////////////////////////
//                  Vertex and Fragment functions                            //
///////////////////////////////////////////////////////////////////////////////

// Used in Standard (Physically Based) shader
Varyings LitPassVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

    // normalWS and tangentWS already normalize.
    // this is required to avoid skewing the direction during interpolation
    // also required for per-vertex lighting and SH evaluation
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);

    half fogFactor = 0;
    #if !defined(_FOG_FRAGMENT)
        fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
    #endif

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
#if defined(_ECO_SMOOTH)
    // ECO: on smoothed ground, the quad's materials and corner weights, its ore in z, its own block's
    // material in w.
    output.voxelLight = float4(input.color.rg, round(input.color.b * 255.0), round(input.color.a * 255.0));
    output.smoothMaterials = input.smoothMaterials;
    output.smoothWeights = input.smoothWeights;
#else
    // ECO: the overlay's place on an ore's faces, below zero (none) everywhere else.
    output.voxelLight = float4(input.color.rg, input.color.b > 0.5h ? input.overlayUV : float2(-1.0, -1.0));
#endif

    // already normalized from normal transform to WS.
    output.normalWS = normalInput.normalWS;
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    real sign = input.tangentOS.w * GetOddNegativeScale();
    half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
#endif
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    output.tangentWS = tangentWS;
#endif

#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(tangentWS, output.normalWS, viewDirWS);
    output.viewDirTS = viewDirTS;
#endif

#if !defined(_ECO_SMOOTH) // ECO: the smoothed ground's chunks are never lightmapped, and use these channels
    OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
#ifdef DYNAMICLIGHTMAP_ON
    output.dynamicLightmapUV = input.dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif
#endif
    OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, output.probeOcclusion);
#ifdef _ADDITIONAL_LIGHTS_VERTEX
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
#else
    output.fogFactor = fogFactor;
#endif

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    output.positionWS = vertexInput.positionWS;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    output.positionCS = vertexInput.positionCS;

    return output;
}

#if defined(_ECO_SMOOTH)
// ECO: the colour of the smoothed ground at this pixel: its side, material, tile, variant, the climate's
// tint and its block's ore (see _SmoothMaterials above).
half3 EcoSmoothAlbedo(Varyings input)
{
    float3 p = input.positionWS;
    float3 n = normalize(input.normalWS);
    float3 dpdx = ddx(p);
    float3 dpdy = ddy(p);

    // The texel the pixel lies in, a little inside the ground so that flat ground does not flicker
    // between two cells: the dithering's cell.
    float texels = _BaseMap_TexelSize.w;
    int3 texel = (int3)floor((p - n * (0.5 / texels)) * texels);

    // The side it is textured from, by how much the surface itself faces each: the triangle's own
    // normal (from the position's derivatives), not the smoothed one, which leans toward the
    // neighbouring faces near an edge and would pick a side the face runs along, stretching the
    // texture into streaks.
    float3 faceNormal = cross(dpdy, dpdx);
    float faceLength = length(faceNormal);
    faceNormal = faceLength > 1e-8 ? faceNormal / faceLength : n;
    if (dot(faceNormal, n) < 0.0) faceNormal = -faceNormal;
    float3 share = pow(abs(faceNormal), 8.0);
    share /= share.x + share.y + share.z;
    float pickSide = EcoRandom(texel, 0u);
    uint axis = pickSide < share.x ? 0u : (pickSide < share.x + share.y ? 1u : 2u);

    // The material, by its corner's share (squared, so that each keeps to its corner more).
    float4 w = input.smoothWeights * input.smoothWeights;
    float pickMaterial = EcoRandom(texel, 1u) * (w.x + w.y + w.z + w.w);
    float4 ids = input.smoothMaterials;
    float id = pickMaterial < w.x ? ids.x : (pickMaterial < w.x + w.y ? ids.y : (pickMaterial < w.x + w.y + w.z ? ids.z : ids.w));

    // The quad's own block over most of its face: its corners' materials only near them (from a
    // corner's weight of 0.5, the middle of an edge, fading in to 0.9), dithered too. A corner's
    // material is what most of the cells around it hold, so without this a block amid another ground
    // took that ground's look.
    float4 raw = input.smoothWeights;
    float nearest = max(max(raw.x, raw.y), max(raw.z, raw.w));
    if (EcoRandom(texel, 5u) < saturate((0.9 - nearest) / 0.4)) id = input.voxelLight.w;
    float4 tiles = LOAD_TEXTURE2D(_SmoothMaterials, int2((int)round(id), 0));

    // Its top where the ground faces up enough (the threshold dithered), its bottom facing down, else its side.
    float threshold = 0.5 + (EcoRandom(texel, 2u) - 0.5) * 0.3;
    float tile = n.y > threshold ? tiles.x : (n.y < -0.7 ? tiles.z : tiles.y);

    // As the blocks' faces lay their tiles: +x along z, -x and +z mirrored, the top along x and z.
    float2 uv, du, dv;
    if (axis == 0u)
    {
        float s = faceNormal.x >= 0.0 ? 1.0 : -1.0;
        uv = float2(s * p.z, p.y);
        du = float2(s * dpdx.z, dpdx.y);
        dv = float2(s * dpdy.z, dpdy.y);
    }
    else if (axis == 1u)
    {
        uv = p.xz;
        du = dpdx.xz;
        dv = dpdy.xz;
    }
    else
    {
        float s = faceNormal.z >= 0.0 ? -1.0 : 1.0;
        uv = float2(s * p.x, p.y);
        du = float2(s * dpdx.x, dpdx.y);
        dv = float2(s * dpdy.x, dpdy.y);
    }

    int3 cell = int3((int2)floor(uv), (int)axis);
    float2 f = frac(uv);
    uint turn;
    bool mirror;
    float slot = EcoPickSlot((int)round(tile), EcoHash(cell, 3u), axis == 1u, turn, mirror);
    float slots = _SmoothAtlas.x;
    half4 colour = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, EcoSlotUv(slot, slots, EcoTurn(f, turn, mirror)),
        float2(du.x / slots, du.y), float2(dv.x / slots, dv.y));

    // Tinted pixels take the climate's colour, as on the blocks.
    half3 albedo = colour.rgb * _BaseColor.rgb;
    albedo *= lerp(half3(1.0h, 1.0h, 1.0h), ClimateTintAt(p), 1.0h - colour.a);

    // The ore of the quad's own block, over the rock.
    int ore = (int)round(input.voxelLight.z);
    if (ore > 0)
    {
        float oreSlots = _SmoothAtlas.y;
        float oreSlot = EcoPickSlot((int)_SmoothAtlas.z + ore - 1, EcoHash(cell, 4u), true, turn, mirror);
        half4 crystals = SAMPLE_TEXTURE2D_GRAD(_OreOverlay, sampler_OreOverlay, EcoSlotUv(oreSlot, oreSlots, EcoTurn(f, turn, mirror)),
            float2(du.x / oreSlots, du.y), float2(dv.x / oreSlots, dv.y));
        albedo = lerp(albedo, crystals.rgb * _BaseColor.rgb, crystals.a);
    }

    return albedo;
}
#endif

// Used in Standard (Physically Based) shader
void LitPassFragment(
    Varyings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_PARALLAXMAP)
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS = input.viewDirTS;
#else
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
#endif
    ApplyPerPixelDisplacement(viewDirTS, input.uv);
#endif

    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(input.uv, surfaceData);

#if defined(_ECO_SMOOTH)
    surfaceData.albedo = EcoSmoothAlbedo(input); // ECO
#else
    // ECO: tinted pixels take the climate's colour; the others keep the atlas's.
    half tinted = 1.0h - SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a;
    surfaceData.albedo *= lerp(half3(1.0h, 1.0h, 1.0h), ClimateTintAt(input.positionWS), tinted);

    // ECO: an ore's crystals over the rock it lies in (sampled everywhere, outside any branch, so its
    // mipmap level comes from the pixels' neighbours; nothing shows where there is no overlay).
    half4 ore = SAMPLE_TEXTURE2D(_OreOverlay, sampler_OreOverlay, input.voxelLight.zw);
    ore.a *= step(0.0, input.voxelLight.z);
    surfaceData.albedo = lerp(surfaceData.albedo, ore.rgb * _BaseColor.rgb, ore.a);
#endif
#if !defined(_SURFACE_TYPE_TRANSPARENT)
    // The atlas's alpha is the tint mask, not transparency. Transparent materials (the water, which
    // has no atlas) keep their own alpha.
    surfaceData.alpha = 1.0h;
#endif

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, surfaceData.normalTS, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    InitializeBakedGIData(input, inputData);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb *= SkyBrightness(input.voxelLight.x); // ECO
    color.rgb += surfaceData.albedo * TorchColour * TorchGlow(input.voxelLight.y); // ECO
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent());

    outColor = color;

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
