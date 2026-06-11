// Fullscreen UI depth preview — must render on Canvas (above Quest passthrough compositor).
Shader "QuestObjectron/EnvironmentDepthVisualizationUI"
{
    Properties
    {
        [Toggle] _UsePreprocessed ("Use preprocessed depth", Float) = 1
        _NearColor ("Near color", Color) = (1, 0.95, 0.2, 1)
        _FarColor ("Far color", Color) = (0.15, 0.35, 0.1, 1)
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Overlay"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_PreprocessedEnvironmentDepthTexture);
            UNITY_DECLARE_TEX2DARRAY(_EnvironmentDepthTexture);

            float _UsePreprocessed;
            fixed4 _NearColor;
            fixed4 _FarColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 SampleDepth(float2 uv, uint eye)
            {
                float3 uvEye = float3(uv.x, uv.y, eye);
                if (_UsePreprocessed > 0.5)
                {
                    return UNITY_SAMPLE_TEX2DARRAY(_PreprocessedEnvironmentDepthTexture, uvEye);
                }

                float r = UNITY_SAMPLE_TEX2DARRAY(_EnvironmentDepthTexture, uvEye).r;
                return fixed4(r, r, r, 1);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                uint eye = unity_StereoEyeIndex;
                fixed4 depthSample = SampleDepth(i.uv, eye);
                float energy = depthSample.r + depthSample.g + depthSample.b + depthSample.a;

                if (energy < 0.0001)
                {
                    return fixed4(1, 0, 1, 1);
                }

                if (depthSample.g + depthSample.b > 0.01)
                {
                    return fixed4(depthSample.rgb, 1);
                }

                float t = saturate(depthSample.r);
                return lerp(_FarColor, _NearColor, t);
            }
            ENDCG
        }
    }
    FallBack Off
}
