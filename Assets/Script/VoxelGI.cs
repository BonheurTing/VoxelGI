using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.Rendering;

public enum VoxelGIDebugType
{
    Albedo = 0,
    Normal,
    Emissive,
    Lighting,
    IndirectLighting,
    ConeTrace,
    TemporalFilter,
    BilateralFilter
}

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
public class VoxelGI : MonoBehaviour
{

    //********************************************************************************************************************************************************************
    // Serialization.
    //********************************************************************************************************************************************************************

    #region Serialization

    [System.Serializable]
    public class VoxelizationConfigDropdown
    {
        public int ShadowMapResolution = 1024;
        public float ShadowMapRange = 50f;
        public int VoxelTextureResolution = 256;
        public float VoxelSize = 0.1f;
        public int StableMipLevel = 6;
        public bool EnableConservativeRasterization = false;
        [Range(0.0f, 3.0f)]
        public float ConsevativeRasterizeScale = 1.5f;
    };

    [System.Serializable]
    public class DirectLightingConfigDropdown
    {
        public Light SunLight;
        [Range(0.0f, 10.0f)]
        public float LightIndensityMulti = 1.5f;
        [Range(0.0f, 10.0f)]
        public float EmissiveMulti = 1.5f;
        public float ShadowSunBias = 0.25f;
        public float ShadowNormalBias = 1f;
    };

    [System.Serializable]
    public class IndirectLightingConfigDropdown
    {
        public bool EnableSecondBounce = true;
        [Range(1, 32)]
        public int IndirectLightingMaxStepNum = 12;
        [Range(1.0f, 10.0f)]
        public float IndirectLightingAlphaAtten = 2f;
        [Range(0.0f, 10.0f)]
        public float IndirectLightingScale = 2f;
        [Range(0.5f, 3f)]
        public float IndirectLightingFirstStep = 1f;
        [Range(1f, 3f)]
        public float IndirectLightingStepScale = 1f;
        [Range(20f, 150f)]
        public float IndirectLightingConeAngle = 120f;
        [Range(0, 5)]
        public int IndirectLightingMinMipLevel = 0;
    };

    [System.Serializable]
    public class ConeTracingConfigDropdown
    {
        [SerializeField]
        public Texture2D BlueNoiseLUT = null;
        [Range(1, 32)]
        public int ScreenMaxStepNum = 32;
        [Range(1.0f, 10.0f)]
        public float ScreenAlphaAtten = 5f;
        [Range(0.0f, 10.0f)]
        public float ScreenScale = 1f;
        [Range(0.5f, 3f)]
        public float ScreenFirstStep = 0.9f;
        [Range(1f, 3f)]
        public float ScreenStepScale = 1.2f;
        [Range(20f, 150f)]
        public float ScreenConeAngle = 120f;
    };

    [System.Serializable]
    public class TemporalFilterConfigDropdown
    {
        public bool EnableTemporalFilter = true;
        [Range(0f, 1f)]
        public float TemporalBlendAlpha = 0.005f;
        public float ClampAABBScale = 1.2f;
        public Vector2 BlueNoiseScale = new Vector2(1, 1);
        public int HaltonValueCount = 8;
    };

    [System.Serializable]
    public class BilateralFilterConfigDropdown
    {
        public bool EnableBilateralFilter = true;
        [Range(0f, 10f)]
        public float BilateralSamplerRadius = 6.0f;
        public float DepthThresholdLowerBound = 0.1f;
        public float DepthThresholdUpperBound = 0.2f;
        [Range(0f, 1f)]
        public float NormalThresholdLowerBound = 0.7f;
        [Range(0f, 1f)]
        public float NormalThresholdUpperBound = 1f;
    };

    [System.Serializable]
    public class DebugConfigDropdown
    {
        public bool DebugMode = false;
        public VoxelGIDebugType DebugType;
        [Range(0, 10)]
        public int DirectLightingDebugMipLevel = 0;
        [Range(0, 10)]
        public int IndirectLightingDebugMipLevel = 0;
        [Range(0.01f, 0.5f)]
        public float RayStepSize = 0.03f;
    };

    [SerializeField]
    public VoxelizationConfigDropdown VoxelizationConfig;

    [SerializeField]
    public DirectLightingConfigDropdown DirectLightingConfig;

    [SerializeField]
    public IndirectLightingConfigDropdown IndirectLightingConfig;
    
    [SerializeField]
    public ConeTracingConfigDropdown ConeTracingConfig;

    [SerializeField]
    public TemporalFilterConfigDropdown TemporalFilterConfig;

    [SerializeField]
    public BilateralFilterConfigDropdown BilateralFilterConfig;

    [SerializeField]
    public DebugConfigDropdown DebugConfig;

    #endregion

    //********************************************************************************************************************************************************************
    // Private.
    //********************************************************************************************************************************************************************

    #region Private

    private enum PassIndex
    {
        Voxelization = 0,
        VoxelShadow = 1,
        ConeTracing = 2,
        TemporalFilter = 3,
        Combine = 4,
        VoxelizationDebug
    }

    ComputeShader VXGIComputeShader;
    private int ComputeKernelIdDirectLighting;
    private int ComputeKernelIdMipmap;
    private int ComputeKernelIdCopyTexture3D;
    private int ComputeKernelIdIndirectLighting;
    private int ComputeKernelIdBilateralFiltering;

    private Camera RenderCamera;
    private UnityEngine.Rendering.CommandBuffer CommandBuffer = null;
    private Material GiMaterial;

