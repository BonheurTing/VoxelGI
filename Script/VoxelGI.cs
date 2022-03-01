using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.Rendering;

public enum VoxelGbufferType
{
    Albedo = 0,
    Normal,
    Emissive,
    Lighting,
    IndirectLighting,
    ConeTrace,
    TemporalFilter
}

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
public class VoxelGI : MonoBehaviour
{
    //********************************************************************************************************************************************************************
    // Reflection.
    //********************************************************************************************************************************************************************

    [Header("Voxelization")]
    public int ShadowMapResolution = 8;
    public float ShadowMapRange = 50f;
    public int VoxelTextureResolution = 256;
    public float VoxelSize = 0.25f;
    public int StableMipLevel = 2;
    public bool EnableConservativeRasterization;
    [Range(0.0f, 3.0f)]
    public float ConsevativeRasterizeScale = 1.5f;

    [Header("Direct Lighting")]
    public Light SunLight;
    [Range(0.0f, 10.0f)]
    public float LightIndensityMulti = 1.0f;
    [Range(0.0f, 10.0f)]
    public float EmissiveMulti = 1.0f;
    public float ShadowSunBias = 0.5f;
    public float ShadowNormalBias = 0.0f;

    [Header("Indirect Lighting")]
    public bool EnableSecondBounce = false;
    [Range(1, 32)]
    public int IndirectLightingMaxStepNum = 32;
    [Range(1.0f, 10.0f)]
    public float IndirectLightingAlphaAtten = 5f;
    [Range(0.0f, 10.0f)]
    public float IndirectLightingScale = 1f;
    [Range(0.5f, 3f)]
    public float IndirectLightingFirstStep = 1f;
    [Range(1f, 3f)]
    public float IndirectLightingStepScale = 1.5f;
    [Range(20f, 150f)]
    public float IndirectLightingConeAngle = 90f;
    [Range(0, 5)]
    public int IndirectLightingMinMipLevel = 0;

    [Header("Cone Tracing")]
    [SerializeField]
    Texture2D BlueNoise_LUT = null;
    [Range(1, 32)]
    public int ScreenMaxStepNum = 32;
    [Range(1.0f, 10.0f)]
    public float ScreenAlphaAtten = 5f;
    [Range(0.0f, 10.0f)]
    public float ScreenScale = 1f;
    [Range(0.5f, 3f)]
    public float ScreenFirstStep = 1.5f;
    [Range(1f, 3f)]
    public float ScreenStepScale = 1f;
    [Range(20f, 150f)]
    public float ScreenConeAngle = 90f;

    [Header("TemporalFilter")]
    public bool EnableTemporalFilter = true;
    [Range(0f, 1f)]
    public float TemporalBlendAlpha = 0.01f;
    public float ClampAABBScale = 1.5f;
    public Vector2 BlueNoiseScale = new Vector2(1, 1);

    [Header("Voxel Visualization")]
    public bool DebugMode = false;
    public VoxelGbufferType DebugType;
    [Range(0, 10)]
    public int DirectLightingDebugMipLevel = 0;
    [Range(0, 10)]
    public int IndirectLightingDebugMipLevel = 0;
    [Range(0.01f, 0.5f)]
    public float RayStepSize = 0.03f;

    [System.Serializable]
    public class TestClass
    {
        [SerializeField]
        public int a1;
        [SerializeField]
        [Range(0.0f, 0.5f)]
        public float a2;
    };
    [SerializeField]
    public TestClass TestSerialization;

    //********************************************************************************************************************************************************************
    // Private.
    //********************************************************************************************************************************************************************

    private enum PassIndex
    {
        EPIVoxelization = 0,
        EPIVoxelShadow = 1,
        EPIConeTracing = 2,
        EPITemporalFilter = 3,
        EPICombine = 4,
        EPIVoxelizationDebug
    }

    private int mComputeKernelIdDirectLighting;
    private int mComputeKernelIdMipmap;
    private int mComputeKernelIdCopyTexture3D;
    private int mComputeKernelIdIndirectLighting;

    // Voxelization Debug
    private Camera RenderCamera;
    private UnityEngine.Rendering.CommandBuffer mCommandBuffer = null;
    private Material mGiMaterial;

    // Voxelization
    private Camera VoxelizationCamera;
    private Matrix4x4 ForwordViewMatrix;
    private Matrix4x4 RightViewMatrix;
    private Matrix4x4 UpViewMatrix;

    public float VoxelizationRange
    {
        get
        {
            return VoxelSize * VoxelTextureResolution;
        }
    }

    private Vector3 mOrigin
    {
        get
        {
            Vector3 fixedCameraPos = RenderCamera.transform.position / (VoxelSize * Mathf.Pow(2, StableMipLevel));
            Vector3Int intPosition = new Vector3Int((int)fixedCameraPos.x, (int)fixedCameraPos.y, (int)fixedCameraPos.z);
            fixedCameraPos.x = intPosition.x  * (VoxelSize * Mathf.Pow(2, StableMipLevel));
            fixedCameraPos.y = intPosition.y  * (VoxelSize * Mathf.Pow(2, StableMipLevel));
            fixedCameraPos.z = intPosition.z  * (VoxelSize * Mathf.Pow(2, StableMipLevel));
            return fixedCameraPos;
        }
    }

