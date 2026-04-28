using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(NDMFVRoidMeshTrimmerNDMFPlugin))]

[CustomEditor(typeof(NDMFVRoidMeshTrimmer))]
public class NDMFVRoidMeshTrimmerEditor : Editor
{
    private const string LanguagePrefKey = "NDMFVRoidMeshTrimmerEditor.Language";
    private enum UiLanguage { English = 0, Japanese = 1 }

    private readonly Dictionary<int, bool> _foldouts = new Dictionary<int, bool>();
    private UiLanguage _language;

    private void OnEnable()
    {
        _language = (UiLanguage)EditorPrefs.GetInt(LanguagePrefKey, (int)UiLanguage.English);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawLanguageSelector();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("enabled"), new GUIContent(T("有効", "Enabled")));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(T("基本設定", "Basic Settings"), EditorStyles.boldLabel);
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
        if (GUILayout.Button(T("対象を自動検出", "Auto Detect Targets")))
        {
            AutoDetectTargets((NDMFVRoidMeshTrimmer)target);
            serializedObject.Update();
        }

        DrawTargets(serializedObject.FindProperty("targets"));

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawLanguageSelector()
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        EditorGUI.BeginChangeCheck();
        _language = (UiLanguage)EditorGUILayout.EnumPopup(_language, GUILayout.Width(140f));
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetInt(LanguagePrefKey, (int)_language);
        }
        EditorGUILayout.EndHorizontal();
    }

    private string T(string ja, string en) => _language == UiLanguage.Japanese ? ja : en;

    private void DrawSetting(string name)
    {
        EditorGUILayout.PropertyField(serializedObject.FindProperty(name), new GUIContent(GetSettingLabel(name)));
    }

    private string GetSettingLabel(string name)
    {
        switch (name)
        {
            case "alphaThreshold": return T("アルファ閾値", "Alpha Threshold");
            case "maskDilatePixels": return T("マスク膨張ピクセル", "Mask Dilate Pixels");
            case "maskClosePixels": return T("マスクCloseピクセル", "Mask Close Pixels");
            case "fillSmallHolesPixels": return T("小穴埋め面積", "Fill Small Holes Pixels");
            case "removeSmallIslandsPixels": return T("小島削除面積", "Remove Small Islands Pixels");
            case "minIntersectionT": return T("最小交点t", "Min Intersection t");
            case "maxIntersectionT": return T("最大交点t", "Max Intersection t");
            case "minTriangleUvArea": return T("最小UV三角形面積", "Min Triangle UV Area");
            case "minTriangleWorldArea": return T("最小3D三角形面積", "Min Triangle World Area");
            default: return name;
        }
    }

    private void DrawTargets(SerializedProperty targetsProp)
    {
        EditorGUILayout.LabelField(T("テクスチャ対象", "Texture Targets"), EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"{T("件数", "Count")}: {targetsProp.arraySize}");

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
            EditorGUILayout.LabelField($"{texName}  ({T("使用箇所", "Usages")}: {usagesProp.arraySize})", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(postProcessModeProp, new GUIContent(T("テクスチャ後処理", "Texture Post Process")));
            bool showFillColor = postProcessModeProp.enumValueIndex == (int)NDMFVRoidMeshTrimmer.TexturePostProcessMode.FillColor;
            using (new EditorGUI.DisabledScope(!showFillColor))
            {
                EditorGUILayout.PropertyField(fillColorProp, new GUIContent(T("塗り色", "Fill Color")));
            }

            int key = i;
            _foldouts.TryGetValue(key, out bool open);
            bool newOpen = EditorGUILayout.Foldout(open, T("使用箇所を表示", "Show Usages"), true);
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
                    string rendererName = smr != null ? smr.name : T("(Rendererなし)", "(Missing Renderer)");
                    string matName = mat != null ? mat.name : T("(Materialなし)", "(Missing Material)");
                    EditorGUILayout.LabelField($"{rendererName} / {T("サブメッシュ", "SubMesh")} {subMeshProp.intValue} / {matName}");
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
