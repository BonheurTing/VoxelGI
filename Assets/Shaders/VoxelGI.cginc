#include "UnityCG.cginc"

#define MOVING_AVERAGE_MAX 255.0
#define EMISSIVE_SIG_BIT 16
#define EMISSIVE_EXP_BIT 8
#define PI 3.1415926
#define USE_YCOCG_CLAMP 1

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
uniform RWTexture3D<uint> OutOpacity : register(u4);
float3 CameraPosW;
float4x4  CameraView;
float4x4  CameraViewProj;
float4x4 CameraInvView;
float4x4 CameraInvViewProj;
float4x4 CameraReprojectInvViewProj;
float CameraFielfOfView; // degree
float CameraAspect;
int VoxelTextureResolution;
float VoxelSize;
float RayStepSize; // 0.5  --- < for 100

// Shadowmapping
float4x4 WorldToShadowVP;
SamplerState point_clamp_sampler;
SamplerState linear_clamp_sampler;
SamplerState sampler_point_repeat;
SamplerState sampler_linear_repeat;

// Cone Tracing
sampler2D _CameraDepthTexture;
sampler2D _CameraDepthNormalsTexture;
sampler2D ScreenNormal;
sampler2D ScreenAlbedo;
Texture3D<float4> ScreenConeTraceLighting;
float ScreenMaxMipLevel;
int ScreenMaxStepNum;
float ScreenAlphaAtten;
float ScreenScale;
float ScreenConeAngle;
float ScreenFirstStep;
float ScreenStepScale;
int EnableTemporalFilter;
float3 ConeTraceDirection;
float2 RandomUV;
Texture2D NoiseLUT;
float4 ScreenResolution;
float4 BlueNoiseResolution;
float4 BlueNoiseScale;

// Temporal Filter
sampler2D _CameraMotionVectorsTexture;
sampler2D CurrentScreenIrradiance;
sampler2D HistoricalScreenIrradiance;
float BlendAlpha;  // this frame
float TemporalClampAABBScale;

// Combine
sampler2D SceneDirect;
sampler2D VXGIIndirect;
int TemporalFrameCount;

// Debug
Texture3D<uint> VoxelTexAlbedo;
Texture3D<uint> VoxelTexNormal;
Texture3D<uint> VoxelTexEmissive;
Texture3D<uint> VoxelTexOpacity;
Texture3D<half4> VoxelTexLighting;
Texture3D<half4> VoxelTexIndirectLighting;
float EmissiveMulti;
int VisualizeDebugType;
float HalfPixelSize;
int EnableConservativeRasterization;
int DirectLightingDebugMipLevel;
int IndirectLightingDebugMipLevel;
sampler2D ScreenConeTraceIrradiance;
sampler2D ScreenBlendIrradiance;
sampler2D ScreenBilateralFiltering;

//********************************************************************************************************************************************************************
// Util.
//********************************************************************************************************************************************************************

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

float3 RgbToYcocg(float3 c)
{
	float tmp = (c.r + c.b) / 2.0;
	float y = (c.g + tmp) / 2.0;
	float co = (c.r - c.b) / 2.0;
	float cg = (c.g - tmp) / 2.0;
	return float3(y, co, cg);
}