    // Voxelization
    private Camera VoxelizationCamera;
    private Matrix4x4 ForwordViewMatrix;
    private Matrix4x4 RightViewMatrix;
    private Matrix4x4 UpViewMatrix;

    public float VoxelizationRange
    {
        get
        {
            return VoxelizationConfig.VoxelSize * VoxelizationConfig.VoxelTextureResolution;
        }
    }

    private Vector3 OriginPos
    {
        get
        {
            Vector3 fixedCameraPos = RenderCamera.transform.position / (VoxelizationConfig.VoxelSize * Mathf.Pow(2, VoxelizationConfig.StableMipLevel));
            Vector3Int intPosition = new Vector3Int((int)fixedCameraPos.x, (int)fixedCameraPos.y, (int)fixedCameraPos.z);
            fixedCameraPos.x = intPosition.x  * (VoxelizationConfig.VoxelSize * Mathf.Pow(2, VoxelizationConfig.StableMipLevel));
            fixedCameraPos.y = intPosition.y  * (VoxelizationConfig.VoxelSize * Mathf.Pow(2, VoxelizationConfig.StableMipLevel));
            fixedCameraPos.z = intPosition.z  * (VoxelizationConfig.VoxelSize * Mathf.Pow(2, VoxelizationConfig.StableMipLevel));
            return fixedCameraPos;
        }
    }

    private int MipLevel
    {
        get
        {
            return (int)Mathf.Log(VoxelizationConfig.VoxelTextureResolution, 2) + 1;
        }
    }

    private static Mesh LocalMesh;
    private RenderTexture RTSceneColor;
    private RenderTexture RTConeTracing;
    private int DummyTargetID;
    private RenderTexture DummyTex;
    private RenderTextureDescriptor DummyDesc;
    
    private RenderTexture UavAlbedo;
    private RenderTexture UavNormal;
    private RenderTexture UavEmissive;
    private RenderTexture UavOpacity;
    private RenderTextureDescriptor GBufferDesc3D;

    // Lighting
    private RenderTexture UavLighting;
    private RenderTextureDescriptor LightingDesc;
    RenderTexture SecondLightingRT;
    RenderTexture LightingPingPongRT;

    private Camera ShadowCamera;
    private RenderTextureDescriptor ShadowCameraDesc;

    private RenderTexture ShadowDummy;
    private RenderTextureDescriptor ShadowDesc;
    private RenderTexture ShadowDepth;
    private RenderTextureDescriptor ShadowDepthDesc;
    private Matrix4x4 ShadowViewMatrix;

    // Cone Trace
    private int RandomOffsetIndex = 0;
    private int ConeTraceCount = 0;
    private double[,] Hemisphere8 = new double[,]{
        { -0.735927295315164, -0.674169686362497, 0.0625 },
        { 0.0858751947674414, 0.978503551819642, 0.1875 },
        { 0.577966879673501, -0.75385544768243, 0.3125 },
        { -0.885472495182538, 0.156627616578975, 0.4375 },
        { 0.697614586708622, 0.443765296537886, 0.5625 },
        { -0.188520590528854, -0.701287200044783, 0.6875 },
        { -0.26869090798331, 0.517347993102423, 0.8125 },
        { 0.326869977432545, -0.119372391503427, 0.9375 }
    };
    private RenderTexture ConeTracingRT;
    private RenderTextureDescriptor ConeTraceDesc;

    // Temporal Filtering
    private Matrix4x4 PreLocalToWorld;
    private System.Random DirRand;
    private bool PingPongFlag = false;
    private RenderTexture ScreenIrradianceRT0;
    private RenderTexture ScreenIrradianceRT1;
    private Vector4 ScreenResolution
    {
        get
        {
            Resolution resolution = Screen.currentResolution;
            
            return new Vector4(RenderCamera.pixelWidth, RenderCamera.pixelHeight, 1.0f / RenderCamera.pixelWidth, 1.0f / RenderCamera.pixelHeight);
        }
    }
    private Vector4 BlueNoiseResolution;
    private bool NeedToClearHistory = true;

    // Bilateral Filtering
    private RenderTexture UavBilateralFilter;
    private RenderTextureDescriptor BilateralDesc;

    #endregion

    //********************************************************************************************************************************************************************
    // MonoBehaviour.
    //********************************************************************************************************************************************************************

    #region MonoBehaviour

    void Awake()
    {
        // Setup.
        RenderCamera = gameObject.GetComponent<Camera>();
        RenderCamera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors;
        GiMaterial = new Material(Shader.Find("Hidden/VoxelGI"));
        DirRand = new System.Random();
        BlueNoiseResolution = new Vector4(ConeTracingConfig.BlueNoiseLUT.width,
            ConeTracingConfig.BlueNoiseLUT.height,
            1.0f / ConeTracingConfig.BlueNoiseLUT.width,
            1.0f / ConeTracingConfig.BlueNoiseLUT.height);

        BuildCamera();
        BuildDescripters();

        if (CommandBuffer == null)
        {
            CommandBuffer = new CommandBuffer();
            CommandBuffer.name = "VXGI_CommandBuffer";
        }

        if (VXGIComputeShader == null)
        {
            VXGIComputeShader = (ComputeShader)Resources.Load("VoxelGICompute");
            ComputeKernelIdDirectLighting = VXGIComputeShader.FindKernel("VoxelDirectLighting");
            ComputeKernelIdMipmap = VXGIComputeShader.FindKernel("MipmapGeneration");
            ComputeKernelIdCopyTexture3D = VXGIComputeShader.FindKernel("CopyTexture3D");
            ComputeKernelIdIndirectLighting = VXGIComputeShader.FindKernel("VoxelIndirectLighting");
            ComputeKernelIdBilateralFiltering = VXGIComputeShader.FindKernel("BilateralFiltering");
        }

        UpdateCamera();
    }

