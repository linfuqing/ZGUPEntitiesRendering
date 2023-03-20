Shader "ZG/GenerateDepth"
{
    HLSLINCLUDE

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"

    float4 _BlitTexture_TexelSize;
    float4 _CameraDepthTexture_TexelSize;

    float SampleDepth(float2 texcoord, float2 texelSize, Texture2D t, SamplerState s)
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv = UnityStereoTransformScreenSpaceTex(texcoord);
        float2 offset = texelSize * 0.5;
        float x = SAMPLE_TEXTURE2D_X(t, s, uv + offset).r;
        float y = SAMPLE_TEXTURE2D_X(t, s, uv - offset).r;
        float z = SAMPLE_TEXTURE2D_X(t, s, uv + float2(offset.x, -offset.y)).r;
        float w = SAMPLE_TEXTURE2D_X(t, s, uv + float2(-offset.x, offset.y)).r;

        float4 readDepth = float4(x,y,z,w);
        #if UNITY_REVERSED_Z
            readDepth.xy = min(readDepth.xy, readDepth.zw);
            readDepth.x = min(readDepth.x, readDepth.y);
        #else
            readDepth.xy = max(readDepth.xy, readDepth.zw);
            readDepth.x = max(readDepth.x, readDepth.y);
        #endif
        return readDepth.x;
    }

    float FragDepth(Varyings input) : SV_Target
    {
        return SampleDepth(input.texcoord, _CameraDepthTexture_TexelSize.xy, _CameraDepthTexture, sampler_CameraDepthTexture);
    }

    float FragBlit(Varyings input) : SV_Target
    {
        return SampleDepth(input.texcoord, _BlitTexture_TexelSize.xy, _BlitTexture, sampler_PointClamp);
    }
    ENDHLSL

    SubShader
    {
        Tags{ "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Copy Depth"

            HLSLPROGRAM
                #pragma vertex Vert//FullscreenVert
                #pragma fragment FragDepth
            ENDHLSL
        }

        Pass
        {
            Name "Generate Depth"

            HLSLPROGRAM
                #pragma vertex Vert//FullscreenVert
                #pragma fragment FragBlit
            ENDHLSL
        }
    }
}