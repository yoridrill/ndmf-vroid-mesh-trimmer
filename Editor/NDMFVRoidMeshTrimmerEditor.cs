using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(NDMFVRoidMeshTrimmerNDMFPlugin))]

[CustomEditor(typeof(NDMFVRoidMeshTrimmer))]
public class NDMFVRoidMeshTrimmerEditor : Editor
{
    private readonly Dictionary<int, bool> _foldouts = new Dictionary<int, bool>();

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(serializedObject.FindProperty("enabled"));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Basic Settings", EditorStyles.boldLabel);
        DrawSetting("alphaThreshold");
        DrawSetting("maskDilatePixels");
        DrawSetting("maskClosePixels");
        DrawSetting("fillSmallHolesPixels");
        DrawSetting("removeSmallIslandsPixels");
        DrawSetting("minIntersectionT");
        DrawSetting("maxIntersectionT");
        DrawSetting("minTriangleUvArea");
        DrawSetting("minTriangleWorldArea");

        EditorGUILayout.Space();
        if (GUILayout.Button("Auto Detect Targets"))
        {
            AutoDetectTargets((NDMFVRoidMeshTrimmer)target);
            serializedObject.Update();
        }

        DrawTargets(serializedObject.FindProperty("targets"));

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawSetting(string name)
    {
        EditorGUILayout.PropertyField(serializedObject.FindProperty(name));
    }

    private void DrawTargets(SerializedProperty targetsProp)
    {
        EditorGUILayout.LabelField("Texture Targets", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"Count: {targetsProp.arraySize}");

        for (int i = 0; i < targetsProp.arraySize; i++)
        {
            SerializedProperty targetProp = targetsProp.GetArrayElementAtIndex(i);
            SerializedProperty enabledProp = targetProp.FindPropertyRelative("enabled");
            SerializedProperty texProp = targetProp.FindPropertyRelative("mainTexture");
            SerializedProperty postProcessModeProp = targetProp.FindPropertyRelative("texturePostProcessMode");
            SerializedProperty fillColorProp = targetProp.FindPropertyRelative("fillColor");
            SerializedProperty usagesProp = targetProp.FindPropertyRelative("usages");

            Texture2D tex = texProp.objectReferenceValue as Texture2D;
            string texName = tex != null ? tex.name : "(None)";

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            enabledProp.boolValue = EditorGUILayout.Toggle(enabledProp.boolValue, GUILayout.Width(18));
            EditorGUILayout.LabelField($"{texName}  (Usages: {usagesProp.arraySize})", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(postProcessModeProp, new GUIContent("Texture Post Process"));
            bool showFillColor = postProcessModeProp.enumValueIndex == (int)NDMFVRoidMeshTrimmer.TexturePostProcessMode.FillColor;
            using (new EditorGUI.DisabledScope(!showFillColor))
            {
                EditorGUILayout.PropertyField(fillColorProp, new GUIContent("Fill Color"));
            }

            int key = i;
            _foldouts.TryGetValue(key, out bool open);
            bool newOpen = EditorGUILayout.Foldout(open, "Show Usages", true);
            _foldouts[key] = newOpen;

            if (newOpen)
            {
                EditorGUI.indentLevel++;
                for (int u = 0; u < usagesProp.arraySize; u++)
                {
                    SerializedProperty usage = usagesProp.GetArrayElementAtIndex(u);
                    var rendererProp = usage.FindPropertyRelative("renderer");
                    var subMeshProp = usage.FindPropertyRelative("subMeshIndex");
                    var matProp = usage.FindPropertyRelative("material");

                    var smr = rendererProp.objectReferenceValue as SkinnedMeshRenderer;
                    var mat = matProp.objectReferenceValue as Material;
                    string rendererName = smr != null ? smr.name : "(Missing Renderer)";
                    string matName = mat != null ? mat.name : "(Missing Material)";
                    EditorGUILayout.LabelField($"{rendererName} / SubMesh {subMeshProp.intValue} / {matName}");
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }
    }

    private static void AutoDetectTargets(NDMFVRoidMeshTrimmer trimmer)
    {
        Undo.RecordObject(trimmer, "Auto Detect NDMF VRoid Mesh Trimmer Targets");
        trimmer.targets.Clear();

        Dictionary<Texture2D, NDMFVRoidMeshTrimmer.TextureTargetSettings> grouped =
            new Dictionary<Texture2D, NDMFVRoidMeshTrimmer.TextureTargetSettings>();

        SkinnedMeshRenderer[] renderers = trimmer.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        foreach (var renderer in renderers)
        {
            if (renderer.sharedMesh == null)
            {
                continue;
            }

            Material[] mats = renderer.sharedMaterials;
            int subMeshCount = renderer.sharedMesh.subMeshCount;
            int scanCount = Mathf.Min(subMeshCount, mats.Length);

            for (int sub = 0; sub < scanCount; sub++)
            {
                Material mat = mats[sub];
                if (mat == null)
                {
                    continue;
                }

                if (!MaterialMainTextureResolver.TryGetMainTexture(mat, out Texture2D tex, out _))
                {
                    continue;
                }

                if (!grouped.TryGetValue(tex, out var targetSettings))
                {
                    targetSettings = new NDMFVRoidMeshTrimmer.TextureTargetSettings
                    {
                        enabled = true,
                        mainTexture = tex,
                        usages = new List<NDMFVRoidMeshTrimmer.RendererSubMeshRef>()
                    };
                    grouped[tex] = targetSettings;
                }

                targetSettings.usages.Add(new NDMFVRoidMeshTrimmer.RendererSubMeshRef
                {
                    renderer = renderer,
                    subMeshIndex = sub,
                    material = mat
                });
            }
        }

        trimmer.targets.AddRange(grouped.Values);
        EditorUtility.SetDirty(trimmer);

        Debug.Log($"[NDMF VRoid Mesh Trimmer] Auto Detect completed. Texture targets: {trimmer.targets.Count}");
    }

}

public class NDMFVRoidMeshTrimmerNDMFPlugin : Plugin<NDMFVRoidMeshTrimmerNDMFPlugin>
{
    public override string DisplayName => "NDMF VRoid Mesh Trimmer";

    protected override void Configure()
    {
        var sequence = InPhase(BuildPhase.Transforming)
            .BeforePlugin("KRT.VRCQuestTools.Ndmf.VRCQuestToolsPlugin")
            .BeforePlugin("KRT.VRCQuestTools.Ndmf.AvatarConverterNdmfPlugin")
            .BeforePlugin("KRT.VRCQuestTools.AvatarConverter.Ndmf.AvatarConverterPlugin")
            .BeforePlugin("KRT.VRCQuestTools.Ndmf.MaterialConversionNdmfPlugin");

        sequence.Run("Run NDMF VRoid Mesh Trimmer", context =>
        {
            var avatarRoot = context.AvatarRootObject;
            if (avatarRoot == null)
            {
                return;
            }

            var trimmers = avatarRoot.GetComponentsInChildren<NDMFVRoidMeshTrimmer>(true);
            foreach (var trimmer in trimmers)
            {
                if (trimmer == null || !trimmer.enabled)
                {
                    continue;
                }

                MeshTrimProcessor.ApplyTrim(trimmer);
                TexturePostProcessProcessor.ApplyBuildTimeReplacement(trimmer);
            }
        });
    }
}
