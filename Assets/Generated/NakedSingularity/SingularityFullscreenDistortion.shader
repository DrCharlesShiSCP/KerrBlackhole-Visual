Shader "Hidden/NakedSingularity/FullscreenDistortion"
{
    Properties
    {
        _CenterUV ("Center UV", Vector) = (0.5, 0.5, 0, 0)
        _LensStrength ("Lens Strength", Float) = 0.18
        _Power ("Power", Float) = 1.75
        _Epsilon ("Epsilon", Float) = 0.002
        _SwirlStrength ("Swirl Strength", Float) = 0.35
        _SwirlFalloff ("Swirl Falloff", Float) = 4.0
        _Chromatic ("Chromatic", Float) = 0.0035
        _RingFreq ("Ring Freq", Float) = 18.0
        _RingAmp ("Ring Amp", Float) = 0.012
        _RingFalloff ("Ring Falloff", Float) = 4.5
        _UseGlobalCenter ("Use Global Center (1/0)", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "FullscreenDistortion"
            ZTest Always ZWrite Off Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BlitTexture);
            SAMPLER(sampler_BlitTexture);

            float4 _CenterUV;
            float _LensStrength;
            float _Power;
            float _Epsilon;
            float _SwirlStrength;
            float _SwirlFalloff;
            float _Chromatic;
            float _RingFreq;
            float _RingAmp;
            float _RingFalloff;
            float _UseGlobalCenter;

            float4 _SingularityViewportPos;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            Varyings Vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            float2 Rotate2D(float2 p, float a)
            {
                float s = sin(a);
                float c = cos(a);
                return float2(p.x * c - p.y * s, p.x * s + p.y * c);
            }

            half4 SampleScene(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv);
            }

            half4 Frag (Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;

                float2 center = _CenterUV.xy;
                if (_UseGlobalCenter > 0.5)
                {
                    center = _SingularityViewportPos.xy;
                }

                float2 dir = uv - center;
                float r = length(dir);
                float inv = 1.0 / (pow(max(r, 1e-6), _Power) + _Epsilon);
                float2 nDir = (r > 1e-6) ? (dir / r) : float2(0, 0);

                float2 radialOffset = nDir * (_LensStrength * inv);
                float2 uv1 = uv + radialOffset;

                float swirlMask = saturate(1.0 - r * _SwirlFalloff);
                float angle = _SwirlStrength * inv * swirlMask;
                float2 dirRot = Rotate2D(dir, angle);
                float2 uv2 = center + dirRot;

                float2 uvFinal = lerp(uv1, uv2, swirlMask);

                float ring = sin(r * _RingFreq) * _RingAmp;
                float ringFade = 1.0 / (1.0 + r * _RingFalloff);
                uvFinal += nDir * (ring * ringFade);

                float ch = _Chromatic * inv;
                float2 uvR = uvFinal + nDir * ch;
                float2 uvB = uvFinal - nDir * ch;

                half4 colR = SampleScene(uvR);
                half4 colG = SampleScene(uvFinal);
                half4 colB = SampleScene(uvB);

                half3 outRGB = half3(colR.r, colG.g, colB.b);
                return half4(outRGB, 1);
            }
            ENDHLSL
        }
    }
}