    void OnEnable()
    {
        if (CommandBuffer != null)
        {
            RenderCamera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, CommandBuffer);
        }

        BuildResources();

        NeedToClearHistory = true;
    }

    void OnPreRender()
    {
        UpdateCamera();

        if (CommandBuffer != null)
        {
            BeginRender();

            RenderShodowMap();

            RenderVoxel();

            ComputeDirectLighting();

            if(IndirectLightingConfig.EnableSecondBounce)
            {
                ComputeIndirectLighting();
            }

            ScreenConeTracing();

            if (TemporalFilterConfig.EnableTemporalFilter)
            {
                TemporalFilter();
            }

            if (BilateralFilterConfig.EnableBilateralFilter)
            {
                ComputeBilateralFiltering();
            }

            Combine();

            if (DebugConfig.DebugMode)
            {
                RenderDebug();
            }

            EndRender();
        }
    }

    void OnDisable()
    {
        if (CommandBuffer != null)
        {
            RenderCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, CommandBuffer);
        }

        ReleaseResources();
    }

    void OnDestroy()
    {
        if (CommandBuffer != null)
        {
            CommandBuffer.Dispose();
        }

        ReleaseResources();
    }

    void Update()
    {
    }

    #endregion

    //********************************************************************************************************************************************************************
    // Utils.
    //********************************************************************************************************************************************************************

    #region Utils

    public Vector3 GenDirection()
    {
        double theta = DirRand.NextDouble() * 1.57;
        double fi = DirRand.NextDouble() * 6.2832;
        double x = System.Math.Sin(theta) * System.Math.Cos(fi);
        double y = System.Math.Sin(theta) * System.Math.Sin(fi);
        double z = System.Math.Cos(theta);
        Vector3 randVec = new Vector3((float)x, (float)y, (float)z);
        int id = ConeTraceCount % 8;
        ConeTraceCount = (ConeTraceCount + 1) % 8;
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
        int  count = TemporalFilterConfig.HaltonValueCount -1;
        Vector2 offset = new Vector2(GetHaltonValue(RandomOffsetIndex, 2), GetHaltonValue(RandomOffsetIndex, 3));
        RandomOffsetIndex++;
        RandomOffsetIndex = RandomOffsetIndex >= count ? 0 : RandomOffsetIndex;
        return offset;
    }

    public Vector2 GenRandomUV()
    {
        return new Vector2((float)DirRand.NextDouble(), (float)DirRand.NextDouble());
    }

    public Matrix4x4 voxelToWorld
    {
        get
        {
            var origin = OriginPos - new Vector3(VoxelizationRange, VoxelizationRange, VoxelizationRange) * 0.5f;
            return Matrix4x4.TRS(origin, Quaternion.identity, Vector3.one * VoxelizationConfig.VoxelSize);
        }
    }

    public Matrix4x4 worldToVoxel
    {
        get { return voxelToWorld.inverse; }
    }

    void ClearHistoryRT()
    {
        CommandBuffer.SetRenderTarget(ScreenIrradianceRT0);
        CommandBuffer.ClearRenderTarget(false, true, Color.black);
        CommandBuffer.SetRenderTarget(ScreenIrradianceRT1);
        CommandBuffer.ClearRenderTarget(false, true, Color.black);
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
        ShadowDesc = new RenderTextureDescriptor()
        {
            width = VoxelizationConfig.ShadowMapResolution,
            height = VoxelizationConfig.ShadowMapResolution,
            colorFormat = RenderTextureFormat.R8,
            dimension = TextureDimension.Tex2D,
            msaaSamples = 1,
            volumeDepth = 1
        };

        ShadowDepthDesc = new RenderTextureDescriptor()
        {
            width = VoxelizationConfig.ShadowMapResolution,
            height = VoxelizationConfig.ShadowMapResolution,
            colorFormat = RenderTextureFormat.Depth,
            dimension = TextureDimension.Tex2D,
            msaaSamples = 1,
            volumeDepth = 1
        };

        GBufferDesc3D = new RenderTextureDescriptor()
        {
            width = VoxelizationConfig.VoxelTextureResolution,
            height = VoxelizationConfig.VoxelTextureResolution,
            volumeDepth = VoxelizationConfig.VoxelTextureResolution,
            colorFormat = RenderTextureFormat.RInt,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1
        };

        BilateralDesc = new RenderTextureDescriptor()
        {
            width = RenderCamera.pixelWidth,
            height = RenderCamera.pixelHeight,
            volumeDepth = VoxelizationConfig.VoxelTextureResolution,
            colorFormat = RenderTextureFormat.ARGBHalf,
            dimension = TextureDimension.Tex2D,
            enableRandomWrite = true,
            msaaSamples = 1
        };

        LightingDesc = new RenderTextureDescriptor()
        {
            width = VoxelizationConfig.VoxelTextureResolution,
            height = VoxelizationConfig.VoxelTextureResolution,
            volumeDepth = VoxelizationConfig.VoxelTextureResolution,
            colorFormat = RenderTextureFormat.ARGBHalf,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1,
            useMipMap = true,
            mipCount = MipLevel
        };

        ConeTraceDesc = new RenderTextureDescriptor()
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
            height = VoxelizationConfig.VoxelTextureResolution,
            width = VoxelizationConfig.VoxelTextureResolution,
            msaaSamples = 1
        };
        DummyTargetID = Shader.PropertyToID("DummyTarget");
    }

    public void BuildResources()
    {
        // build render textures.
        ShadowDummy = new RenderTexture(ShadowDesc);
        ShadowDummy.Create();

        UavAlbedo = new RenderTexture(GBufferDesc3D);
        UavAlbedo.Create();

        UavNormal = new RenderTexture(GBufferDesc3D);
        UavNormal.Create();

        UavEmissive = new RenderTexture(GBufferDesc3D);
        UavEmissive.Create();

        UavOpacity = new RenderTexture(GBufferDesc3D);
        UavOpacity.Create();

        UavBilateralFilter = new RenderTexture(BilateralDesc);
        UavBilateralFilter.Create();

        UavLighting = new RenderTexture(LightingDesc);
        UavLighting.Create();

        SecondLightingRT = new RenderTexture(LightingDesc);
        SecondLightingRT.Create();

        LightingPingPongRT = new RenderTexture(LightingDesc);
        LightingPingPongRT.Create();

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

        DummyTex = new RenderTexture(DummyDesc);
        DummyTex.Create();

        ShadowDepth = new RenderTexture(ShadowDepthDesc);
        ShadowDepth.Create();
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

        UavBilateralFilter.DiscardContents();
        UavBilateralFilter.Release();

        UavLighting.DiscardContents();
        UavLighting.Release();

        SecondLightingRT.DiscardContents();
        SecondLightingRT.Release();

        LightingPingPongRT.DiscardContents();
        LightingPingPongRT.Release();

        RenderTexture.ReleaseTemporary(ScreenIrradianceRT0);
        RenderTexture.ReleaseTemporary(ScreenIrradianceRT1);
        RenderTexture.ReleaseTemporary(RTSceneColor);
        RenderTexture.ReleaseTemporary(RTConeTracing);
        RenderTexture.ReleaseTemporary(ConeTracingRT);

        DummyTex.DiscardContents();
        DummyTex.Release();

        ShadowDepth.DiscardContents();
        ShadowDepth.Release();
    }

    public static Mesh GetQuadMesh()
    {
        if (LocalMesh != null)
            return LocalMesh;
            LocalMesh = new Mesh();

            LocalMesh.vertices = new Vector3[]
            {
                new Vector3(-1,-1,0.5f),
                new Vector3(-1,1,0.5f),
                new Vector3(1,1,0.5f),
                new Vector3(-1,-1,0.5f),
                new Vector3(1,1,0.5f),
                new Vector3(1,-1,0.5f)
            };

        LocalMesh.uv = new Vector2[]
        {
                new Vector2(0,0),
                new Vector2(0,1),
                new Vector2(1,1),
                new Vector2(0,0),
                new Vector2(1,1),
                new Vector2(1,0)
            };

        LocalMesh.normals = new Vector3[]
        {
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f),
                new Vector3(0f,0f,-1f)
        };

        // winding : clockwise.
        LocalMesh.SetIndices(new int[] { 0, 1, 2, 3, 4, 5 }, MeshTopology.Triangles, 0);
        return LocalMesh;
    }

    public void RenderScreenQuad(RenderTargetIdentifier renderTarget, Material mat, int pass)
    {
        CommandBuffer.SetRenderTarget(renderTarget);
        CommandBuffer.DrawMesh(GetQuadMesh(), Matrix4x4.identity, mat, 0, pass);
    }

    public void RenderScreenQuad(RenderTargetIdentifier renderTarget, int mipLevel, Material mat, int pass)
    {
        CommandBuffer.SetRenderTarget(renderTarget, mipLevel);
        CommandBuffer.DrawMesh(GetQuadMesh(), Matrix4x4.identity, mat, 0, pass);
    }

    public void Blit(RenderTargetIdentifier src, RenderTargetIdentifier dst)
    {
        CommandBuffer.Blit(src, dst);
    }

    private void CopyTexture3D(RenderTexture src, RenderTexture dst, int mipLevel)
    {
        int mipRes = (int)((src.width + 0.01f) / Mathf.Pow(2f, mipLevel));
        int groupNum = Mathf.Max(1, Mathf.CeilToInt(mipRes / 8f));
        CommandBuffer.SetComputeIntParam(VXGIComputeShader, "CopyMipLevel", mipLevel);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdCopyTexture3D, Shader.PropertyToID("TexSrc"), src);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdCopyTexture3D, Shader.PropertyToID("TexDst"), dst, mipLevel);
        CommandBuffer.DispatchCompute(
            VXGIComputeShader,
            ComputeKernelIdCopyTexture3D,
            groupNum,
            groupNum,
            groupNum
            );

        CommandBuffer.ClearRandomWriteTargets();
    }

    #endregion

    //********************************************************************************************************************************************************************
    // Update.
    //********************************************************************************************************************************************************************

    #region Update

    public void UpdateCamera()
    {
        if(VoxelizationCamera)
        {
            VoxelizationCamera.nearClipPlane = -VoxelizationRange;
            VoxelizationCamera.farClipPlane = VoxelizationRange;
            VoxelizationCamera.orthographicSize = 0.5f * VoxelizationRange;
            VoxelizationCamera.aspect = 1;
            
            VoxelizationCamera.transform.position = OriginPos - Vector3.right * VoxelizationCamera.orthographicSize;
            VoxelizationCamera.transform.LookAt(OriginPos, Vector3.up);
            RightViewMatrix = VoxelizationCamera.worldToCameraMatrix;

            VoxelizationCamera.transform.position = OriginPos - Vector3.up * VoxelizationCamera.orthographicSize;
            VoxelizationCamera.transform.LookAt(OriginPos, -Vector3.forward);
            UpViewMatrix = VoxelizationCamera.worldToCameraMatrix;

            VoxelizationCamera.transform.position = OriginPos - Vector3.forward * VoxelizationCamera.orthographicSize;
            VoxelizationCamera.transform.LookAt(OriginPos, Vector3.up);
            ForwordViewMatrix = VoxelizationCamera.worldToCameraMatrix;
        }

        if(ShadowCamera)
        {
            ShadowCamera.nearClipPlane = -VoxelizationConfig.ShadowMapRange * 10f;
            ShadowCamera.farClipPlane = VoxelizationConfig.ShadowMapRange * 10f;
            ShadowCamera.orthographicSize = VoxelizationConfig.ShadowMapRange;
            ShadowCamera.aspect = 1;

            ShadowCamera.transform.position = OriginPos - DirectLightingConfig.SunLight.transform.forward * ShadowCamera.orthographicSize;
            ShadowCamera.transform.LookAt(OriginPos, Vector3.up);
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
        CommandBuffer.Clear();

        CommandBuffer.SetGlobalVector(Shader.PropertyToID("CameraPosW"), RenderCamera.transform.position);
        var renderCameraVP = RenderCamera.projectionMatrix * RenderCamera.worldToCameraMatrix;
        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraView"), RenderCamera.worldToCameraMatrix);
        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraViewProj"), renderCameraVP);
        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraInvView"), RenderCamera.cameraToWorldMatrix);
        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraReprojectInvViewProj"), (GL.GetGPUProjectionMatrix(RenderCamera.projectionMatrix, true) * RenderCamera.worldToCameraMatrix).inverse);
        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraInvViewProj"), renderCameraVP.inverse);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("CameraFielfOfView"), RenderCamera.fieldOfView);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("CameraAspect"), RenderCamera.aspect);
        CommandBuffer.SetGlobalInt(Shader.PropertyToID("VoxelTextureResolution"), VoxelizationConfig.VoxelTextureResolution);

        if (NeedToClearHistory)
        {
            ClearHistoryRT();
            NeedToClearHistory = false;
        }
    }

    void RenderShodowMap()
    {
        CommandBuffer.BeginSample("ShadowMap");
        
        CommandBuffer.SetRenderTarget(ShadowDummy, ShadowDepth);
        CommandBuffer.ClearRenderTarget(true, true, Color.black, 0f);

        CommandBuffer.SetGlobalMatrix("WorldToShadowVP", ShadowCamera.projectionMatrix * ShadowViewMatrix);

        var shadowGameObjects = FindObjectsOfType(typeof(GameObject)) as GameObject[];
        foreach(var obj in shadowGameObjects)
        {
            var mesh = obj.GetComponent<MeshFilter>();
            if (mesh == null)
                continue;

            var objRenderer = obj.GetComponent<Renderer>();
            if (objRenderer == null)
                continue;

            CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("ObjWorld"), obj.transform.localToWorldMatrix);

            if (objRenderer.shadowCastingMode != ShadowCastingMode.Off)
            {
                CommandBuffer.DrawMesh(mesh.sharedMesh, obj.transform.localToWorldMatrix, GiMaterial, 0, (int)PassIndex.VoxelShadow);
            }
        }
        
        CommandBuffer.EndSample("ShadowMap");
    }

    void RenderVoxel()
    {
        CommandBuffer.BeginSample("Voxelization");

        // clear 3d gbuffer
        CommandBuffer.SetRenderTarget(UavAlbedo, 0, CubemapFace.Unknown, -1);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);
        CommandBuffer.SetRenderTarget(UavNormal, 0, CubemapFace.Unknown, -1);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);
        CommandBuffer.SetRenderTarget(UavEmissive, 0, CubemapFace.Unknown, -1);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);
        CommandBuffer.SetRenderTarget(UavOpacity, 0, CubemapFace.Unknown, -1);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);

        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationForwardVP"), VoxelizationCamera.projectionMatrix * ForwordViewMatrix);
        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationRightVP"), VoxelizationCamera.projectionMatrix * RightViewMatrix);
        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationUpVP"), VoxelizationCamera.projectionMatrix * UpViewMatrix);

        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelToWorld"), voxelToWorld);
        CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("WorldToVoxel"), worldToVoxel);
        CommandBuffer.SetRandomWriteTarget(1, UavAlbedo);
        CommandBuffer.SetRandomWriteTarget(2, UavNormal);
        CommandBuffer.SetRandomWriteTarget(3, UavEmissive);
        CommandBuffer.SetRandomWriteTarget(4, UavOpacity);
        
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("HalfPixelSize"), VoxelizationConfig.ConsevativeRasterizeScale / VoxelizationConfig.VoxelTextureResolution);
        CommandBuffer.SetGlobalInt(Shader.PropertyToID("EnableConservativeRasterization"), VoxelizationConfig.EnableConservativeRasterization ? 1 : 0);

        CommandBuffer.GetTemporaryRT(DummyTargetID, DummyDesc);
        CommandBuffer.SetRenderTarget(DummyTargetID, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);

        var gameObjects = FindObjectsOfType(typeof(GameObject)) as GameObject[];
        foreach (var obj in gameObjects)
        {
            var mesh = obj.GetComponent<MeshFilter>();
            if (mesh == null)
                continue;

            var objRenderer = obj.GetComponent<Renderer>();
            if (objRenderer == null)
                continue;

            if (objRenderer.sharedMaterial.name.Substring(0, 7) == "Blocker")
            {
                continue;
            }

                CommandBuffer.SetGlobalMatrix(Shader.PropertyToID("ObjWorld"), obj.transform.localToWorldMatrix);
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ObjAlbedo"), objRenderer.sharedMaterial.GetTexture("_MainTex"));
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ObjEmissive"), objRenderer.sharedMaterial.GetTexture("_EmissionMap"));

            CommandBuffer.DrawMesh(mesh.sharedMesh, obj.transform.localToWorldMatrix, GiMaterial, 0, (int)PassIndex.Voxelization);
        }

        CommandBuffer.ClearRandomWriteTargets();

        CommandBuffer.EndSample("Voxelization");
    }

    void ComputeDirectLighting()
    {
        CommandBuffer.BeginSample("Direct Lighting");

        CommandBuffer.SetRenderTarget(UavLighting, 0, CubemapFace.Unknown, -1);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);

        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdDirectLighting, Shader.PropertyToID("RWAlbedo"), UavAlbedo);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdDirectLighting, Shader.PropertyToID("RWNormal"), UavNormal);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdDirectLighting, Shader.PropertyToID("RWEmissive"), UavEmissive);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdDirectLighting, Shader.PropertyToID("RWOpacity"), UavOpacity);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdDirectLighting, Shader.PropertyToID("ShadowDepth"), ShadowDepth);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdDirectLighting, Shader.PropertyToID("OutRadiance"), UavLighting);

        CommandBuffer.SetComputeMatrixParam(VXGIComputeShader, "VoxelToWorld", voxelToWorld);
        CommandBuffer.SetComputeMatrixParam(VXGIComputeShader, "WorldToVoxel", worldToVoxel);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "VoxelTextureResolution", VoxelizationConfig.VoxelTextureResolution);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "VoxelSize", VoxelizationConfig.VoxelSize);

        Debug.Assert(
            (DirectLightingConfig.SunLight.type == LightType.Directional)
            && (DirectLightingConfig.SunLight != null)
            && (DirectLightingConfig.SunLight.isActiveAndEnabled),
            "The sun is not directional.",
            DirectLightingConfig.SunLight
            );
        CommandBuffer.SetComputeVectorParam(VXGIComputeShader, "SunLightColor", DirectLightingConfig.SunLight.color);
        CommandBuffer.SetComputeVectorParam(VXGIComputeShader, "SunLightDir", DirectLightingConfig.SunLight.transform.forward);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "SunLightIntensity", DirectLightingConfig.SunLight.intensity);

        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "LightIndensityMulti", DirectLightingConfig.LightIndensityMulti);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "EmissiveMulti", DirectLightingConfig.EmissiveMulti);

        CommandBuffer.SetComputeMatrixParam(VXGIComputeShader, "gWorldToShadowVP", ShadowCamera.projectionMatrix * ShadowViewMatrix);

        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "ShadowSunBias", DirectLightingConfig.ShadowSunBias);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "ShadowNormalBias", DirectLightingConfig.ShadowNormalBias);

        CommandBuffer.DispatchCompute(
            VXGIComputeShader,
            ComputeKernelIdDirectLighting,
            (int)(VoxelizationConfig.VoxelTextureResolution) / 4 + 1,
            (int)(VoxelizationConfig.VoxelTextureResolution) / 4 + 1,
            (int)(VoxelizationConfig.VoxelTextureResolution) / 4 + 1
            );

        CommandBuffer.ClearRandomWriteTargets();

        CommandBuffer.EndSample("Direct Lighting");

        CommandBuffer.BeginSample("Direct Lighting Mipmaping");

        // generate direct lighting mipmap
        for (var i = 0; i < MipLevel - 1; i++)
        {
            int currentRes = (int)((VoxelizationConfig.VoxelTextureResolution + 0.01f) / Mathf.Pow(2f, i + 1f));
            int groupNum = Mathf.CeilToInt(currentRes / 8f);

            CommandBuffer.SetComputeIntParam(VXGIComputeShader, "DstRes", currentRes);
            CommandBuffer.SetComputeIntParam(VXGIComputeShader, "SrcMipLevel", i);
            CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdMipmap, Shader.PropertyToID("MipmapSrc"), UavLighting);
            CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdMipmap, Shader.PropertyToID("MipmapDst"), LightingPingPongRT, i + 1);

            CommandBuffer.DispatchCompute(
                VXGIComputeShader,
                ComputeKernelIdMipmap,
                groupNum,
                groupNum,
                groupNum
                );

            CommandBuffer.ClearRandomWriteTargets();

            CopyTexture3D(LightingPingPongRT, UavLighting, i + 1);
        }
        
        CommandBuffer.EndSample("Direct Lighting Mipmaping");
    }

    void ComputeIndirectLighting()
    {
        CommandBuffer.BeginSample("Indirect Lighting");

        CommandBuffer.SetRenderTarget(SecondLightingRT, 0, CubemapFace.Unknown, -1);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);

        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdIndirectLighting, Shader.PropertyToID("RWAlbedo"), UavAlbedo);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdIndirectLighting, Shader.PropertyToID("RWNormal"), UavNormal);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdIndirectLighting, Shader.PropertyToID("RWOpacity"), UavOpacity);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdIndirectLighting, Shader.PropertyToID("VoxelLighting"), UavLighting);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdIndirectLighting, Shader.PropertyToID("OutIndirectRadiance"), SecondLightingRT);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "IndirectLightingMaxMipLevel", MipLevel);
        CommandBuffer.SetComputeIntParam(VXGIComputeShader, "IndirectLightingMaxStepNum", IndirectLightingConfig.IndirectLightingMaxStepNum);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "IndirectLightingAlphaAtten", IndirectLightingConfig.IndirectLightingAlphaAtten);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "IndirectLightingScale", IndirectLightingConfig.IndirectLightingScale);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "IndirectLightingFirstStep", IndirectLightingConfig.IndirectLightingFirstStep);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "IndirectLightingStepScale", IndirectLightingConfig.IndirectLightingStepScale);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "IndirectLightingConeAngle", IndirectLightingConfig.IndirectLightingConeAngle);
        CommandBuffer.SetComputeIntParam(VXGIComputeShader, "IndirectLightingMinMipLevel", IndirectLightingConfig.IndirectLightingMinMipLevel);

        CommandBuffer.DispatchCompute(
                VXGIComputeShader,
                ComputeKernelIdIndirectLighting,
                (int)(VoxelizationConfig.VoxelTextureResolution) / 8 + 1,
                (int)(VoxelizationConfig.VoxelTextureResolution) / 8 + 1,
                (int)(VoxelizationConfig.VoxelTextureResolution) / 8 + 1
                );

        CommandBuffer.EndSample("Indirect Lighting");

        CommandBuffer.BeginSample("Indirect Lighting Mipmaping");

        // generate indirect lighting mipmap
        for (var i = 0; i < MipLevel - 1; i++)
        {
            int currentRes = (int)((VoxelizationConfig.VoxelTextureResolution + 0.01f) / Mathf.Pow(2f, i + 1f));
            int groupNum = Mathf.CeilToInt(currentRes / 8f);

            CommandBuffer.SetComputeIntParam(VXGIComputeShader, "DstRes", currentRes);
            CommandBuffer.SetComputeIntParam(VXGIComputeShader, "SrcMipLevel", i);
            CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdMipmap, Shader.PropertyToID("MipmapSrc"), SecondLightingRT);
            CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdMipmap, Shader.PropertyToID("MipmapDst"), LightingPingPongRT, i + 1);
            
            CommandBuffer.DispatchCompute(
            VXGIComputeShader,
            ComputeKernelIdMipmap,
            groupNum,
            groupNum,
            groupNum
            );

            CommandBuffer.ClearRandomWriteTargets();

            CopyTexture3D(LightingPingPongRT, SecondLightingRT, i + 1);
        }

        CommandBuffer.EndSample("Indirect Lighting Mipmaping");
    }

    void ScreenConeTracing()
    {
        CommandBuffer.BeginSample("Cone Tracing");

        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenMaxMipLevel"), MipLevel);
        CommandBuffer.SetGlobalInt(Shader.PropertyToID("ScreenMaxStepNum"), ConeTracingConfig.ScreenMaxStepNum);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenAlphaAtten"), ConeTracingConfig.ScreenAlphaAtten);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenScale"), ConeTracingConfig.ScreenScale);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenConeAngle"), ConeTracingConfig.ScreenConeAngle);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenFirstStep"), ConeTracingConfig.ScreenFirstStep);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("ScreenStepScale"), ConeTracingConfig.ScreenStepScale);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenNormal"), BuiltinRenderTextureType.GBuffer2);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenAlbedo"), BuiltinRenderTextureType.GBuffer0);
        if(IndirectLightingConfig.EnableSecondBounce)
        {
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenConeTraceLighting"), SecondLightingRT);
        }
        else
        {
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenConeTraceLighting"), UavLighting);
        }
        
        CommandBuffer.SetGlobalInt(Shader.PropertyToID("EnableTemporalFilter"), TemporalFilterConfig.EnableTemporalFilter ? 1 : 0);
        CommandBuffer.SetGlobalVector(Shader.PropertyToID("ScreenResolution"), ScreenResolution);
        CommandBuffer.SetGlobalVector(Shader.PropertyToID("BlueNoiseResolution"), BlueNoiseResolution);
        CommandBuffer.SetGlobalVector(Shader.PropertyToID("BlueNoiseScale"), new Vector4(TemporalFilterConfig.BlueNoiseScale.x, 
                                                                                                                                                    TemporalFilterConfig.BlueNoiseScale.y,
                                                                                                                                                    1f / TemporalFilterConfig.BlueNoiseScale.x,
                                                                                                                                                    1f / TemporalFilterConfig.BlueNoiseScale.y));
        CommandBuffer.SetGlobalVector(Shader.PropertyToID("RandomUV"), GenerateRandomOffset()); // GenRandomUV
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("NoiseLUT"), ConeTracingConfig.BlueNoiseLUT);
        CommandBuffer.SetRenderTarget(ConeTracingRT);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);
        RenderScreenQuad(ConeTracingRT, GiMaterial, (int)PassIndex.ConeTracing);

        CommandBuffer.EndSample("Cone Tracing");
    }

    void TemporalFilter()
    {
        CommandBuffer.BeginSample("Temporal Filtering");

        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("BlendAlpha"), TemporalFilterConfig.TemporalBlendAlpha);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("TemporalClampAABBScale"), TemporalFilterConfig.ClampAABBScale);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("CurrentScreenIrradiance"), ConeTracingRT);
        
        if (PingPongFlag)
        {
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("HistoricalScreenIrradiance"), ScreenIrradianceRT0);
            CommandBuffer.SetRenderTarget(ScreenIrradianceRT1);
            RenderScreenQuad(ScreenIrradianceRT1, GiMaterial, (int)PassIndex.TemporalFilter);
        }
        else
        {
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("HistoricalScreenIrradiance"), ScreenIrradianceRT1);
            CommandBuffer.SetRenderTarget(ScreenIrradianceRT0);
            RenderScreenQuad(ScreenIrradianceRT0, GiMaterial, (int)PassIndex.TemporalFilter);
        }
        
        CommandBuffer.EndSample("Temporal Filtering");
    }

    void ComputeBilateralFiltering()
    {
        CommandBuffer.BeginSample("Bilateral Filtering");

        if (TemporalFilterConfig.EnableTemporalFilter)
        {
            if (PingPongFlag)
            {
                CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdBilateralFiltering, Shader.PropertyToID("WholeIndirectLight"), ScreenIrradianceRT1);
            }
            else
            {
                CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdBilateralFiltering, Shader.PropertyToID("WholeIndirectLight"), ScreenIrradianceRT0);
            }
            PingPongFlag = !PingPongFlag;
        }
        else
        {
            CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdBilateralFiltering, Shader.PropertyToID("WholeIndirectLight"), ConeTracingRT);
        }

        CommandBuffer.SetComputeVectorParam(VXGIComputeShader, "ScreenResolution", ScreenResolution);
        CommandBuffer.SetComputeTextureParam(VXGIComputeShader, ComputeKernelIdBilateralFiltering, Shader.PropertyToID("OutBilateralFilter"), UavBilateralFilter);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "SampleRadius", BilateralFilterConfig.BilateralSamplerRadius);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "FarClip", RenderCamera.farClipPlane);
        CommandBuffer.SetComputeFloatParam(VXGIComputeShader, "NearClip", RenderCamera.nearClipPlane);
        CommandBuffer.SetComputeVectorParam(VXGIComputeShader,
            "BilaterialThreshold",
            new Vector4(BilateralFilterConfig.DepthThresholdLowerBound,
            BilateralFilterConfig.DepthThresholdUpperBound,
            BilateralFilterConfig.NormalThresholdLowerBound,
            BilateralFilterConfig.NormalThresholdUpperBound)
            );

    CommandBuffer.DispatchCompute(
            VXGIComputeShader,
            ComputeKernelIdBilateralFiltering,
            (int)(RenderCamera.pixelWidth) / 8 + 1,
            (int)(RenderCamera.pixelHeight) / 8 + 1,
            1
            );

        CommandBuffer.ClearRandomWriteTargets();

        CommandBuffer.EndSample("Bilateral Filtering");
    }

    void Combine()
    {
        CommandBuffer.BeginSample("Combine");

        if (BilateralFilterConfig.EnableBilateralFilter)
        {
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VXGIIndirect"), UavBilateralFilter);
        }
        else
        {
            if (TemporalFilterConfig.EnableTemporalFilter)
            {
                if (PingPongFlag)
                {
                    CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VXGIIndirect"), ScreenIrradianceRT1);
                }
                else
                {
                    CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VXGIIndirect"), ScreenIrradianceRT0);
                }
                PingPongFlag = !PingPongFlag;
            }
            else
            {
                CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VXGIIndirect"), ConeTracingRT);
            }
        }

        CommandBuffer.SetGlobalInt(Shader.PropertyToID("EnableTemporalFilter"), TemporalFilterConfig.EnableTemporalFilter ? 1 : 0);
        CommandBuffer.SetGlobalInt(Shader.PropertyToID("TemporalFrameCount"), 4);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("SceneDirect"), BuiltinRenderTextureType.CameraTarget);
        CommandBuffer.SetRenderTarget(RTConeTracing);
        CommandBuffer.ClearRenderTarget(true, true, Color.black);
        RenderScreenQuad(RTConeTracing, GiMaterial, (int)PassIndex.Combine);
        Blit(RTConeTracing, BuiltinRenderTextureType.CameraTarget);

        CommandBuffer.EndSample("Combine");
    }

    void RenderDebug()
    {
        // Debug Pass
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexAlbedo"), UavAlbedo);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexNormal"), UavNormal);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexEmissive"), UavEmissive);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexOpacity"), UavOpacity);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexLighting"), UavLighting);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexIndirectLighting"), SecondLightingRT);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenConeTraceIrradiance"), ConeTracingRT);
        CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenBilateralFiltering"), UavBilateralFilter);
        if (PingPongFlag)
        {
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenBlendIrradiance"), ScreenIrradianceRT0);
        }
        else
        {
            CommandBuffer.SetGlobalTexture(Shader.PropertyToID("ScreenBlendIrradiance"), ScreenIrradianceRT1);
        }

        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("EmissiveMulti"), DirectLightingConfig.EmissiveMulti);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("VoxelSize"), VoxelizationConfig.VoxelSize);
        CommandBuffer.SetGlobalFloat(Shader.PropertyToID("RayStepSize"), DebugConfig.RayStepSize);
        CommandBuffer.SetGlobalInt(Shader.PropertyToID("VisualizeDebugType"), (int)DebugConfig.DebugType);

        CommandBuffer.SetGlobalInt(Shader.PropertyToID("DirectLightingDebugMipLevel"), Mathf.Clamp(DebugConfig.DirectLightingDebugMipLevel, 0, MipLevel - 1));
        CommandBuffer.SetGlobalInt(Shader.PropertyToID("IndirectLightingDebugMipLevel"), Mathf.Clamp(DebugConfig.IndirectLightingDebugMipLevel, 0, MipLevel - 1));

        CommandBuffer.SetRenderTarget(RTSceneColor);
        RenderScreenQuad(RTSceneColor, GiMaterial, (int)PassIndex.VoxelizationDebug);
        Blit(RTSceneColor, BuiltinRenderTextureType.CameraTarget);
    }

    void EndRender()
    {
    }

    #endregion

}

