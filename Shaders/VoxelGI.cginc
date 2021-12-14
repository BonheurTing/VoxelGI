#include "UnityCG.cginc"


// Voxelization
float4x4 ObjWorld;
float4x4  VoxelizationVP;
float4x4 VoxelToWorld;
float4x4 WorldToVoxel;
sampler2D ObjAlbedo;
sampler2D ObjEmission;
uniform RWTexture3D<uint> OutAlbedo : register(u1);
uniform RWTexture3D<uint> OutNormal : register(u2);
uniform RWTexture3D<uint> OutEmission : register(u3);
// debug
float3 CameraPosW;
float4x4 CameraInvView;
float4x4 CameraInvViewProj;
float CameraFielfOfView; // degree
float CameraAspect;
int VoxelTextureResolution;
float VoxelSize;
float RayStepSize; // 0.5  --- < for 100
Texture3D<uint> VoxelTexAlbedo;


// Voxelization

//void mappingAtomicRGBA8Avg()

struct VoxelizationVsInput
{
	float4 vertex : POSITION;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
};

struct VoxelizationFsInput
{
	float4 posH : SV_POSITION;
	float4 posW : POSITION1;
	float2 uv : TEXCOORD0;
	float3 normal : NORMAL;
};

VoxelizationFsInput VoxelizationVs(VoxelizationVsInput v)
{
	VoxelizationFsInput o;

	o.posW = mul(ObjWorld, v.vertex);
	o.posH = mul(VoxelizationVP, o.posW);
	o.uv = v.uv;
	o.normal = UnityObjectToWorldNormal(v.normal);
	//o.posH.z = 0.5f;
	return o;
}

half4 VoxelizationFs(VoxelizationFsInput i) : SV_Target
{
	float4 posV = mul(WorldToVoxel, i.posW);
	int3 testuvw = int3(posV.xyz);
	testuvw.z = 130;
	OutAlbedo[testuvw] = uint(10);

	return half4(1.f, 0.f, 0.f, 1.0f);




	// pbr
	float4 albedo = tex2Dlod(ObjAlbedo, float4(i.uv,0,0));


	// location
	float3 texC = i.posH;
	texC.xy = (i.posH.xy + float2(1.f,1.f)) / 2.f;

	// calculate the 3d tecture index given the dominant projection axis
	int3 uvw = texC * VoxelTextureResolution;

	// store the fragment in our 3d texture using a moving average
	// mappingAtomicRGBA8Avg()
	//OutAlbedo[uvw] = albedo;
	

}

// Debug

struct VoxelizationDebugVsInput
{
	float3 vertex : POSITION;
	float2 uv : TEXCOORD0; // 0-1
};

struct VoxelizationDebugFsInput
{
	float4 pos : SV_POSITION;
	float2 uv : TEXCOORD0;
};

VoxelizationDebugFsInput VoxelizationDebugVs(VoxelizationVsInput v)
{
	VoxelizationDebugFsInput o;
	o.pos = v.vertex;
	o.uv = v.uv;
	return o;
}

bool IsInsideVoxelgrid(const float3 p)
{
	return abs(p.x) < 1.1f && abs(p.y) < 1.1f && abs(p.z) < 1.1f;
}

float4 VoxelizationDebugFs(VoxelizationDebugFsInput i) : SV_Target
{
	float4 accumulatedColor = float4(0.f, 0.f, 0.f, 0.f);
	float fov = tan(CameraFielfOfView * 3.1415926f / 360.f);
	float3 rayDirView = float3(fov * CameraAspect * (i.uv.x * 2.0f - 1.0f), fov *  (1.0f - i.uv.y * 2.0f), -1.f);
	float3 rayDirW = normalize(mul((float3x3)CameraInvView, normalize(rayDirView)));

	// float4 posV = mul(CameraInvViewProj, rayPosH)

	int totalSamples = VoxelTextureResolution * VoxelSize  / RayStepSize;
	[loop]
	for (int i = 0; i < totalSamples; ++i)
	{
		float4 rayWorld = float4(CameraPosW + rayDirW * RayStepSize * i, 1.f);
		uint3 uvw = mul(WorldToVoxel, rayWorld).xyz; // VoxelTextureResolution;
		uint texSample = VoxelTexAlbedo[uvw].x;
		if (texSample > 1)
		{
			accumulatedColor.g = 1.f;
			break;
		}


		/*
		if (texSample.a > 0)
		{
			accumulatedColor.rgb = accumulatedColor.rgb + (1.f - accumulatedColor.a) * texSample.rgb;
			accumulatedColor.a = accumulatedColor.a + (1.f - accumulatedColor.a) * texSample.a;
		}

		if (accumulatedColor.a > 0.95f)
		{
			break;
		}
		*/
	}


	return accumulatedColor;


}