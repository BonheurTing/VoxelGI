﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public enum VoxelGbufferType
{
    Albedo = 0,
    Normal,
    Emissive
}

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
public class VoxelGI : MonoBehaviour
{
    //********************************************************************************************************************************************************************
    // Reflection.
    //********************************************************************************************************************************************************************

    public int VoxelTextureResolution = 256;
    public float VoxelSize = 0.25f;

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
        EPIVoxelizationDebug
    }

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
            Vector3 fixedCameraPos = RenderCamera.transform.position * VoxelSize;
            Vector3Int intPosition = new Vector3Int((int)fixedCameraPos.x, (int)fixedCameraPos.y, (int)fixedCameraPos.z);
            intPosition.x = (int)(intPosition.x / VoxelSize);
            intPosition.y = (int)(intPosition.y / VoxelSize);
            intPosition.z = (int)(intPosition.z / VoxelSize);
            return intPosition;
        }
    }

    private RenderTexture RTSceneColor;
    private int DummyTargetID;
    private RenderTexture DummyTex;
    private RenderTextureDescriptor DummyDesc;
    private RenderTextureDescriptor mDescriptorGBuffer;
    private RenderTexture UavAlbedo;
    private RenderTexture UavNormal;
    private RenderTexture UavEmissiveHSA;
    private RenderTexture UavBrightness;

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
        mGiMaterial = new Material(Shader.Find("Hidden/VoxelGI"));

        BuildCamera();
        BuildDescripters();

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
            RenderVoxel();
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
    }

    public void BuildDescripters()
    {
        mDescriptorGBuffer = new RenderTextureDescriptor()
        {
            width = VoxelTextureResolution,
            height = VoxelTextureResolution,
            volumeDepth = VoxelTextureResolution,
            colorFormat = RenderTextureFormat.RInt,
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
        UavAlbedo = new RenderTexture(mDescriptorGBuffer);
        UavAlbedo.Create();

        UavNormal = new RenderTexture(mDescriptorGBuffer);
        UavNormal.Create();

        UavEmissiveHSA = new RenderTexture(mDescriptorGBuffer);
        UavEmissiveHSA.Create();

        UavBrightness = new RenderTexture(mDescriptorGBuffer);
        UavBrightness.Create();

        DummyTex = new RenderTexture(DummyDesc);
        DummyTex.Create();
    }

    public void ReleaseResources()
    {
        // Release render textures.
        UavAlbedo.DiscardContents();
        UavAlbedo.Release();

        UavNormal.DiscardContents();
        UavNormal.Release();

        UavEmissiveHSA.DiscardContents();
        UavEmissiveHSA.Release();

        UavBrightness.DiscardContents();
        UavBrightness.Release();

        DummyTex.DiscardContents();
        DummyTex.Release();
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

    void RenderVoxel()
    {
        // clear 3d gbuffer
        mCommandBuffer.SetRenderTarget(UavAlbedo, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);
        mCommandBuffer.SetRenderTarget(UavNormal, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);
        mCommandBuffer.SetRenderTarget(UavEmissiveHSA, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);
        mCommandBuffer.SetRenderTarget(UavBrightness, 0, CubemapFace.Unknown, -1);
        mCommandBuffer.ClearRenderTarget(true, true, Color.black);

        //mCommandBuffer
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationForwardVP"), VoxelizationCamera.projectionMatrix * ForwordViewMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationRightVP"), VoxelizationCamera.projectionMatrix * RightViewMatrix);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelizationUpVP"), VoxelizationCamera.projectionMatrix * UpViewMatrix);

        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("VoxelToWorld"), voxelToWorld);
        mCommandBuffer.SetGlobalMatrix(Shader.PropertyToID("WorldToVoxel"), worldToVoxel);
        mCommandBuffer.SetRandomWriteTarget(1, UavAlbedo);
        mCommandBuffer.SetRandomWriteTarget(2, UavNormal);
        mCommandBuffer.SetRandomWriteTarget(3, UavEmissiveHSA);
        mCommandBuffer.SetRandomWriteTarget(4, UavBrightness);
        
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

    void RenderDebug()
    {
        // Voxelization Debug Pass
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexAlbedo"), UavAlbedo);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexNormal"), UavNormal);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexEmissive"), UavEmissiveHSA);
        mCommandBuffer.SetGlobalTexture(Shader.PropertyToID("VoxelTexBrightness"), UavBrightness);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("VoxelSize"), VoxelSize);
        mCommandBuffer.SetGlobalFloat(Shader.PropertyToID("RayStepSize"), RayStepSize);
        mCommandBuffer.SetGlobalInt(Shader.PropertyToID("VisualizeDebugType"), (int)DebugType);

        mCommandBuffer.SetRenderTarget(RTSceneColor);
        RenderScreenQuad(RTSceneColor, mGiMaterial, (int)PassIndex.EPIVoxelizationDebug);
        Blit(RTSceneColor, BuiltinRenderTextureType.CameraTarget);
    }

    #endregion

}

