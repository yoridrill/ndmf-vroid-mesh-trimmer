using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("NDMF VRoid Mesh Trimmer")]
public class NDMFVRoidMeshTrimmer : MonoBehaviour
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
        public List<RendererSubMeshRef> usages = new List<RendererSubMeshRef>();
    }

    [Serializable]
    public class PreviewRecoveryRecord
    {
        public SkinnedMeshRenderer renderer;
        public Mesh originalSharedMesh;
        public Material[] originalSharedMaterials;
    }

    public bool enabled = true;
    public List<TextureTargetSettings> targets = new List<TextureTargetSettings>();

    [Range(0f, 1f)] public float alphaThreshold = 0.5f;
    [Min(0)] public int maskDilatePixels = 2;
    [Min(0)] public int maskClosePixels = 1;
    [Min(0)] public int fillSmallHolesPixels = 16;
    [Min(0)] public int removeSmallIslandsPixels = 16;

    [Range(0f, 1f)] public float minIntersectionT = 0.02f;
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
