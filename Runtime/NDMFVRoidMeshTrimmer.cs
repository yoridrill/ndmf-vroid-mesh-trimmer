using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

[AddComponentMenu("yoridrill/NDMF VRoid Mesh Trimmer")]
public class NDMFVRoidMeshTrimmer : MonoBehaviour, IEditorOnly
{
    public enum TexturePostProcessMode
    {
        None = 0,
        FillColor = 1,
        Solidify = 2
    }

    [Serializable]
    public class RendererSubMeshRef
    {
        public SkinnedMeshRenderer renderer;
        public int subMeshIndex;
        public Material material;
    }

    [Serializable]
    public class TextureTargetSettings
    {
        public bool enabled = true;
        public Texture2D mainTexture;
        public bool enableTextureFill = true;
        public TexturePostProcessMode texturePostProcessMode = TexturePostProcessMode.Solidify;
        public Color fillColor = Color.black;
        public bool enablePreSubdivide = false;
        [Range(0, 2)] public int preSubdivideLevel = 0;
        public bool preSubdivideQuadAware = false;
        public List<RendererSubMeshRef> usages = new List<RendererSubMeshRef>();
    }

    [Serializable]
    public class PreviewRecoveryRecord
    {
        public SkinnedMeshRenderer renderer;
        public Mesh originalSharedMesh;
        public Material[] originalSharedMaterials;
    }

    public bool enableForWindows = false;
    public bool enableForAndroid = true;
    public bool enableForiOS = true;
    public bool enableTexturePadding = false;
    public List<TextureTargetSettings> targets = new List<TextureTargetSettings>();

    [Range(0f, 1f)] public float alphaThreshold = 0.5f;
    [Min(0)] public int maskDilatePixels = 2;
    [Min(0)] public int maskClosePixels = 1;
    [Min(0)] public int fillSmallHolesPixels = 16;
    [Min(0)] public int removeSmallIslandsPixels = 16;

    [Range(0f, 1f)] public float minIntersectionT = 0.02f;

    public bool enableBridgeCut = true;
    [Range(0f, 1f)] public float bridgeSmallKeptAreaRatio = 0.08f;
    [Range(0f, 1f)] public float bridgeSmallRemovedAreaRatio = 0.03f;
    public bool bridgeUseNeighborKeptSide = true;
    [Range(0f, 1f)] public float maxIntersectionT = 0.98f;

    [Min(0f)] public float minTriangleUvArea = 0.0000001f;
    [Min(0f)] public float minTriangleWorldArea = 0.0000001f;

    [SerializeField] private bool previewActiveSerialized;
    [SerializeField] private List<PreviewRecoveryRecord> previewRecoveryRecords = new List<PreviewRecoveryRecord>();

    public bool PreviewActiveSerialized
    {
        get => previewActiveSerialized;
        set => previewActiveSerialized = value;
    }

    public List<PreviewRecoveryRecord> PreviewRecoveryRecords => previewRecoveryRecords;
}
