﻿Shader "Hidden/VoxelGI"
{
    Properties
    {
    }
	CGINCLUDE
		#include "VoxelGI.cginc"
	ENDCG

	SubShader
	{
		pass
		{
			Name "Voxelization"

			Cull Off
			ZWrite Off
			ZTest Off

			CGPROGRAM
				#pragma enable_d3d11_debug_symbols
				#pragma require geometry
				#pragma target 5.0
				#pragma vertex VoxelizationVs
				#pragma geometry VoxelizationGs
				#pragma fragment VoxelizationFs
			ENDCG
		}

		pass
		{
			Name "VoxelShadow"

			Cull Back
			ZWrite On
			ZTest GEqual

			CGPROGRAM
				#pragma enable_d3d11_debug_symbols
				#pragma target 5.0
				#pragma vertex ShadowVs
				#pragma fragment ShadowFs
			ENDCG
		}

		pass
		{
			Name "VoxelVisualization"

			Cull Off
			ZWrite Off
			ZTest Off

			CGPROGRAM
				#pragma enable_d3d11_debug_symbols
				#pragma target 5.0
				#pragma vertex VoxelizationDebugVs
				#pragma fragment VoxelizationDebugFs
			ENDCG
		}
	}
}