float3 YcocgToRgb(float3 c)
{
	float r = c.x + c.y - c.z;
	float g = c.x + c.z;
	float b = c.x - c.y - c.z;
	return float3(r, g, b);
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

void OpacityMoveingAvg(uniform RWTexture3D<uint> outUav, int3 uvw, float2 val)
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

float4 UnityClipToClipPos(float4 pos)
{
	pos.y = -pos.y;
	return pos;
}

float CalcMipLevel(float size)
{
	return size <= 1.0 ? size : log2(size) + 1;
}

float3x3 GetTangentBasis(float3 TangentZ)
{
	float3 UpVector = abs(TangentZ.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
	float3 TangentX = normalize(cross(UpVector, TangentZ));
	float3 TangentY = cross(TangentZ, TangentX);
	return float3x3(TangentX, TangentY, TangentZ);
}

float TextureSDF(float3 position)
{
	position = .5f - abs(position - .5f);
	return min(min(position.x, position.y), position.z);
}

static half2 NeighborUVNoise[9] =
{
	half2(-1.0, -1.0),
	half2(-1.0, 0.0),
	half2(-1.0, 1.0),
	half2(0.0, -1.0),
	half2(0.0, 1.0),
	half2(1.0, -1.0),
	half2(1.0, 0.0),
	half2(1.0, 1.0),
	half2(0.0, 0.0),
};

static float3 Fibonacci_Lattice_Hemisphere_1[1] =
{
	float3(0.0, 0.0, 1.0)
};

static float3 Fibonacci_Lattice_Hemisphere_4[4] =
{
	float3(-0.731585503467728, -0.670192249370187, 0.125),
	float3(0.0810458159223954, 0.923475270768781, 0.375),
	float3(0.474962433620099, -0.619504387918014, 0.625),
	float3(-0.476722366176565, 0.0843254741286256, 0.875),
};

static float3 Fibonacci_Lattice_Hemisphere_8[8] =
{
	float3(-0.735927295315164, -0.674169686362497, 0.0625),
	float3(0.0858751947674414, 0.978503551819642, 0.1875),
	float3(0.577966879673501, -0.75385544768243, 0.3125),
	float3(-0.885472495182538, 0.156627616578975, 0.4375),
	float3(0.697614586708622, 0.443765296537886, 0.5625),
	float3(-0.188520590528854, -0.701287200044783, 0.6875),
	float3(-0.26869090798331, 0.517347993102423, 0.8125),
	float3(0.326869977432545, -0.119372391503427, 0.9375),
};

static float3 Fibonacci_Lattice_Hemisphere_16[16] =
{
	float3(-0.737008746736654, -0.675160384452218, 0.03125),
	float3(0.0870406817287227, 0.991783674610648, 0.09375),
	float3(0.600965734598756, -0.783853381276229, 0.15625),
	float3(-0.960864647623557, 0.169963426793115, 0.21875),
	float3(0.80969671855586, 0.515062774290544, 0.28125),
	float3(-0.243784330107323, -0.906865556680881, 0.34375),
	float3(-0.421159310808654, 0.810916624825992, 0.40625),
	float3(0.829731504061828, -0.303016614507021, 0.46875),
	float3(-0.783119519232434, -0.323260353426092, 0.53125),
	float3(0.341047499504824, 0.72879869688516, 0.59375),
	float3(0.225822703319255, -0.71995836279995, 0.65625),
	float3(-0.601554193574072, 0.34861295112696, 0.71875),
	float3(0.609658853079947, 0.134031788622115, 0.78125),
	float3(-0.308692885622987, -0.43908386427396, 0.84375),
	float3(-0.0543268871447994, 0.419236838592646, 0.90625),
	float3(0.189662913959254, -0.159848104675922, 0.96875),
};

bool IsInsideVoxelgrid(const float3 p)
{
	return abs(p.x) < 1.1f && abs(p.y) < 1.1f && abs(p.z) < 1.1f;
}

//********************************************************************************************************************************************************************
// Voxelization.
//********************************************************************************************************************************************************************

struct VoxelizationVsInput
{
	float4 vertex : POSITION;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
};

struct VoxelizationGsInput
{
	float4 posH : POSITION;
	float4 posW : POSITION1;
	float2 uv : TEXCOORD0;
	float3 normal : NORMAL;
};

struct VoxelizationFsInput
{
	float4 posH : SV_POSITION;
	float4 posW : POSITION1;
	float2 uv : TEXCOORD0;
	float3 normal : NORMAL;
	float4 aabb : TEXCOORD1;
};

VoxelizationGsInput VoxelizationVs(VoxelizationVsInput v)
{
	VoxelizationGsInput o;

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

[maxvertexcount(3)]
void VoxelizationGs(triangle VoxelizationGsInput i[3], inout TriangleStream<VoxelizationFsInput> triStream)
{
	int j;

	if (EnableConservativeRasterization == 0)
	{
		for (j = 0; j < 3; j++)
		{
			VoxelizationFsInput o = (VoxelizationFsInput)0;
			o.posH = i[j].posH;
			o.posW = i[j].posW;
			o.uv = i[j].uv;
			o.normal = i[j].normal;
			triStream.Append(o);
		}
		return;
	}

	float4 vertex[3];
	float2 texCoord[3];
	for (j = 0; j < 3; ++j)
	{
		vertex[j] = i[j].posH / i[j].posH.w; // vertex 
		texCoord[j] = i[j].uv;
	}

	// Change winding, otherwise there are artifacts for the back faces
	float3 clipTriangleNormal = normalize(cross(vertex[2].xyz - vertex[0].xyz, vertex[1].xyz - vertex[0].xyz));

	if (clipTriangleNormal.z > 0.f)
	{
		// swap 1 2
		float4 tempVertex = vertex[2];
		float2 tempTexC = texCoord[2];
		vertex[2] = vertex[1];
		vertex[1] = tempVertex;
		texCoord[2] = texCoord[1];
		texCoord[1] = tempTexC;
	}

	// Triangle plane to later calculate the new z coordinate.
	float4 trianglePlane;
	trianglePlane.xyz = normalize(cross(vertex[2].xyz - vertex[0].xyz, vertex[1].xyz - vertex[0].xyz));
	trianglePlane.w = -dot(vertex[0].xyz, trianglePlane.xyz);

	if (trianglePlane.z > 0.001f)
	{
		return;
	}

	// Axis aligned bounding box (AABB).
	// AABB initialized with maximum/minimum NDC values.
	float4 aabb = float4(1.0f, 1.0f, -1.0f, -1.0f);
	for (j = 0; j < 3; j++)
	{
		aabb.xy = min(aabb.xy, vertex[j].xy);
		aabb.zw = max(aabb.zw, vertex[j].xy);
	}
	// Add offset of half pixel size to AABB.
	aabb += float4(-HalfPixelSize.xx, HalfPixelSize.xx);

	// expand the triangle.
	float3 plane[3];
	for (j = 0; j < 3; j++)
	{
		plane[j] = cross(vertex[(j + 2) % 3].xyw, vertex[(j + 1) % 3].xyw);
		plane[j].z -= dot(HalfPixelSize.xx, abs(plane[j].xy));
	}

	// calculate intersection.
	float3 intersect[3];
	for (j = 0; j < 3; j++)
	{
		intersect[j] = cross(plane[(j + 1) % 3], plane[(j+ 2) % 3]);
		if (intersect[j].z != 0.0f)
		{
			intersect[j] /= intersect[j].z;
		}
	}

	for (j = 0; j < 3; j++)
	{
		vertex[j].xyz = intersect[j];
		vertex[j].w = 1.f;
		// Calculate the new z-Coordinate derived from a point on a plane.
		vertex[j].z = -(trianglePlane.x * intersect[j].x + trianglePlane.y * intersect[j].y + trianglePlane.w) / trianglePlane.z;
	}

	[unroll]
	for (j = 0; j < 3; j++)
	{
		VoxelizationFsInput o = (VoxelizationFsInput)0;
		o.posH = vertex[j];
		o.posW = i[j].posW;
		o.uv = texCoord[j];
		o.normal = i[j].normal;
		o.aabb = aabb;
		triStream.Append(o);
	}
}

half4 VoxelizationFs(VoxelizationFsInput i) : SV_Target
{
	if (EnableConservativeRasterization)
	{
		float2 inputPos = i.posH.xy;
		inputPos /= VoxelTextureResolution;
		inputPos = inputPos * float2(2.f, -2.f)  + float2(-1.f, 1.f);
		if ((inputPos.x < i.aabb.x ||
			inputPos.y < i.aabb.y ||
			inputPos.x > i.aabb.z ||
			inputPos.y > i.aabb.w)
			)
		{
			discard;
		}
	}

	float4 albedo = tex2Dlod(ObjAlbedo, float4(i.uv,0,0));
	i.normal = (i.normal + float3(1.f, 1.f, 1.f)) / 2.f;
	float4 normalA = float4(i.normal, albedo.a);
	float4 emissive = tex2Dlod(ObjEmissive, float4(i.uv, 0, 0));
	float4 opacity = float4(albedo.a, 0.f, 0.f, albedo.a);

	// calculate the 3d texture index
	float4 posV = mul(WorldToVoxel, i.posW);
	int3 uvw = int3(posV.xyz);

	// store the fragment in our 3d texture using a moving average
	MovingAverage(OutAlbedo, uvw, albedo, 0);
	MovingAverage(OutNormal, uvw, normalA, 1);
	MovingAverage(OutEmissive, uvw, emissive, 0);
	MovingAverage(OutOpacity, uvw, opacity, 3);

	return half4(1.f, 0.f, 0.f, 1.0f);
}

//********************************************************************************************************************************************************************
// Shadow Mapping.
//********************************************************************************************************************************************************************

struct ShadowFsInput
{
	float4 vertex : SV_POSITION;
};

ShadowFsInput ShadowVs(appdata_base v)
{
	ShadowFsInput o;
	float4 posW = mul(ObjWorld, v.vertex);
	o.vertex = mul(WorldToShadowVP, posW);
	return o;
}

float ShadowFs(ShadowFsInput i) : SV_Target
{
	return i.vertex.z;
}

//********************************************************************************************************************************************************************
// Cone Tracing.
//********************************************************************************************************************************************************************

#define INDIRECT_CONE_TRACE_MID 1
#if INDIRECT_CONE_TRACE_VERY_LOW
#define CONE_COUNT 1
#define Fibonacci_Lattice_Hemisphere Fibonacci_Lattice_Hemisphere_1
#elif INDIRECT_CONE_TRACE_LOW
#define CONE_COUNT 4
#define Fibonacci_Lattice_Hemisphere Fibonacci_Lattice_Hemisphere_4
#elif INDIRECT_CONE_TRACE_MID
#define CONE_COUNT 8
#define Fibonacci_Lattice_Hemisphere Fibonacci_Lattice_Hemisphere_8
#elif INDIRECT_CONE_TRACE_HIGH
#define CONE_COUNT 16
#define Fibonacci_Lattice_Hemisphere Fibonacci_Lattice_Hemisphere_16
#endif

struct ConeTracingVsInput
{
	float3 vertex : POSITION;
	float2 uv : TEXCOORD0; // 0-1
};

struct ConeTracingFsInput
{
	float4 pos : SV_POSITION;
	float2 uv : TEXCOORD0;
};

float3 CalculateScreenIrradiance(float4 voxelPos, float3 normal)
{
	if (TextureSDF(voxelPos / VoxelTextureResolution) < 0.0)
	{
		return float3(0.f, 0.f, 0.f);
	}

	normal = normalize(normal);
	float3 origin = voxelPos / VoxelTextureResolution;

	float3x3 TangentBasis = GetTangentBasis(normal);
	float coneTan = tan(ScreenConeAngle * 3.14159265f / 360.f);
	float offset, sampleRadius, step, ndotl;
	float3 coordinate, coneDir;
	float4 coneColor, resultColor = float4(0.f, 0.f, 0.f, 0.f);
	int coneIndex, stepNum;

	for (coneIndex = 0; coneIndex < CONE_COUNT; ++coneIndex)
	{
		coneColor = float4(0.f, 0.f, 0.f, 0.f);
		step = ScreenFirstStep / VoxelTextureResolution;
		offset = step;
		sampleRadius = offset * coneTan;
		coneDir = Fibonacci_Lattice_Hemisphere[coneIndex];
		coneDir = normalize(mul(coneDir, TangentBasis));

		coordinate = origin + offset * coneDir;
		stepNum = 0;
		[loop]
		while (coneColor.a < 0.95f && TextureSDF(coordinate) > 0.0f && stepNum <= ScreenMaxStepNum)
		{
			float mip = clamp(CalcMipLevel(sampleRadius * VoxelTextureResolution), 0.0, ScreenMaxMipLevel);
			float4 sampledRadiance = ScreenConeTraceLighting.SampleLevel(linear_clamp_sampler, coordinate, mip);
			coneColor += (1.f - pow(coneColor.a, ScreenAlphaAtten)) *  sampledRadiance;

			step *= ScreenStepScale;
			offset += step;
			sampleRadius = offset * coneTan;
			coordinate = origin + offset * coneDir;
			stepNum++;
		}

		ndotl = dot(coneDir, normal);
		resultColor += coneColor * ndotl;
	}
	//resultColor /= CONE_COUNT;

	return resultColor.xyz;
}

float2 UniformSampleDiskConcentric(float2 E)
{
	float2 p = 2 * E - 1;
	float Radius;
	float Phi;
	if (abs(p.x) > abs(p.y))
	{
		Radius = p.x;
		Phi = (PI / 4) * (p.y / p.x);
	}
	else
	{
		Radius = p.y;
		Phi = (PI / 2) - (PI / 4) * (p.x / p.y);
	}
	return float2(Radius * cos(Phi), Radius * sin(Phi));
}

float3 CalculateTemporalScreenIrradiance(float4 voxelPos, float3 normal, float2 hashNoise)
{
	if (TextureSDF(voxelPos / VoxelTextureResolution) < 0.0)
	{
		return float3(0.f, 0.f, 0.f);
	}

	normal = normalize(normal);
	float3 origin = voxelPos / VoxelTextureResolution;

	float3x3 TangentBasis = GetTangentBasis(normal);
	float coneTan = tan(ScreenConeAngle * 3.14159265f / 360.f);
	float offset, sampleRadius, step, ndotl;
	float3 coordinate, coneDir;
	float4 coneColor, resultColor = float4(0.f, 0.f, 0.f, 0.f);
	int stepNum = 0;

	coneColor = float4(0.f, 0.f, 0.f, 0.f);
	step = ScreenFirstStep / VoxelTextureResolution;
	offset = step;
	sampleRadius = offset * coneTan;

	coneDir.xy = UniformSampleDiskConcentric(hashNoise);
	coneDir.z = sqrt(1 - dot(coneDir.xy, coneDir.xy));
	coneDir = normalize(mul(coneDir, TangentBasis));
	//return coneDir;

	coordinate = origin + offset * coneDir;
	[loop]
	while (coneColor.a < 0.95f && TextureSDF(coordinate) > 0.0f && stepNum <= ScreenMaxStepNum)
	{
		float mip = clamp(CalcMipLevel(sampleRadius * VoxelTextureResolution), 0.0, ScreenMaxMipLevel);
		float4 sampledRadiance = ScreenConeTraceLighting.SampleLevel(linear_clamp_sampler, coordinate, mip);
		coneColor += (1.f - pow(coneColor.a, ScreenAlphaAtten)) *  sampledRadiance;

		step *= ScreenStepScale;
		offset += step;
		sampleRadius = offset * coneTan;
		coordinate = origin + offset * coneDir;
		stepNum++;
	}

	ndotl = dot(coneDir, normal);
	resultColor = coneColor * ndotl;

	return resultColor.xyz;
}

ConeTracingFsInput ConeTracingVs(ConeTracingVsInput v)
{
	ConeTracingFsInput o;
	o.pos = UnityClipToClipPos(float4(v.vertex, 1.f));
	o.uv = v.uv;
	return o;
}

float4 ConeTracingFs(ConeTracingFsInput i) : SV_Target
{
	// depth to world pos
	float depth = tex2D(_CameraDepthTexture, i.uv).r;
	float4 clipPos = float4(mad(2.0, float2(i.uv.x, 1 - i.uv.y), -1.0), depth, 1.0);
	float4 worldPos = mul(CameraReprojectInvViewProj, clipPos);
	worldPos /= worldPos.w;
	float3 voxelPos = mul(WorldToVoxel, worldPos);

	// normal
	float3 N = tex2D(ScreenNormal, i.uv).xyz;
	N = normalize(N * 2.f - 1.f);

	float3 screenIrradiance;
	if (EnableTemporalFilter > 0)
	{
		float2 noiseUV = (i.uv * ScreenResolution.xy * BlueNoiseResolution.zw * BlueNoiseScale.xy) + RandomUV;
		float2 dirNoise = NoiseLUT.SampleLevel(sampler_point_repeat, noiseUV, 0).xy;
		screenIrradiance = CalculateTemporalScreenIrradiance(float4(voxelPos, 1.f), N, dirNoise);
	}
	else
	{
		screenIrradiance = CalculateScreenIrradiance(float4(voxelPos, 1.f), N);
	}
	//return float4(screenIrradiance, 1.f);
	float3 albedo = tex2D(ScreenAlbedo, i.uv).xyz;
	float3 traceColor = screenIrradiance * albedo * ScreenScale;
	
    return float4(traceColor, 1.f);
}

//********************************************************************************************************************************************************************
// Temporal Filter.
//********************************************************************************************************************************************************************

struct TemporalFilterVsInput
{
	float3 vertex : POSITION;
	float2 uv : TEXCOORD0; // 0-1
};

struct TemporalFilterFsInput
{
	float4 pos : SV_POSITION;
	float2 uv : TEXCOORD0;
};

TemporalFilterFsInput TemporalFilterVs(TemporalFilterVsInput v)
{
	TemporalFilterFsInput o;
	o.pos = UnityClipToClipPos(float4(v.vertex, 1.f));
	o.uv = v.uv;
	return o;
}

float4 TemporalFilterFs(TemporalFilterFsInput i) : SV_Target
{
	float3 traceColor = tex2D(CurrentScreenIrradiance, i.uv).rgb;

	// get history
	float2 velocity = tex2D(_CameraMotionVectorsTexture, i.uv).rg;
	float2 historicalUV = i.uv - velocity;
	float3 historicalColor = tex2D(HistoricalScreenIrradiance, historicalUV).rgb;

	// clamp
#if USE_YCOCG_CLAMP
	historicalColor = RgbToYcocg(historicalColor);
#endif
	float3 minYcocg = float3(99999.f, 99999.f, 99999.f);
	float3 maxYcocg = float3(0.f, 0.f, 0.f);

	int k;
	for (k = 0; k < 9; ++k)
	{
		float2 neighborUV = i.uv + float2(NeighborUVNoise[k].x * ScreenResolution.z, NeighborUVNoise[k].y * ScreenResolution.w);
		float3 neighborColor = tex2D(CurrentScreenIrradiance, neighborUV).rgb;
#if USE_YCOCG_CLAMP
		neighborColor = RgbToYcocg(neighborColor);
#endif
		minYcocg = min(minYcocg, neighborColor);
		maxYcocg = max(maxYcocg, neighborColor);
	}
	historicalColor = clamp(historicalColor, minYcocg * rcp(TemporalClampAABBScale), maxYcocg * TemporalClampAABBScale);
#if USE_YCOCG_CLAMP
	historicalColor = YcocgToRgb(historicalColor);
#endif

	//blend
	BlendAlpha = 1.f - saturate((1.f - BlendAlpha) * (1 - length(velocity) * 30));
	float3 resultColor = lerp(historicalColor, traceColor, BlendAlpha);
	return float4(resultColor, 1.f);
}

//********************************************************************************************************************************************************************
// Combine.
//********************************************************************************************************************************************************************

struct CombineVsInput
{
	float3 vertex : POSITION;
	float2 uv : TEXCOORD0; // 0-1
};

struct CombineFsInput
{
	float4 pos : SV_POSITION;
	float2 uv : TEXCOORD0;
};

CombineFsInput CombineVs(CombineVsInput v)
{
	CombineFsInput o;
	o.pos = UnityClipToClipPos(float4(v.vertex, 1.f));
	o.uv = v.uv;
	return o;
}

float4 CombineFs(CombineFsInput i) : SV_Target
{
	float3 sceneColor = tex2D(SceneDirect, i.uv).rgb;
	float3 vxgiColor = tex2D(VXGIIndirect, i.uv).rgb;

	if (EnableTemporalFilter)
	{
		return float4(sceneColor + vxgiColor * TemporalFrameCount, 1.f);
	}
	else
	{
		return float4(sceneColor + vxgiColor, 1.f);
	}
}

//********************************************************************************************************************************************************************
// Debug.
//********************************************************************************************************************************************************************

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
	o.pos = UnityClipToClipPos(v.vertex);
	o.uv = v.uv;
	return o;
}

float4 VoxelizationDebugFs(VoxelizationDebugFsInput fsIn) : SV_Target
{
	float4 accumulatedColor = float4(0.f, 0.f, 0.f, 0.f);
	float fov = tan(CameraFielfOfView * 3.1415926f / 360.f);
	float3 rayDirView = float3(fov * CameraAspect * (fsIn.uv.x * 2.0f - 1.0f), fov *  (fsIn.uv.y * 2.0f - 1.0f), -1.f);
	float3 rayDirW = normalize(mul((float3x3)CameraInvView, normalize(rayDirView)));

	// float4 posV = mul(CameraInvViewProj, rayPosH)

	int totalSamples = VoxelTextureResolution * VoxelSize / RayStepSize;
	[loop]
	for (int i = 0; i < totalSamples; ++i)
	{
		float4 rayWorld = float4(CameraPosW + rayDirW * RayStepSize * i, 1.f);
		float3 uvwLerp = mul(WorldToVoxel, rayWorld).xyz;
		uint3 uvw = uvwLerp; // VoxelTextureResolution;
		uvwLerp /= VoxelTextureResolution;
		float opacity = DecodeGbuffer(VoxelTexOpacity[uvw]).x;
		float4 texSample = float4(0.f, 0.f, 0.f, 0.f);
		switch (VisualizeDebugType)
		{
		case 0: // albedo
			texSample = DecodeGbuffer(VoxelTexAlbedo[uvw]);
			texSample.a = opacity;
			break;
		case 1: // normal
			texSample = DecodeGbuffer(VoxelTexNormal[uvw]);
			texSample.rgb = (texSample.rgb * 2.f) - float3(1.f, 1.f, 1.f);
			texSample.a = opacity;
			break;
		case 2: // emissive
			texSample = DecodeGbuffer(VoxelTexEmissive[uvw]);
			texSample.rgb *= EmissiveMulti;
			texSample.a = opacity;
			break;
		case 3: // lighting
			texSample = VoxelTexLighting.SampleLevel(linear_clamp_sampler, uvwLerp, DirectLightingDebugMipLevel);
			break;
		case 4: // indirectlighting
			texSample = VoxelTexIndirectLighting.SampleLevel(linear_clamp_sampler, uvwLerp, IndirectLightingDebugMipLevel);
			break;
		case 5: // cone trace
			float3 traceColor = tex2D(ScreenConeTraceIrradiance, fsIn.uv).rgb;
			return float4(traceColor, 1.f);
		case 6: // TAA
			float3 blendColor = tex2D(ScreenBlendIrradiance, fsIn.uv).rgb;
			return float4(blendColor, 1.f);
		case 7: // BilateralFiltering
			float3 filterColor = tex2D(ScreenBilateralFiltering, fsIn.uv).rgb;
			return float4(filterColor, 1.f);
		default:
			break;
		}

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

