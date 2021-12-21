using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public enum VoxelGbufferType
{
    Albedo = 0,
    Normal,
    Emissive,
    Lighting
}

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
public class VoxelGI : MonoBehaviour
{
    //********************************************************************************************************************************************************************
    // Reflection.
    //********************************************************************************************************************************************************************

    public int ShadowMapResolution = 1024;
    public float ShadowMapRange = 50f;

    public int VoxelTextureResolution = 256;
    public float VoxelSize = 0.25f;

    public Light SunLight;
    [Range(0.0f, 10.0f)]
    public float LightIndensityMulti = 1.0f;
    [Range(0.0f, 10.0f)]
    public float EmissiveMulti = 1.0f;

    public float ShadowSunBias = 0.5f;
    public float ShadowNormalBias = 0.0f;

    public bool EnableConservativeRasterization;

    [Range(0.0f, 3.0f)]
    public float ConsevativeRasterScale = 1.5f;

    public bool DebugMode = false;
    
    public VoxelGbufferType DebugType;

    [Range(0.01f, 0.5f)]
    public float RayStepSize = 0.03f;

    //********************************************************************************************************************************************************************
    // Private.
    //********************************************************************************************************************************************************************

    private enum PassIndex
    {
        EPIVoxelization = 0,
        EPIVoxelShadow = 1,
        EPIVoxelizationDebug
    }

    private int mComputeKernelIdLighting;

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
            Vector3 fixedCameraPos = RenderCamera.transform.position / VoxelSize;
            Vector3Int intPosition = new Vector3Int((int)fixedCameraPos.x, (int)fixedCameraPos.y, (int)fixedCameraPos.z);
            fixedCameraPos.x = intPosition.x * VoxelSize;
            fixedCameraPos.y = intPosition.y * VoxelSize;
            fixedCameraPos.z = intPosition.z * VoxelSize;
            return fixedCameraPos;
        }
    }

    private RenderTexture RTSceneColor;
    private int DummyTargetID;
    private RenderTexture DummyTex;
    private RenderTextureDescriptor DummyDesc;
    private RenderTextureDescriptor mGBufferDesc;
    private RenderTextureDescriptor mLightingDesc;
    private RenderTexture UavAlbedo;
    private RenderTexture UavNormal;
    private RenderTexture UavEmissive;
    private RenderTexture UavOpacity;
    private RenderTexture UavLighting;

    private static Mesh mMesh;

    // computer shader : lighting
    ComputeShader  mComputeShader;
    Vector3 SunLightColor;
    Vector3 SunLightDirection;
    Vector3 SunLightIntensity;

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
            mComputeKernelIdLighting = mComputeShader.FindKernel("VoxelDirectLighting");
        }

        UpdateParam();
    }

    void OnEnable()
    {
        //Screen.SetResolution(1900, 900, true);

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
            if(DebugMode)
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
            RenderTextureFormat.ARGB2101010
            );
        mCommandBuffer.Clear();

        mCommandBuffer.SetGlobalVector(Shader.PropertyToID("CameraPosW"), RenderCamera.transform.position);
        var renderCameraVP = RenderCamera.projectionMatrix * RenderCamera.worldToCameraMatrix;
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraView"), RenderCamera.worldToCameraMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraViewProj"), renderCameraVP);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraInvView"), RenderCamera.cameraToWorldMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraInvViewProj"), renderCameraVP.inverse);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("CameraFielfOfView"), RenderCamera.fieldOfView);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("CameraAspect"), RenderCamera.aspect);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("VoxelTextureResolution"), VoxelTextureResolution);
    }

    void EndRender()
    {
        RenderTexture.ReleaseTemporary(RTSceneColor);
    }

    void RenderShodowMap()
    {
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

        //         mCommandBuffer.EndSample("Shadow Mapping");
    }

    void RenderVoxel()
    {
//         mCommandBuffer.BeginSample("Voxelization");

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
        
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("HalfPixelSize"), ConsevativeRasterScale / VoxelTextureResolution);
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
    }

    void ComputeDirectLighting()
    {
        mCommandBuffer.SetRenderTarget(UavLighting, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);

        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdLighting, Shader.PropertyToID("RWAlbedo"), UavAlbedo);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdLighting, Shader.PropertyToID("RWNormal"), UavNormal);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdLighting, Shader.PropertyToID("RWEmissive"), UavEmissive);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdLighting, Shader.PropertyToID("RWOpacity"), UavOpacity);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdLighting, Shader.PropertyToID("ShadowDepth"), mShadowDepth);
        mCommandBuffer.SetComputeTextureParam(mComputeShader, mComputeKernelIdLighting, Shader.PropertyToID("OutRadiance"), UavLighting);

        mCommandBuffer.SetComputeMatrixParam(mComputeShader, "gVoxelToWorld", voxelToWorld);
        mCommandBuffer.SetComputeMatrixParam(mComputeShader, "gWorldToVoxel", worldToVoxel);
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
            mComputeKernelIdLighting,
            (int)(VoxelTextureResolution) / 4 + 1,
            (int)(VoxelTextureResolution) / 4 + 1,
            (int)(VoxelTextureResolution) / 4 + 1
            );
    }

    void RenderDebug()
    {
        // Voxelization Debug Pass
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexAlbedo"), UavAlbedo);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexNormal"), UavNormal);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexEmissive"), UavEmissive);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexOpacity"), UavOpacity);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexLighting"), UavLighting);

        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("EmissiveMulti"), EmissiveMulti);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("VoxelSize"), VoxelSize);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("RayStepSize"), RayStepSize);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("VisualizeDebugType"), (int)DebugType);

        mCommandBuffer.SetRenderTarget(RTSceneColor);
        RenderScreenQuad(RTSceneColor, mGiMaterial, (int)PassIndex.EPIVoxelizationDebug);
        Blit(RTSceneColor, BuiltinRenderTextureType.CameraTarget);
    }

    #endregion

}

