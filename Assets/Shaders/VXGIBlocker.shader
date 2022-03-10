Shader "VXGI/Blocker"
{
    Properties
    {
		_Cutoff("Alpha Culloff", Range(0.5, 1)) = 0.5
    }
    SubShader
    {
		Tags { "Queue" = "AlphaTest" "IgnoreProjector" = "True" "RenderType" = "TransparentCutOut" }
		LOD 200

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
		#pragma surface surf Standard addshadow fullforwardshadows alphatest:_Cutoff

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        struct Input
        {
            float2 uv_MainTex;
        };

        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            o.Albedo = float3(1.f, 0.f, 0.f);
            o.Metallic = 0.f;
            o.Smoothness = 0.f;
            o.Alpha = 0.f;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
