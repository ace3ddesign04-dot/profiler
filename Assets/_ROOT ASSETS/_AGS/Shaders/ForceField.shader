Shader "Custom/Lightweight Force Field Instanced"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.1, 0.6, 1, 0.35)
        _RimColor ("Rim Color", Color) = (0.4, 0.9, 1, 1)

        _Alpha ("Base Alpha", Range(0, 1)) = 0.25
        _RimStrength ("Rim Strength", Range(0, 5)) = 2.0
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5

        _ScanStrength ("Scan Line Strength", Range(0, 1)) = 0.35
        _ScanDensity ("Scan Line Density", Range(1, 50)) = 12
        _ScanSpeed ("Scan Line Speed", Range(-5, 5)) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            // GPU Instancing
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            UNITY_INSTANCING_BUFFER_START(ForceFieldProps)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _BaseColor)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _RimColor)

                UNITY_DEFINE_INSTANCED_PROP(float, _Alpha)
                UNITY_DEFINE_INSTANCED_PROP(float, _RimStrength)
                UNITY_DEFINE_INSTANCED_PROP(float, _RimPower)

                UNITY_DEFINE_INSTANCED_PROP(float, _ScanStrength)
                UNITY_DEFINE_INSTANCED_PROP(float, _ScanDensity)
                UNITY_DEFINE_INSTANCED_PROP(float, _ScanSpeed)
            UNITY_INSTANCING_BUFFER_END(ForceFieldProps)

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;

                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half3 worldNormal : TEXCOORD0;
                half3 viewDir : TEXCOORD1;
                float3 worldPos : TEXCOORD2;

                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert(appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.pos = UnityObjectToClipPos(v.vertex);

                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldPos = worldPos;

                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = _WorldSpaceCameraPos.xyz - worldPos;

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                fixed4 baseColor = UNITY_ACCESS_INSTANCED_PROP(ForceFieldProps, _BaseColor);
                fixed4 rimColor = UNITY_ACCESS_INSTANCED_PROP(ForceFieldProps, _RimColor);

                half alphaValue = UNITY_ACCESS_INSTANCED_PROP(ForceFieldProps, _Alpha);
                half rimStrength = UNITY_ACCESS_INSTANCED_PROP(ForceFieldProps, _RimStrength);
                half rimPower = UNITY_ACCESS_INSTANCED_PROP(ForceFieldProps, _RimPower);

                half scanStrength = UNITY_ACCESS_INSTANCED_PROP(ForceFieldProps, _ScanStrength);
                half scanDensity = UNITY_ACCESS_INSTANCED_PROP(ForceFieldProps, _ScanDensity);
                half scanSpeed = UNITY_ACCESS_INSTANCED_PROP(ForceFieldProps, _ScanSpeed);

                half3 normalDir = normalize(i.worldNormal);
                half3 viewDir = normalize(i.viewDir);

                // Fresnel rim glow
                half fresnel = 1.0h - saturate(dot(normalDir, viewDir));
                fresnel = pow(fresnel, rimPower);

                // Animated horizontal scan lines
                half scan = frac((i.worldPos.y * scanDensity) + (_Time.y * scanSpeed));
                scan = smoothstep(0.0h, 0.08h, scan) * smoothstep(0.16h, 0.08h, scan);

                half rim = fresnel * rimStrength;
                half scanGlow = scan * scanStrength;

                half alpha = saturate(alphaValue + rim + scanGlow) * baseColor.a;

                half3 color = baseColor.rgb;
                color += rimColor.rgb * rim;
                color += rimColor.rgb * scanGlow;

                return fixed4(color, alpha);
            }
            ENDCG
        }
    }

    FallBack Off
}