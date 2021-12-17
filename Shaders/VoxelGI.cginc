#include "UnityCG.cginc"

#define MOVING_AVERAGE_MAX 255.0
#define EMISSIVE_SIG_BIT 16
#define EMISSIVE_EXP_BIT 8

// Voxelization
float4x4 ObjWorld;
float4x4  VoxelizationForwardVP;
float4x4  VoxelizationRightVP;
float4x4  VoxelizationUpVP;
float4x4 VoxelToWorld;
float4x4 WorldToVoxel;
sampler2D ObjAlbedo;
sampler2D ObjEmissive;
uniform RWTexture3D<uint> OutAlbedo : register(u1);
uniform RWTexture3D<uint> OutNormal : register(u2);
uniform RWTexture3D<uint> OutEmissive : register(u3);
uniform RWTexture3D<uint> OutBrightness : register(u4);
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
Texture3D<uint> VoxelTexNormal;
Texture3D<uint> VoxelTexEmissive;
Texture3D<uint> VoxelTexBrightness;
int VisualizeDebugType;

// Util
float3 RgbToHsl(float3 c)
{
	float4 K = float4(0.0f, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
	float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
	float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));

	float d = q.x - min(q.w, q.y);
	float e = 1.0e-10;
	return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 HslToRgb(float3 c)
{
	float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
	float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
	return abs(c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y));
}

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
 
uint EncodeFloat2ToUint248(float2 value)
{
	uint res = asuint(value.x);
	res = res & 0x7FFFFF00;
	res = res + uint(value.y * 255);
	return res;
}

float2 DecodeUint248ToFloat2(uint value)
{
	float2 res = float2(0.f, 0.f);
	res.y = (value & 255) / 255.f;
	res.x = asfloat(value & 0x00);
	return res;
}

uint EncodeEmissive(float4 value)
{
	uint res = uint(clamp(value.x * 255.f / 10.f, 0.f, 255.f)) << 24;
	res = res+ uint(clamp(value.y * 255.f / 10.f, 0.f, 255.f)) << 16;
	res = res + uint(clamp(value.z * 255.f / 10.f, 0.f, 255.f)) << 8;
	res = res + uint(clamp(value.y * 255.f, 0.f, 255.f));
	return res;
}

float4 DecodeEmissive(uint value)
{
	float4 res = float4(0.f, 0.f, 0.f, 0.f);
	res.w = (value & 255) / 255.f;
	value = value >> 8;
	res.z = (value & 255) / 255.f * 10.f;
	value = value >> 8;
	res.y = (value & 255) / 255.f * 10.f;
	value = value >> 8;
	res.x = (value & 255) / 255.f * 10.f;
	return res;
}

void MovingAverage(uniform RWTexture3D<uint> outUav, int3 uvw, float4 val, int codeType)
{
	uint newVal = 0;
	if (codeType == 2)
	{
		newVal = EncodeEmissive(val);
	}
	else
	{
		newVal = EncodeGbuffer(val);
	}
	uint prevStoredVal = 0xFFFFFFFF;
	uint curStoredVal;
	// Loop as long as destination value gets changed by other threads

	InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
	while (curStoredVal != prevStoredVal)
	{
		prevStoredVal = curStoredVal;
		float4 gbuffer = float4(0.f, 0.f, 0.f, 0.f);
		if (codeType == 2)
		{
			gbuffer = DecodeEmissive(curStoredVal);
		}
		else
		{
			gbuffer = DecodeGbuffer(curStoredVal);
		}
		gbuffer.w *= MOVING_AVERAGE_MAX;
		gbuffer.xyz = (gbuffer.xyz * gbuffer.w); // Denormalize
		float4 curValF = gbuffer + val; // Add new value
		curValF.xyz /= max(curValF.w, 0.001f); // Renormalize
		curValF.w /= MOVING_AVERAGE_MAX;
		curValF.w += 0.001f;

		if (codeType == 2)
		{
			newVal = EncodeEmissive(curValF);
		}
		else
		{
			newVal = EncodeGbuffer(curValF);
		}
		InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
	}
}