    private int mMipLevel
    {
        get
        {
            return (int)Mathf.Log(VoxelTextureResolution, 2) + 1;
        }
    }

    private static Mesh mMesh;
    private RenderTexture RTSceneColor;
    private RenderTexture RTConeTracing;
    private int DummyTargetID;
    private RenderTexture DummyTex;
    private RenderTextureDescriptor DummyDesc;
    
    private RenderTexture UavAlbedo;
    private RenderTexture UavNormal;
    private RenderTexture UavEmissive;
    private RenderTexture UavOpacity;
    private RenderTextureDescriptor mGBufferDesc;

    // Cone Tracing
    private RenderTexture ConeTracingRT;
    private RenderTextureDescriptor mConeTraceDesc;

    // Temporal
    public int mHaltonValueCount = 8;
    private Matrix4x4 PreLocalToWorld;
    private System.Random mDirRand;
    private bool mPingPongFlag = false;
    private RenderTexture ScreenIrradianceRT0;
    private RenderTexture ScreenIrradianceRT1;
    private Vector4 mScreenResolution
    {
        get
        {
            Resolution resolution = Screen.currentResolution;
            return new Vector4(resolution.width, resolution.height, 1.0f / resolution.width, 1.0f / resolution.height);
        }
    }
    private Vector4 mBlueNoiseResolution;

    private int mRandomOffsetIndex = 0;
    private int mConeTraceCount = 0;
    private double[,] mHemisphere8 = new double[,]{
        { -0.735927295315164, -0.674169686362497, 0.0625 },
        { 0.0858751947674414, 0.978503551819642, 0.1875 },
        { 0.577966879673501, -0.75385544768243, 0.3125 },
        { -0.885472495182538, 0.156627616578975, 0.4375 },
        { 0.697614586708622, 0.443765296537886, 0.5625 },
        { -0.188520590528854, -0.701287200044783, 0.6875 },
        { -0.26869090798331, 0.517347993102423, 0.8125 },
        { 0.326869977432545, -0.119372391503427, 0.9375 }
    };

    // computer shader :
    // Lighting
    ComputeShader mComputeShader;
    private RenderTexture UavLighting;
    private RenderTextureDescriptor mLightingDesc;
    RenderTexture mSecondLightingRT;
    RenderTexture mLightingPingPongRT;

    // Shadowing
    private Camera ShadowCamera;
    private RenderTextureDescriptor mShadowCameraDesc;

    private RenderTexture ShadowDummy;
    private RenderTextureDescriptor mShadowDesc;
    private RenderTexture mShadowDepth;
    private RenderTextureDescriptor mShadowDepthDesc;
    private Matrix4x4 ShadowViewMatrix;

    //********************************************************************************************************************************************************************
    // MonoBehaviour.
    //********************************************************************************************************************************************************************

    #region MonoBehaviour

    void Awake()
    {
        // Setup.
        RenderCamera = gameObject.GetComponent<Camera>();
        RenderCamera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors;
        mGiMaterial = new Material(Shader.Find("Hidden/VoxelGI"));
        mDirRand = new System.Random();
        mBlueNoiseResolution = new Vector4(BlueNoise_LUT.width, BlueNoise_LUT.height, 1.0f / BlueNoise_LUT.width, 1.0f / BlueNoise_LUT.height);

        BuildCamera();
        BuildDescripters();

        if (mCommandBuffer == null)
        {
            mCommandBuffer = new CommandBuffer();
            mCommandBuffer.name = "VXGI_CommandBuffer";
        }

        if (mComputeShader == null)
        {
            mComputeShader = (ComputeShader)Resources.Load("VoxelGICompute");
            mComputeKernelIdDirectLighting = mComputeShader.FindKernel("VoxelDirectLighting");
            mComputeKernelIdMipmap = mComputeShader.FindKernel("MipmapGeneration");
            mComputeKernelIdCopyTexture3D = mComputeShader.FindKernel("CopyTexture3D");
            mComputeKernelIdIndirectLighting = mComputeShader.FindKernel("VoxelIndirectLighting");
        }

        UpdateParam();
    }

    void OnEnable()
    {
        //Screen.SetResolution(1900, 900, true);
        //Camera.main.depthTextureMode = DepthTextureMode.Depth;

        if (mCommandBuffer != null)
        {
            RenderCamera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, mCommandBuffer);
        }

