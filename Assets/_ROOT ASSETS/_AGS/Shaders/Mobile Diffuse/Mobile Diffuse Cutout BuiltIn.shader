Shader "MTD/Mobile Diffuse Cutout BuiltIn"
{
    Properties
    {
        _MainTex ("Base Texture (RGB) Alpha (A)", 2D) = "white" {}
        _Color ("Tint Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5

        [Toggle] _UseEmission ("Use Emission", Float) = 0
        _EmissionMap ("Emission Texture (RGB)", 2D) = "white" {}
        _EmissionColor ("Emission Color", Color) = (0,0,0,1)

        // Shader-only double-sided control:
        // Off = 2 (Cull Back), On = 0 (Cull Off).
        // This directly controls the GPU render state, so no Editor script,
        // keyword, extra pass, or fragment branch is required.
        [Enum(Off,2,On,0)] _Cull ("Double Sided", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
            "RenderType" = "TransparentCutout"
        }

        LOD 100

        Cull [_Cull]
        ZWrite On

        CGPROGRAM

        // Lightweight Built-in Render Pipeline diffuse cutout shader.
        // noforwardadd prevents additional forward-light passes on mobile.
        #pragma surface surf Lambert alphatest:_Cutoff addshadow noforwardadd
        #pragma target 2.0

        sampler2D _MainTex;
        sampler2D _EmissionMap;

        fixed4 _Color;
        fixed _UseEmission;
        fixed4 _EmissionColor;

        struct Input
        {
            half2 uv_MainTex;
        };

        void surf(Input IN, inout SurfaceOutput output)
        {
            fixed4 textureColor = tex2D(_MainTex, IN.uv_MainTex) * _Color;

            output.Albedo = textureColor.rgb;
            output.Alpha = textureColor.a;

            fixed3 emissionTex = tex2D(_EmissionMap, IN.uv_MainTex).rgb;
            output.Emission = emissionTex * _EmissionColor.rgb * _UseEmission;
        }

        ENDCG
    }

    Fallback "Transparent/Cutout/Diffuse"
}