using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
public class VoxelGI : MonoBehaviour
{
    private enum PassIndex
    {
        EPIVoxelization = 0,
        EPIVoxelizationDebug
    };

    public Texture2D MainTex = null;
    public Color AdjustColor;

    private Camera RenderCamera;
    private UnityEngine.Rendering.CommandBuffer mCommandBuffer = null;
    private Camera VoxelizationCamera;
    private Material mGiMaterial;

    // Voxelization Camera
//     private GameObject VoxelizationCameraObj = null;
    public int VoxelTextureResolution = 256;
    public float VoxelSize = 0.25f;
    public float VoxelizationRange
    {
        get
        {
            return VoxelSize * VoxelTextureResolution;
        }
    }
    public float RayStepSize = 0.5f;

    private RenderTexture RTSceneColor;
    private int DummyTargetID;
    private RenderTexture DummyTex;
    private RenderTextureDescriptor DummyDesc;
    private RenderTexture UavAlbedo;
    private RenderTextureDescriptor DescriptorAlbedo;
    private RenderTexture UavNormal;
    private RenderTextureDescriptor DescriptorNormal;
    private RenderTexture UavEmissive;
    private RenderTextureDescriptor DescriptorEmissive;

    private static Mesh mMesh;

    //********************************************************************************************************************************************************************
    // MonoBehaviour.
    //********************************************************************************************************************************************************************

    #region MonoBehaviour

    void Awake()
    {
        // Setup.
        RenderCamera = gameObject.GetComponent<Camera>();
        RenderCamera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors;
        var sd = Shader.Find("Hidden/VoxelGI");
        mGiMaterial = new Material(sd);

        BuildCamera();
        BuildUavAndDescripters();

        if (mCommandBuffer == null)
        {
            mCommandBuffer = new CommandBuffer();
            mCommandBuffer.name = "VXGI_CommandBuffer";
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
    }

    void OnPreRender()
    {
        UpdateParam();

        if (mCommandBuffer != null)
        {
            //             BeforeVoxelization();
            //             EndVoxelization();
            
            BeginRender();
            Render();
            EndRender();
        }
    }

    void OnDisable()
    {
        if (mCommandBuffer != null)
        {
            RenderCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, mCommandBuffer);
        }
    }

    void OnDestroy()
    {
        // Release render textures.
        RenderTexture.ReleaseTemporary(RTSceneColor);
        // #todo : release rt resources here.
        UavAlbedo.DiscardContents();
        UavAlbedo.Release();

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
            var origin = RenderCamera.transform.position - new Vector3(VoxelizationRange, VoxelizationRange, VoxelizationRange) * 0.5f;
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

        var rect = new Rect(0f, 0f, 1f, 1f);
        VoxelizationCamera = VoxelizationCameraObj.AddComponent<Camera>();
        VoxelizationCamera.allowMSAA = true;
        VoxelizationCamera.orthographic = true;
        VoxelizationCamera.nearClipPlane = 0f;
        VoxelizationCamera.pixelRect = rect;
        VoxelizationCamera.depth = 1f;
        //VoxelizationCamera.enabled = false;

    }


    public void BuildUavAndDescripters()
    {
        DescriptorAlbedo = new RenderTextureDescriptor()
        {
            width = VoxelTextureResolution,
            height = VoxelTextureResolution,
            volumeDepth = VoxelTextureResolution,
            colorFormat = RenderTextureFormat.RInt,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1
        };
        UavAlbedo = new RenderTexture(DescriptorAlbedo);
        UavAlbedo.Create();

        DescriptorNormal = new RenderTextureDescriptor()
        {
            width = VoxelTextureResolution,
            height = VoxelTextureResolution,
            volumeDepth = VoxelTextureResolution,
            colorFormat = RenderTextureFormat.RInt,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1
        };
        UavNormal = new RenderTexture(DescriptorAlbedo);
        UavNormal.Create();

        DescriptorEmissive = new RenderTextureDescriptor()
        {
            width = VoxelTextureResolution,
            height = VoxelTextureResolution,
            volumeDepth = VoxelTextureResolution,
            colorFormat = RenderTextureFormat.RInt,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            msaaSamples = 1
        };
        UavEmissive = new RenderTexture(DescriptorAlbedo);
        UavEmissive.Create();

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
        DummyTex = new RenderTexture(DummyDesc);
        DummyTex.Create();
    }

