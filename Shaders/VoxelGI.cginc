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
uint EncodeGbuffer(float4 value)
{
	uint res = (uint(value.x * 255.f) << 24) + (uint(value.y * 255.f) << 16)
		+ (uint(value.z * 255.f) << 8) + uint(value.w * 255.f);
	return res;
}

float4 DecodeGbuffer(uint value)
{
	float4 res = float4(0.f, 0.f, 0.f, 0.f);
	res.w = (value & 255) / 255.f;
	value = value >> 8;
	res.z = (value & 255) / 255.f;
	value = value >> 8;
	res.y= (value & 255) / 255.f;
	value = value >> 8;
	res.x = (value & 255) / 255.f;
	return res;
}

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
	return o;
}

half4 VoxelizationFs(VoxelizationFsInput i) : SV_Target
{
	// pbr
	float4 albedo = tex2Dlod(ObjAlbedo, float4(i.uv,0,0));

	// calculate the 3d tecture index
	float4 posV = mul(WorldToVoxel, i.posW);
	int3 uvw = int3(posV.xyz);
	OutAlbedo[uvw] = EncodeGbuffer(albedo);

	return half4(1.f, 0.f, 0.f, 1.0f);

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
	float3 rayDirView = float3(fov * CameraAspect * (i.uv.x * 2.0f - 1.0f), fov *  (i.uv.y * 2.0f - 1.0f), -1.f);
	float3 rayDirW = normalize(mul((float3x3)CameraInvView, normalize(rayDirView)));

	// float4 posV = mul(CameraInvViewProj, rayPosH)

	int totalSamples = VoxelTextureResolution * VoxelSize  / RayStepSize;
	[loop]
	for (int i = 0; i < totalSamples; ++i)
	{
		float4 rayWorld = float4(CameraPosW + rayDirW * RayStepSize * i, 1.f);
		uint3 uvw = mul(WorldToVoxel, rayWorld).xyz; // VoxelTextureResolution;
		float4 texSample = DecodeGbuffer(VoxelTexAlbedo[uvw]);

		if (texSample.a > 0.0001f)
		{
			accumulatedColor.rgb = accumulatedColor.rgb + (1.f - accumulatedColor.a) * texSample.rgb;
			accumulatedColor.a = accumulatedColor.a + (1.f - accumulatedColor.a) * texSample.a;
		}

		if (accumulatedColor.a > 0.95f)
		{
			break;
		}
	}
	return accumulatedColor;
}