        BuildResources();
    }

    void OnPreRender()
    {
        UpdateParam();

        if (mCommandBuffer != null)
        {
//             BeforeVoxelization();
//             EndVoxelization();
            BeginRender();
            RenderShodowMap();
            RenderVoxel();
            ComputeDirectLighting();
            if(EnableSecondBounce)
            {
                ComputeIndirectLighting();
            }
            ScreenConeTracing();
            if (EnableTemporalFilter)
            {
                TemporalFilter();
            }
            Combine();
            if (DebugMode)
            {
                RenderDebug();
            }
            EndRender();
        }
    }

    void OnDisable()
    {
        if (mCommandBuffer != null)
        {
            RenderCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, mCommandBuffer);
        }

        ReleaseResources();
    }

    void OnDestroy()
    {
        if (mCommandBuffer != null)
        {
            mCommandBuffer.Dispose();
        }
    }

    void Update()
    {
    }

    #endregion

    //********************************************************************************************************************************************************************
    // Utils.
    //********************************************************************************************************************************************************************

    #region Util

    public Vector3 GenDirection()
    {
        double theta = mDirRand.NextDouble() * 1.57;
        double fi = mDirRand.NextDouble() * 6.2832;
        double x = System.Math.Sin(theta) * System.Math.Cos(fi);
        double y = System.Math.Sin(theta) * System.Math.Sin(fi);
        double z = System.Math.Cos(theta);
        Vector3 randVec = new Vector3((float)x, (float)y, (float)z);
        int id = mConeTraceCount % 8;
        mConeTraceCount = (mConeTraceCount + 1) % 8;
        // Vector3 randVec = new Vector3((float)mHemisphere8[id, 0], (float)mHemisphere8[id, 1], (float)mHemisphere8[id, 2]);
        return randVec;
    }

    private float GetHaltonValue(int index, int radix)
    {
        float result = 0f;
        float fraction = 1f / radix;

        while (index > 0)
        {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }
        return result;
    }

    private Vector2 GenerateRandomOffset()
    {
        int  count = mHaltonValueCount -1;
        Vector2 offset = new Vector2(GetHaltonValue(mRandomOffsetIndex, 2), GetHaltonValue(mRandomOffsetIndex, 3));
        mRandomOffsetIndex++;
        mRandomOffsetIndex = mRandomOffsetIndex >= count ? 0 : mRandomOffsetIndex;
        return offset;
    }

    public Vector2 GenRandomUV()
    {
        return new Vector2((float)mDirRand.NextDouble(), (float)mDirRand.NextDouble());
    }

    public Matrix4x4 voxelToWorld
    {
        get
        {
            var origin = mOrigin - new Vector3(VoxelizationRange, VoxelizationRange, VoxelizationRange) * 0.5f;
            return Matrix4x4.TRS(origin, Quaternion.identity, Vector3.one * VoxelSize);
        }
    }

    public Matrix4x4 worldToVoxel
    {
        get { return voxelToWorld.inverse; }
    }

    public void BuildCamera()
    {
        var VoxelizationCameraObj = new GameObject("Voxelization Orth Camera") { hideFlags = HideFlags.HideAndDontSave  };
        VoxelizationCameraObj.SetActive(false);
        
        VoxelizationCamera = VoxelizationCameraObj.AddComponent<Camera>();
        VoxelizationCamera.allowMSAA = true;
        VoxelizationCamera.orthographic = true;
        VoxelizationCamera.pixelRect = new Rect(0f, 0f, 1f, 1f);
        VoxelizationCamera.depth = 1f;
        VoxelizationCamera.enabled = false;

        var ShadowCameraObj = new GameObject("Shadow Camera") { hideFlags = HideFlags.HideAndDontSave };
        ShadowCameraObj.SetActive(false);

        ShadowCamera = ShadowCameraObj.AddComponent<Camera>();
        ShadowCamera.allowMSAA = true;
        ShadowCamera.orthographic = true;
        ShadowCamera.pixelRect = new Rect(0f, 0f, 1f, 1f);
        ShadowCamera.depth = 1f;
        ShadowCamera.enabled = false;

    }

    public void BuildDescripters()
    {
        mShadowDesc = new RenderTextureDescriptor()
        {
            width = ShadowMapResolution,
            height = ShadowMapResolution,
            colorFormat = RenderTextureFormat.R8,
            dimension = TextureDimension.Tex2D,
            msaaSamples = 1,
            volumeDepth = 1
        };

        mShadowDepthDesc = new RenderTextureDescriptor()
        {
            width = ShadowMapResolution,
            height = ShadowMapResolution,
            colorFormat = RenderTextureFormat.Depth,
            dimension = TextureDimension.Tex2D,
            msaaSamples = 1,
            volumeDepth = 1
        };

        mGBufferDesc = new RenderTextureDescriptor()
        {
            width = VoxelTextureResolution,
            height = VoxelTextureResolution,
            volumeDepth = VoxelTextureResolution,
            colorFormat = RenderTextureFormat.RInt,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1
        };

        mLightingDesc = new RenderTextureDescriptor()
        {
            width = VoxelTextureResolution,
            height = VoxelTextureResolution,
            volumeDepth = VoxelTextureResolution,
            colorFormat = RenderTextureFormat.ARGBHalf,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1,
            useMipMap = true,
            mipCount = mMipLevel
        };

        mConeTraceDesc = new RenderTextureDescriptor()
        {
            height = RenderCamera.pixelHeight,
            width = RenderCamera.pixelWidth,
            volumeDepth = 1,
            colorFormat = RenderTextureFormat.ARGBHalf,
            dimension = TextureDimension.Tex2D,
            msaaSamples = 1
        };

        DummyDesc = new RenderTextureDescriptor()
        {
            colorFormat = RenderTextureFormat.R8,
            dimension = TextureDimension.Tex2D,
            memoryless = RenderTextureMemoryless.Color | RenderTextureMemoryless.Depth | RenderTextureMemoryless.MSAA,
            volumeDepth = 1,
            height = VoxelTextureResolution,
            width = VoxelTextureResolution,
            msaaSamples = 1
        };
        DummyTargetID = Shader.PropertyToID("DummyTarget");
    }

    public void BuildResources()
    {
        // build render textures.
        ShadowDummy = new RenderTexture(mShadowDesc);
        ShadowDummy.Create();

        UavAlbedo = new RenderTexture(mGBufferDesc);
        UavAlbedo.Create();

        UavNormal = new RenderTexture(mGBufferDesc);
        UavNormal.Create();

        UavEmissive = new RenderTexture(mGBufferDesc);
        UavEmissive.Create();

        UavOpacity = new RenderTexture(mGBufferDesc);
        UavOpacity.Create();

        UavLighting = new RenderTexture(mLightingDesc);
        UavLighting.Create();

        mSecondLightingRT = new RenderTexture(mLightingDesc);
        mSecondLightingRT.Create();

        mLightingPingPongRT = new RenderTexture(mLightingDesc);
        mLightingPingPongRT.Create();

//         ConeTracingRT = new RenderTexture(mConeTraceDesc);
//         ConeTracingRT.Create();

        ScreenIrradianceRT0 = RenderTexture.GetTemporary(
            RenderCamera.pixelWidth,
            RenderCamera.pixelHeight,
            0,
            RenderTextureFormat.ARGBHalf
         );

        ScreenIrradianceRT1 = RenderTexture.GetTemporary(
            RenderCamera.pixelWidth,
            RenderCamera.pixelHeight,
            0,
            RenderTextureFormat.ARGBHalf
         );

        DummyTex = new RenderTexture(DummyDesc);
        DummyTex.Create();

        mShadowDepth = new RenderTexture(mShadowDepthDesc);
        mShadowDepth.Create();
    }

    public void ReleaseResources()
    {
        // Release render textures.
        ShadowDummy.DiscardContents();
        ShadowDummy.Release();

        UavAlbedo.DiscardContents();
        UavAlbedo.Release();

        UavNormal.DiscardContents();
        UavNormal.Release();

        UavEmissive.DiscardContents();
        UavEmissive.Release();

        UavOpacity.DiscardContents();
        UavOpacity.Release();

        UavLighting.DiscardContents();
        UavLighting.Release();

        mSecondLightingRT.DiscardContents();
        mSecondLightingRT.Release();

        mLightingPingPongRT.DiscardContents();
        mLightingPingPongRT.Release();

        //         ConeTracingRT.DiscardContents();
        //         ConeTracingRT.Release();

        //         ScreenIrradianceRT0.DiscardContents();
        //         ScreenIrradianceRT0.Release();
        // 
        //         ScreenIrradianceRT1.DiscardContents();
        //         ScreenIrradianceRT1.Release();

        RenderTexture.ReleaseTemporary(ScreenIrradianceRT0);
        RenderTexture.ReleaseTemporary(ScreenIrradianceRT1);

        DummyTex.DiscardContents();
        DummyTex.Release();

        mShadowDepth.DiscardContents();
        mShadowDepth.Release();
    }

    public static Mesh GetQuadMesh()
    {
        if (mMesh != null)
            return mMesh;
            mMesh = new Mesh();

            mMesh.vertices = new Vector3[]
            {
                new Vector3(-1,-1,0.5f),
                new Vector3(-1,1,0.5f),
                new Vector3(1,1,0.5f),
                new Vector3(-1,-1,0.5f),
                new Vector3(1,1,0.5f),
                new Vector3(1,-1,0.5f)
            };

        mMesh.uv = new Vector2[]
        {
                new Vector2(0,0),
                new Vector2(0,1),
                new Vector2(1,1),
                new Vector2(0,0),
                new Vector2(1,1),
                new Vector2(1,0)
            };

        mMesh.normals = new Vector3[]
        {
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f)
        };

        // winding : clockwise.
        mMesh.SetIndices(new int[] { 0, 1, 2, 3, 4, 5 }, MeshTopology.Triangles, 0);
        return mMesh;
    }

    public void RenderScreenQuad(RenderTargetIdentifier renderTarget, Material mat, int pass)
    {
        mCommandBuffer.SetRenderTarget(renderTarget);
        mCommandBuffer.DrawMesh(GetQuadMesh(), Matrix4x4.identity, mat, 0, pass);
    }

    public void RenderScreenQuad(RenderTargetIdentifier renderTarget, int mipLevel, Material mat, int pass)
    {
        mCommandBuffer.SetRenderTarget(renderTarget, mipLevel);
        mCommandBuffer.DrawMesh(GetQuadMesh(), Matrix4x4.identity, mat, 0, pass);
    }

    public void Blit(RenderTargetIdentifier src, RenderTargetIdentifier dst)
    {
        mCommandBuffer.Blit(src, dst);
    }

    private void CopyTexture3D(RenderTexture src, RenderTexture dst, int mipLevel)
    {
        int mipRes = (int)((src.width + 0.01f) / Mathf.Pow(2f, mipLevel));
        int groupNum = Mathf.Max(1, Mathf.CeilToInt(mipRes / 8f));
        mCommandBuffer.SetComputeIntParam(mComputeShader, "CopyMipLevel", mipLevel);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdCopyTexture3D, Shader.PropertyToID("TexSrc"), src);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdCopyTexture3D, Shader.PropertyToID("TexDst"), dst, mipLevel);
        mCommandBuffer.DispatchCompute(
            mComputeShader,
            mComputeKernelIdCopyTexture3D,
            groupNum,
            groupNum,
            groupNum
            );

        mCommandBuffer.ClearRandomWriteTargets();
    }

    #endregion

    //********************************************************************************************************************************************************************
    // Update.
    //********************************************************************************************************************************************************************

    #region Update

    void UpdateParam()
    {
        UpdateCamera();
    }

    public void UpdateCamera()
    {
        if(VoxelizationCamera)
        {
            VoxelizationCamera.nearClipPlane = -VoxelizationRange;
            VoxelizationCamera.farClipPlane = VoxelizationRange;
            VoxelizationCamera.orthographicSize = 0.5f * VoxelizationRange;
            VoxelizationCamera.aspect = 1;
            
            VoxelizationCamera.transform.position = mOrigin - Vector3.right * VoxelizationCamera.orthographicSize;
            VoxelizationCamera.transform.LookAt(mOrigin, Vector3.up);
            RightViewMatrix = VoxelizationCamera.worldToCameraMatrix;

            VoxelizationCamera.transform.position = mOrigin - Vector3.up * VoxelizationCamera.orthographicSize;
            VoxelizationCamera.transform.LookAt(mOrigin, -Vector3.forward);
            UpViewMatrix = VoxelizationCamera.worldToCameraMatrix;

            VoxelizationCamera.transform.position = mOrigin - Vector3.forward * VoxelizationCamera.orthographicSize;
            VoxelizationCamera.transform.LookAt(mOrigin, Vector3.up);
            ForwordViewMatrix = VoxelizationCamera.worldToCameraMatrix;
        }

        if(ShadowCamera)
        {
            ShadowCamera.nearClipPlane = -ShadowMapRange * 10f;
            ShadowCamera.farClipPlane = ShadowMapRange * 10f;
            ShadowCamera.orthographicSize = ShadowMapRange;
            ShadowCamera.aspect = 1;

            ShadowCamera.transform.position = mOrigin - SunLight.transform.forward * ShadowCamera.orthographicSize;
            ShadowCamera.transform.LookAt(mOrigin, Vector3.up);
            ShadowViewMatrix = ShadowCamera.worldToCameraMatrix;
        }
    }

    #endregion

    //********************************************************************************************************************************************************************
    // Render.
    //********************************************************************************************************************************************************************

    #region Render

    void BeginRender()
    {
        RTSceneColor = RenderTexture.GetTemporary(
            RenderCamera.pixelWidth,
            RenderCamera.pixelHeight,
            0,
            RenderTextureFormat.ARGBHalf
            );
        RTConeTracing = RenderTexture.GetTemporary(
            RenderCamera.pixelWidth,
            RenderCamera.pixelHeight,
            0,
            RenderTextureFormat.ARGBHalf
            );
        ConeTracingRT = RenderTexture.GetTemporary(
            RenderCamera.pixelWidth,
            RenderCamera.pixelHeight,
            0,
            RenderTextureFormat.ARGBHalf
            );
        mCommandBuffer.Clear();

        mCommandBuffer.SetGlobalVector(Shader.PropertyToID("CameraPosW"), RenderCamera.transform.position);
        var renderCameraVP = RenderCamera.projectionMatrix * RenderCamera.worldToCameraMatrix;
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraView"), RenderCamera.worldToCameraMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraViewProj"), renderCameraVP);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraInvView"), RenderCamera.cameraToWorldMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraReprojectInvViewProj"), (GL.GetGPUProjectionMatrix(RenderCamera.projectionMatrix, true) * RenderCamera.worldToCameraMatrix).inverse);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraInvViewProj"), renderCameraVP.inverse);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("CameraFielfOfView"), RenderCamera.fieldOfView);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("CameraAspect"), RenderCamera.aspect);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("VoxelTextureResolution"), VoxelTextureResolution);
    }

    void EndRender()
    {
        RenderTexture.ReleaseTemporary(RTSceneColor);
        RenderTexture.ReleaseTemporary(RTConeTracing);
        RenderTexture.ReleaseTemporary(ConeTracingRT);
    }

    void RenderShodowMap()
    {
        mCommandBuffer.BeginSample("ShadowMap");

        //         mCommandBuffer.BeginSample("Shadow Mapping");
        mCommandBuffer.SetRenderTarget(ShadowDummy, mShadowDepth);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black, 0f);

        mCommandBuffer.SetGlobalMatrix("WorldToShadowVP", ShadowCamera.projectionMatrix * ShadowViewMatrix);

        var shadowGameObjects = FindObjectsOfType(typeof(GameObject)) as GameObject[];
        foreach(var obj in shadowGameObjects)
        {
            var mesh = obj.GetComponent<MeshFilter>();
            if (mesh == null)
                continue;

            var objRenderer = obj.GetComponent<Renderer>();
            if (objRenderer == null)
                continue;

            mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("ObjWorld"), obj.transform.localToWorldMatrix);

            if (objRenderer.shadowCastingMode != ShadowCastingMode.Off)
            {
                mCommandBuffer.DrawMesh(mesh.mesh, obj.transform.localToWorldMatrix, mGiMaterial, 0, (int)PassIndex.EPIVoxelShadow);
            }
        }

        // mCommandBuffer.Blit(ShadowDummy, BuiltinRenderTextureType.CameraTarget);
        mCommandBuffer.EndSample("ShadowMap");
    }

    void RenderVoxel()
    {
        mCommandBuffer.BeginSample("Voxelization");

        // cVoxelizationlear 3d gbuffer
        mCommandBuffer.SetRenderTarget(UavAlbedo, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);
        mCommandBuffer.SetRenderTarget(UavNormal, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);
        mCommandBuffer.SetRenderTarget(UavEmissive, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);
        mCommandBuffer.SetRenderTarget(UavOpacity, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);

        //mCommandBuffer
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationForwardVP"), VoxelizationCamera.projectionMatrix * ForwordViewMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationRightVP"), VoxelizationCamera.projectionMatrix * RightViewMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationUpVP"), VoxelizationCamera.projectionMatrix * UpViewMatrix);

        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelToWorld"), voxelToWorld);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("WorldToVoxel"), worldToVoxel);
        mCommandBuffer.SetRandomWriteTarget(1, UavAlbedo);
        mCommandBuffer.SetRandomWriteTarget(2, UavNormal);
        mCommandBuffer.SetRandomWriteTarget(3, UavEmissive);
        mCommandBuffer.SetRandomWriteTarget(4, UavOpacity);
        
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("HalfPixelSize"), ConsevativeRasterizeScale / VoxelTextureResolution);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("EnableConservativeRasterization"), EnableConservativeRasterization ? 1 : 0);

        mCommandBuffer.GetTemporaryRT(DummyTargetID, DummyDesc);
        mCommandBuffer.SetRenderTarget(DummyTargetID, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);

        var gameObjects = FindObjectsOfType(typeof(GameObject)) as GameObject[];
        foreach (var obj in gameObjects)
        {
            var mesh = obj.GetComponent<MeshFilter>();
            if (mesh == null)
                continue;

            var objRenderer = obj.GetComponent<Renderer>();
            if (objRenderer == null)
                continue;

            mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("ObjWorld"), obj.transform.localToWorldMatrix);
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ObjAlbedo"), objRenderer.sharedMaterial.GetTexture("_MainTex"));
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ObjEmissive"), objRenderer.sharedMaterial.GetTexture("_EmissionMap"));

            mCommandBuffer.DrawMesh(mesh.sharedMesh, obj.transform.localToWorldMatrix, mGiMaterial, 0, (int)PassIndex.EPIVoxelization);
        }

        mCommandBuffer.ClearRandomWriteTargets();

        mCommandBuffer.EndSample("Voxelization");
    }

    void ComputeDirectLighting()
    {
        mCommandBuffer.BeginSample("Direct Lighting");

        mCommandBuffer.SetRenderTarget(UavLighting, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);

        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdDirectLighting, Shader.PropertyToID("RWAlbedo"), UavAlbedo);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdDirectLighting, Shader.PropertyToID("RWNormal"), UavNormal);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdDirectLighting, Shader.PropertyToID("RWEmissive"), UavEmissive);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdDirectLighting, Shader.PropertyToID("RWOpacity"), UavOpacity);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdDirectLighting, Shader.PropertyToID("ShadowDepth"), mShadowDepth);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdDirectLighting, Shader.PropertyToID("OutRadiance"), UavLighting);

        mCommandBuffer.SetComputeMatrixParam(mComputeShader, "gVoxelToWorld", voxelToWorld);
        mCommandBuffer.SetComputeMatrixParam(mComputeShader, "gWorldToVoxel", worldToVoxel);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "gVoxelTextureResolution", VoxelTextureResolution);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "gVoxelSize", VoxelSize);

        Debug.Assert(
            (SunLight.type == LightType.Directional)
            && (SunLight != null)
            && (SunLight.isActiveAndEnabled),
            "The sun is not directional.",
            SunLight
            );
        mCommandBuffer.SetComputeVectorParam(mComputeShader, "SunLightColor", SunLight.color);
        mCommandBuffer.SetComputeVectorParam(mComputeShader, "SunLightDir", SunLight.transform.forward);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "SunLightIntensity", SunLight.intensity);

        mCommandBuffer.SetComputeFloatParam(mComputeShader, "LightIndensityMulti", LightIndensityMulti);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "EmissiveMulti", EmissiveMulti);

        mCommandBuffer.SetComputeMatrixParam(mComputeShader, "gWorldToShadowVP", ShadowCamera.projectionMatrix * ShadowViewMatrix);

        mCommandBuffer.SetComputeFloatParam(mComputeShader, "ShadowSunBias", ShadowSunBias);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "ShadowNormalBias", ShadowNormalBias);

        mCommandBuffer.DispatchCompute(
            mComputeShader,
            mComputeKernelIdDirectLighting,
            (int)(VoxelTextureResolution) / 4 + 1,
            (int)(VoxelTextureResolution) / 4 + 1,
            (int)(VoxelTextureResolution) / 4 + 1
            );

        mCommandBuffer.ClearRandomWriteTargets();

        mCommandBuffer.EndSample("Direct Lighting");

        mCommandBuffer.BeginSample("Direct Lighting Mipmaping");

        // generate direct lighting mipmap
        for (var i = 0; i < mMipLevel - 1; i++)
        {
            int currentRes = (int)((VoxelTextureResolution + 0.01f) / Mathf.Pow(2f, i + 1f));
            int groupNum = Mathf.CeilToInt(currentRes / 8f);

            mCommandBuffer.SetComputeIntParam(mComputeShader, "DstRes", currentRes);
            mCommandBuffer.SetComputeIntParam(mComputeShader, "SrcMipLevel", i);
            mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdMipmap, Shader.PropertyToID("MipmapSrc"), UavLighting);
            mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdMipmap, Shader.PropertyToID("MipmapDst"), mLightingPingPongRT, i + 1);

            mCommandBuffer.DispatchCompute(
                mComputeShader,
                mComputeKernelIdMipmap,
                groupNum,
                groupNum,
                groupNum
                );

            mCommandBuffer.ClearRandomWriteTargets();

            CopyTexture3D(mLightingPingPongRT, UavLighting, i + 1);
        }
        
        mCommandBuffer.EndSample("Direct Lighting Mipmaping");
    }

    void ComputeIndirectLighting()
    {
        mCommandBuffer.BeginSample("Indirect Lighting");

        mCommandBuffer.SetRenderTarget(mSecondLightingRT, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);

        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdIndirectLighting, Shader.PropertyToID("RWAlbedo"), UavAlbedo);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdIndirectLighting, Shader.PropertyToID("RWNormal"), UavNormal);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdIndirectLighting, Shader.PropertyToID("RWOpacity"), UavOpacity);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdIndirectLighting, Shader.PropertyToID("VoxelLighting"), UavLighting);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdIndirectLighting, Shader.PropertyToID("OutIndirectRadiance"), mSecondLightingRT);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "IndirectLightingMaxMipLevel", mMipLevel);
        mCommandBuffer.SetComputeIntParam(mComputeShader, "IndirectLightingMaxStepNum", IndirectLightingMaxStepNum);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "IndirectLightingAlphaAtten", IndirectLightingAlphaAtten);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "IndirectLightingScale", IndirectLightingScale);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "IndirectLightingFirstStep", IndirectLightingFirstStep);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "IndirectLightingStepScale", IndirectLightingStepScale);
        mCommandBuffer.SetComputeFloatParam(mComputeShader, "IndirectLightingConeAngle", IndirectLightingConeAngle);
        mCommandBuffer.SetComputeIntParam(mComputeShader, "IndirectLightingMinMipLevel", IndirectLightingMinMipLevel);

        mCommandBuffer.DispatchCompute(
                mComputeShader,
                mComputeKernelIdIndirectLighting,
                (int)(VoxelTextureResolution) / 8 + 1,
                (int)(VoxelTextureResolution) / 8 + 1,
                (int)(VoxelTextureResolution) / 8 + 1
                );

        mCommandBuffer.EndSample("Indirect Lighting");

        mCommandBuffer.BeginSample("Indirect Lighting Mipmaping");

        // generate indirect lighting mipmap
        for (var i = 0; i < mMipLevel - 1; i++)
        {
            int currentRes = (int)((VoxelTextureResolution + 0.01f) / Mathf.Pow(2f, i + 1f));
            int groupNum = Mathf.CeilToInt(currentRes / 8f);

            mCommandBuffer.SetComputeIntParam(mComputeShader, "DstRes", currentRes);
            mCommandBuffer.SetComputeIntParam(mComputeShader, "SrcMipLevel", i);
            mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdMipmap, Shader.PropertyToID("MipmapSrc"), mSecondLightingRT);
            mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdMipmap, Shader.PropertyToID("MipmapDst"), mLightingPingPongRT, i + 1);
            
            //mCommandBuffer.SetComputeIntParam(mComputeShader, "MipmapDimension", j);
            mCommandBuffer.DispatchCompute(
            mComputeShader,
            mComputeKernelIdMipmap,
            groupNum,
            groupNum,
            groupNum
            );

            mCommandBuffer.ClearRandomWriteTargets();

            CopyTexture3D(mLightingPingPongRT, mSecondLightingRT, i + 1);
        }

        mCommandBuffer.EndSample("Indirect Lighting Mipmaping");
    }

    void ScreenConeTracing()
    {
        mCommandBuffer.BeginSample("Cone Tracing");

        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenMaxMipLevel"), mMipLevel);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("ScreenMaxStepNum"), ScreenMaxStepNum);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenAlphaAtten"), ScreenAlphaAtten);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenScale"), ScreenScale);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenConeAngle"), ScreenConeAngle);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenFirstStep"), ScreenFirstStep);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenStepScale"), ScreenStepScale);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenNormal"), BuiltinRenderTextureType.GBuffer2);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenAlbedo"), BuiltinRenderTextureType.GBuffer0);
        if(EnableSecondBounce)
        {
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenConeTraceLighting"), mSecondLightingRT);
        }
        else
        {
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenConeTraceLighting"), UavLighting);
        }
        
        //Vector3 coneDir = GenDirection();
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("EnableTemporalFilter"), EnableTemporalFilter ? 1 : 0);
        mCommandBuffer.SetGlobalVector(Shader.PropertyToID("ScreenResolution"), mScreenResolution);
        mCommandBuffer.SetGlobalVector(Shader.PropertyToID("BlueNoiseResolution"), mBlueNoiseResolution);
        mCommandBuffer.SetGlobalVector(Shader.PropertyToID("BlueNoiseScale"), new Vector4(BlueNoiseScale.x, BlueNoiseScale.y, 1f / BlueNoiseScale.x, 1f / BlueNoiseScale.y));
        //mCommandBuffer.SetGlobalVector(Shader.PropertyToID("ConeTraceDirection"), coneDir);
        mCommandBuffer.SetGlobalVector(Shader.PropertyToID("RandomUV"), GenerateRandomOffset()); // GenRandomUV
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("NoiseLUT"), BlueNoise_LUT);
        mCommandBuffer.SetRenderTarget(ConeTracingRT);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);
        RenderScreenQuad(ConeTracingRT, mGiMaterial, (int)PassIndex.EPIConeTracing);

        mCommandBuffer.EndSample("Cone Tracing");
    }

    void TemporalFilter()
    {
        mCommandBuffer.BeginSample("Temporal Filtering");

        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("BlendAlpha"), TemporalBlendAlpha);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("TemporalClampAABBScale"), ClampAABBScale);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("CurrentScreenIrradiance"), ConeTracingRT);
        
        if (mPingPongFlag)
        {
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("HistoricalScreenIrradiance"), ScreenIrradianceRT0);
            mCommandBuffer.SetRenderTarget(ScreenIrradianceRT1);