void BrightnessMoveingAvg(uniform RWTexture3D<uint> outUav, int3 uvw, float2 val)
{
	uint newVal = EncodeFloat2ToUint248(val);
	uint prevStoredVal = 0xFFFFFFFF;
	uint curStoredVal;
	// Loop as long as destination value gets changed by other threads

	InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
	while (curStoredVal != prevStoredVal)
	{
		prevStoredVal = curStoredVal;
		float2 gbuffer = DecodeUint248ToFloat2(curStoredVal);
		gbuffer.y *= MOVING_AVERAGE_MAX;
		gbuffer.x = (gbuffer.x * gbuffer.y); // Denormalize
		float2 curValF = gbuffer + val; // Add new value
		curValF.x /= max(curValF.y, 0.001f); // Renormalize
		curValF.y /= MOVING_AVERAGE_MAX;
		curValF.y += 0.001f;
		newVal = EncodeFloat2ToUint248(curValF);
		InterlockedCompareExchange(outUav[uvw], prevStoredVal, newVal, curStoredVal);
	}
}

// Voxelization
struct VoxelizationVsInput
{
	float4 vertex : POSITION;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
};

/*
struct VoxelizationGsInput
{
	float4 posH : SV_POSITION;
	float4 posW : POSITION1;
	float2 uv : TEXCOORD0;
	float3 normal : NORMAL;
};
*/
struct VoxelizationFsInput
{
	float4 posH : SV_POSITION;
	float4 posW : POSITION1;
	float2 uv : TEXCOORD0;
	float3 normal : NORMAL;
};

VoxelizationGsInput VoxelizationVs(VoxelizationVsInput v)
{
	VoxelizationFsInput o;

	o.posW = mul(ObjWorld, v.vertex);
	o.uv = v.uv;
	o.normal = UnityObjectToWorldNormal(v.normal);
	if (abs(o.normal.x) > abs(o.normal.y)) 
	{
		if (abs(o.normal.x) > abs(o.normal.z)) 
		{
			o.posH = mul(VoxelizationRightVP, o.posW);
		}
		else
		{
			o.posH = mul(VoxelizationForwardVP, o.posW);
		}
	}
	else
	{
		if (abs(o.normal.z) > abs(o.normal.y))
		{
			o.posH = mul(VoxelizationForwardVP, o.posW);
		}
		else
		{
			o.posH = mul(VoxelizationUpVP, o.posW);
		}
	}
	return o;
}

//[maxvertexcount(3)]
//void VoxelizationGs(triangle VoxelizationGsInput i[3], inout TriangleStream<VoxelizationFsInput> triStream)
//{

//}

half4 VoxelizationFs(VoxelizationFsInput i) : SV_Target
{
	// pbr
	float4 albedo = tex2Dlod(ObjAlbedo, float4(i.uv,0,0));
	i.normal = (i.normal + float3(1.f, 1.f, 1.f)) / 2.f;
	float4 normalA = float4(i.normal, albedo.a);
	float4 emissive = tex2Dlod(ObjEmissive, float4(i.uv, 0, 0));
	//emissive.a = albedo.a;
	float4 brightness = float4(albedo.a, 0.f, 0.f, albedo.a);

	// calculate the 3d tecture index
	float4 posV = mul(WorldToVoxel, i.posW);
	int3 uvw = int3(posV.xyz);
	// store the fragment in our 3d texture using a moving average
	MovingAverage(OutAlbedo, uvw, albedo, 0);
	MovingAverage(OutNormal, uvw, normalA, 1);
	MovingAverage(OutEmissive, uvw, emissive, 0);
	MovingAverage(OutBrightness, uvw, brightness, 3);

	return half4(1.f, 0.f, 0.f, 1.0f);
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
		float brightness = DecodeGbuffer(VoxelTexBrightness[uvw]).x;
		float4 texSample = float4(0.f, 0.f, 0.f, 0.f);
		switch (VisualizeDebugType)
		{
		case 0: // albedo
			texSample = DecodeGbuffer(VoxelTexAlbedo[uvw]);
			break;
		case 1: // normal
			texSample = DecodeGbuffer(VoxelTexNormal[uvw]);
			texSample.rgb = (texSample.rgb * 2.f) - float3(1.f, 1.f, 1.f);
			break;
		case 2: // emissive
			texSample = DecodeGbuffer(VoxelTexEmissive[uvw]);
			texSample.rgb *= 1.f;
			break;
		}
		texSample.a = brightness;

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