    public void SwitchCameraToOrthographicOrNot(bool enable)
    {
        RenderCamera.enabled = !enable;
        VoxelizationCamera.enabled = enable;
    }

    public static Mesh GetMesh()
    {
        if (mMesh != null)
            return mMesh;
            mMesh = new Mesh();
            mMesh.vertices = new Vector3[] {
                new Vector3(-1,-1,0.5f),
                new Vector3(-1,1,0.5f),
                new Vector3(1,1,0.5f),
                new Vector3(1,-1,0.5f)
            };
        mMesh.uv = new Vector2[] {
                new Vector2(0,1),
                new Vector2(0,0),
                new Vector2(1,0),
                new Vector2(1,1)
            };
 
        mMesh.SetIndices(new int[] { 0, 1, 2, 3 }, MeshTopology.Quads, 0);
        return mMesh;
    }

    public void RenderScreenQuad(RenderTargetIdentifier renderTarget, Material mat, int pass)
    {
        mCommandBuffer.SetRenderTarget(renderTarget);
        mCommandBuffer.DrawMesh(GetMesh(), Matrix4x4.identity, mat, 0, pass);
    }

    public void RenderScreenQuad(RenderTargetIdentifier renderTarget, int mipLevel, Material mat, int pass)
    {
        mCommandBuffer.SetRenderTarget(renderTarget, mipLevel);
        mCommandBuffer.DrawMesh(GetMesh(), Matrix4x4.identity, mat, 0, pass);
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
            VoxelizationCamera.farClipPlane = VoxelizationRange;
            VoxelizationCamera.orthographicSize = 0.5f * VoxelizationRange;
            VoxelizationCamera.aspect = 1;

            VoxelizationCamera.transform.position = gameObject.transform.position - Vector3.forward * VoxelizationCamera.orthographicSize;
            VoxelizationCamera.transform.LookAt(gameObject.transform.position, Vector3.up);
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
    }

    void EndRender()
    {
        RenderTexture.ReleaseTemporary(RTSceneColor);
    }

    void Render()
    {

        var viewProj = VoxelizationCamera.projectionMatrix * VoxelizationCamera.worldToCameraMatrix;

        // #todo : clear 3d gbuffer here.
        //mCommandBuffer
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationVP"), viewProj);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelToWorld"), voxelToWorld);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("WorldToVoxel"), worldToVoxel);
        mCommandBuffer.SetRandomWriteTarget(1, UavAlbedo);

        // #todo : set a dummy rt here, don't forget to create and dispose rt resource.
        mCommandBuffer.GetTemporaryRT(Shader.PropertyToID("DummyTarget"), DummyDesc);
        mCommandBuffer.SetRenderTarget(DummyTex, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
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
           // SwitchCameraToOrthographicOrNot(true);
            mCommandBuffer.DrawMesh(mesh.sharedMesh, obj.transform.localToWorldMatrix, mGiMaterial, 0, (int)PassIndex.EPIVoxelization);
            //SwitchCameraToOrthographicOrNot(false);
        }

        mCommandBuffer.ClearRandomWriteTargets();

        // Voxelization Debug Pass
        mCommandBuffer.SetGlobalVector(Shader.PropertyToID("CameraPosW"), RenderCamera.transform.position);
        var renderCameraVP = RenderCamera.projectionMatrix * RenderCamera.worldToCameraMatrix;
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraInvView"), RenderCamera.cameraToWorldMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("CameraInvViewProj"), renderCameraVP.inverse);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("CameraFielfOfView"), RenderCamera.fieldOfView);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("CameraAspect"), RenderCamera.aspect);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("VoxelTextureResolution"), VoxelTextureResolution);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("VoxelSize"), VoxelSize);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("RayStepSize"), RayStepSize);

        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexAlbedo"), UavAlbedo);
        mCommandBuffer.SetRenderTarget(RTSceneColor);
        RenderScreenQuad(RTSceneColor, mGiMaterial, (int)PassIndex.EPIVoxelizationDebug);
        Blit(RTSceneColor, BuiltinRenderTextureType.CameraTarget);
    }

    void RenderDebug()
    {
        // Voxelization Debug Pass
        
        RenderScreenQuad(RTSceneColor, mGiMaterial, (int)PassIndex.EPIVoxelizationDebug);
        Blit(RTSceneColor, BuiltinRenderTextureType.CameraTarget);
    }

    #endregion

}