//             mCommandBuffer.ClearRenderTarget(true, true, Color.black);
            RenderScreenQuad(ScreenIrradianceRT1, mGiMaterial, (int)PassIndex.EPITemporalFilter);
        }
        else
        {
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("HistoricalScreenIrradiance"), ScreenIrradianceRT1);
            mCommandBuffer.SetRenderTarget(ScreenIrradianceRT0);
//             mCommandBuffer.ClearRenderTarget(true, true, Color.black);
            RenderScreenQuad(ScreenIrradianceRT0, mGiMaterial, (int)PassIndex.EPITemporalFilter);
        }
        
        mCommandBuffer.EndSample("Temporal Filtering");
    }

    void Combine()
    {
        mCommandBuffer.BeginSample("Combine");

        if (EnableTemporalFilter)
        {
            if (mPingPongFlag)
            {
                mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VXGIIndirect"), ScreenIrradianceRT1);
            }
            else
            {
                mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VXGIIndirect"), ScreenIrradianceRT0);
            }
            mPingPongFlag = !mPingPongFlag;
        }
        else
        {
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VXGIIndirect"), ConeTracingRT);
        }

        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("EnableTemporalFilter"), EnableTemporalFilter ? 1 : 0);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("TemporalFrameCount"), 4);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("SceneDirect"), BuiltinRenderTextureType.CameraTarget);
        mCommandBuffer.SetRenderTarget(RTConeTracing);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);
        RenderScreenQuad(RTConeTracing, mGiMaterial, (int)PassIndex.EPICombine);
        Blit(RTConeTracing, BuiltinRenderTextureType.CameraTarget);

        mCommandBuffer.EndSample("Combine");
    }

    void RenderDebug()
    {
        // Voxelization Debug Pass
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexAlbedo"), UavAlbedo);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexNormal"), UavNormal);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexEmissive"), UavEmissive);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexOpacity"), UavOpacity);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexLighting"), UavLighting);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexIndirectLighting"), mSecondLightingRT);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenConeTraceIrradiance"), ConeTracingRT);
        if (mPingPongFlag)
        {
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenBlendIrradiance"), ScreenIrradianceRT0);
        }
        else
        {
            mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenBlendIrradiance"), ScreenIrradianceRT1);
        }

        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("EmissiveMulti"), EmissiveMulti);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("VoxelSize"), VoxelSize);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("RayStepSize"), RayStepSize);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("VisualizeDebugType"), (int)DebugType);

        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("DirectLightingDebugMipLevel"), Mathf.Clamp(DirectLightingDebugMipLevel, 0, mMipLevel - 1));
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("IndirectLightingDebugMipLevel"), Mathf.Clamp(IndirectLightingDebugMipLevel, 0, mMipLevel - 1));

        mCommandBuffer.SetRenderTarget(RTSceneColor);
        RenderScreenQuad(RTSceneColor, mGiMaterial, (int)PassIndex.EPIVoxelizationDebug);
        Blit(RTSceneColor, BuiltinRenderTextureType.CameraTarget);
    }

    #endregion

}

