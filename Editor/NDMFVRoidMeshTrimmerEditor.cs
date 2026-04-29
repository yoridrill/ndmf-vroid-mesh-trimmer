using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(NDMFVRoidMeshTrimmerNDMFPlugin))]

[CustomEditor(typeof(NDMFVRoidMeshTrimmer))]
public class NDMFVRoidMeshTrimmerEditor : Editor
{
    private const string LanguagePrefKey = "NDMFVRoidMeshTrimmerEditor.Language";

    private enum UiLanguage { English = 0, Japanese = 1 }
    private enum PreviewUpdateType { None, MeshOnly, TextureOnly, MeshAndTexture }

    private class RendererPreviewState
    {
        public SkinnedMeshRenderer renderer;
        public Mesh originalSharedMesh;
        public Material[] originalSharedMaterials;
        public Mesh previewMesh;
        public Material[] previewMaterials;
    }

    private class TexturePreviewState
    {
        public Texture2D originalTexture;
        public Texture2D previewTexture;
    }

    private class PreviewState
    {
        public readonly Dictionary<SkinnedMeshRenderer, RendererPreviewState> rendererStates = new Dictionary<SkinnedMeshRenderer, RendererPreviewState>();
        public readonly Dictionary<Texture2D, TexturePreviewState> textureStates = new Dictionary<Texture2D, TexturePreviewState>();
        public bool active;
        public PreviewUpdateType pending;
        public bool processing;
        public bool queued;
    }

    private static readonly Dictionary<int, PreviewState> PreviewByInstanceId = new Dictionary<int, PreviewState>();

    private readonly Dictionary<int, bool> _foldouts = new Dictionary<int, bool>();
    private UiLanguage _language;
    private string _lastFocusedControl;
    private bool _advancedFoldout;
    private int _lastHotControl;

    private void OnEnable()
    {
        _language = (UiLanguage)EditorPrefs.GetInt(LanguagePrefKey, (int)UiLanguage.English);
        SubscribeEditorEvents();
    }

    private void OnDisable() => ClearPreview((NDMFVRoidMeshTrimmer)target);

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var trimmer = (NDMFVRoidMeshTrimmer)target;
        var state = GetPreviewState(trimmer);

        if (trimmer.PreviewActiveSerialized && !state.active)
        {
            EditorGUILayout.HelpBox(T("前回のPreview状態が残っています。復旧してください。", "Preview state was left over. Please restore originals."), MessageType.Warning);
        }

        DrawTopBar(trimmer, state);

        EditorGUI.BeginChangeCheck();
        DrawBuildTargetEnables(trimmer);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(T("基本設定", "Basic Settings"), EditorStyles.boldLabel);
        DrawSetting("alphaThreshold");
        DrawSetting("maskDilatePixels");
        DrawSetting("maskClosePixels");
        DrawSetting("fillSmallHolesPixels");
        DrawSetting("removeSmallIslandsPixels");
        EditorGUILayout.PropertyField(serializedObject.FindProperty("enableTexturePadding"), new GUIContent(T("テクスチャの余白を塗り足す", "Pad Texture Transparent Areas")));

        if (EditorGUI.EndChangeCheck())
        {
            QueuePreviewUpdate(state, PreviewUpdateType.MeshOnly);
        }

        if (trimmer.enableTexturePadding)
        {
            EnsureAutoDetectedTargets(trimmer, false);
            DrawTargets(serializedObject.FindProperty("targets"), state);
        }

        DrawAdvancedSection(trimmer);
        serializedObject.ApplyModifiedProperties();

        TryFlushPreviewUpdate(trimmer, state);
    }

    private void DrawBuildTargetEnables(NDMFVRoidMeshTrimmer trimmer)
    {
        EditorGUILayout.LabelField(T("有効ビルドターゲット", "Enabled Build Targets"), EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("enableForWindows"), new GUIContent("Windows"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("enableForAndroid"), new GUIContent("Android"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("enableForiOS"), new GUIContent("iOS"));

        if (!trimmer.enableForWindows && !trimmer.enableForAndroid && !trimmer.enableForiOS)
        {
            EditorGUILayout.HelpBox(T("すべてのビルドターゲットで無効です。", "All build targets are disabled."), MessageType.Warning);
        }

        if (HasOverlappingTargetEnabledInHierarchy(trimmer))
        {
            EditorGUILayout.HelpBox(
                T("同一アバター内で同じビルドターゲット向けに複数のTrimmerが有効です。重複適用に注意してください。",
                  "Multiple Trimmers are enabled for the same build target in this avatar. Check for accidental overlap."),
                MessageType.Warning);
        }
    }

    private static bool HasOverlappingTargetEnabledInHierarchy(NDMFVRoidMeshTrimmer trimmer)
    {
        if (trimmer == null || trimmer.transform == null) return false;
        var root = trimmer.transform.root;
        if (root == null) return false;
        var trimmers = root.GetComponentsInChildren<NDMFVRoidMeshTrimmer>(true);
        foreach (var other in trimmers)
        {
            if (other == null || other == trimmer) continue;
            if ((trimmer.enableForWindows && other.enableForWindows) ||
                (trimmer.enableForAndroid && other.enableForAndroid) ||
                (trimmer.enableForiOS && other.enableForiOS))
            {
                return true;
            }
        }
        return false;
    }

    private void DrawAdvancedSection(NDMFVRoidMeshTrimmer trimmer)
    {
        EditorGUILayout.Space();
        _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, "Advanced", true);
        if (!_advancedFoldout)
        {
            return;
        }

        EditorGUI.indentLevel++;
        EditorGUILayout.HelpBox(T("Preview復旧用: 元参照へ戻します。", "Preview recovery: restore original renderer references."), MessageType.None);
        if (GUILayout.Button(T("Restore Originals", "Restore Originals")))
        {
            RestoreOriginalsFromRecovery(trimmer);
        }
        EditorGUI.indentLevel--;
    }

    private void DrawTopBar(NDMFVRoidMeshTrimmer trimmer, PreviewState state)
    {
        EditorGUILayout.BeginHorizontal();
        var oldColor = GUI.backgroundColor;
        if (state.active) GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Preview", GUILayout.Width(100f)))
        {
            if (state.active) ClearPreview(trimmer);
            else RequestBuildPreview(trimmer, state, PreviewUpdateType.MeshAndTexture);
        }
        if (state.processing)
        {
            EditorGUILayout.LabelField("Processing...", GUILayout.Width(90f));
        }
        GUI.backgroundColor = oldColor;

        GUILayout.FlexibleSpace();
        EditorGUI.BeginChangeCheck();
        _language = (UiLanguage)EditorGUILayout.EnumPopup(_language, GUILayout.Width(140f));
        if (EditorGUI.EndChangeCheck()) EditorPrefs.SetInt(LanguagePrefKey, (int)_language);
        EditorGUILayout.EndHorizontal();
    }

    private void QueuePreviewUpdate(PreviewState state, PreviewUpdateType type)
    {
        if (!state.active) return;
        if (type == PreviewUpdateType.None) return;
        if (state.pending == PreviewUpdateType.None) state.pending = type;
        else if (state.pending != type) state.pending = PreviewUpdateType.MeshAndTexture;
    }

    private void TryFlushPreviewUpdate(NDMFVRoidMeshTrimmer trimmer, PreviewState state)
    {
        if (!state.active || state.pending == PreviewUpdateType.None) return;
        bool commit = IsPreviewCommitEvent(Event.current);
        if (!commit) return;

        RequestBuildPreview(trimmer, state, state.pending);
        state.pending = PreviewUpdateType.None;
    }

    private bool IsPreviewCommitEvent(Event e)
    {
        bool focusLostCommit = false;
        string currentFocus = GUI.GetNameOfFocusedControl();
        if (!string.IsNullOrEmpty(_lastFocusedControl) && string.IsNullOrEmpty(currentFocus))
        {
            focusLostCommit = true;
        }
        _lastFocusedControl = currentFocus;

        bool hotControlReleasedCommit = _lastHotControl != 0 && GUIUtility.hotControl == 0;
        _lastHotControl = GUIUtility.hotControl;

        bool enterCommit = (e.type == EventType.KeyDown || e.type == EventType.KeyUp)
                           && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n' || e.character == '\r');
        bool commandEnterCommit = (e.type == EventType.ExecuteCommand || e.type == EventType.ValidateCommand)
                                  && (e.commandName == "Newline" || e.commandName == "SoftReturn");

        return e.type == EventType.MouseUp || enterCommit || commandEnterCommit || focusLostCommit || hotControlReleasedCommit;
    }

    private void DrawSetting(string name) => EditorGUILayout.PropertyField(serializedObject.FindProperty(name), new GUIContent(GetSettingLabel(name)));
    private string T(string ja, string en) => _language == UiLanguage.Japanese ? ja : en;

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

    private void DrawTargets(SerializedProperty targetsProp, PreviewState state)
    {
        EditorGUILayout.LabelField(T("テクスチャ対象", "Texture Targets"), EditorStyles.boldLabel);
        for (int i = 0; i < targetsProp.arraySize; i++)
        {
            var targetProp = targetsProp.GetArrayElementAtIndex(i);
            var enabledProp = targetProp.FindPropertyRelative("enabled");
            var texProp = targetProp.FindPropertyRelative("mainTexture");
            var fillEnabledProp = targetProp.FindPropertyRelative("enableTextureFill");
            var modeProp = targetProp.FindPropertyRelative("texturePostProcessMode");
            var fillColorProp = targetProp.FindPropertyRelative("fillColor");
            var usagesProp = targetProp.FindPropertyRelative("usages");

            Texture2D tex = texProp.objectReferenceValue as Texture2D;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            enabledProp.boolValue = EditorGUILayout.ToggleLeft($"{(tex ? tex.name : "(None)")} ({T("使用箇所", "Usages")}: {usagesProp.arraySize})", enabledProp.boolValue);
            if (EditorGUI.EndChangeCheck()) QueuePreviewUpdate(state, PreviewUpdateType.MeshOnly);

            EditorGUI.BeginChangeCheck();
            fillEnabledProp.boolValue = EditorGUILayout.ToggleLeft(T("Texture Fillを有効", "Enable Texture Fill"), fillEnabledProp.boolValue);
            modeProp.enumValueIndex = (int)(NDMFVRoidMeshTrimmer.TexturePostProcessMode)EditorGUILayout.EnumPopup(T("Fill Mode", "Fill Mode"), (NDMFVRoidMeshTrimmer.TexturePostProcessMode)modeProp.enumValueIndex);
            if ((NDMFVRoidMeshTrimmer.TexturePostProcessMode)modeProp.enumValueIndex == NDMFVRoidMeshTrimmer.TexturePostProcessMode.FillColor)
            {
                EditorGUILayout.PropertyField(fillColorProp, new GUIContent(T("塗り色", "Fill Color")));
            }
            if (EditorGUI.EndChangeCheck()) QueuePreviewUpdate(state, PreviewUpdateType.TextureOnly);

            _foldouts.TryGetValue(i, out bool open);
            open = EditorGUILayout.Foldout(open, T("使用箇所を表示", "Show Usages"), true);
            _foldouts[i] = open;
            if (open)
            {
                for (int u = 0; u < usagesProp.arraySize; u++)
                {
                    var usage = usagesProp.GetArrayElementAtIndex(u);
                    var rendererProp = usage.FindPropertyRelative("renderer");
                    var subMeshProp = usage.FindPropertyRelative("subMeshIndex");
                    var matProp = usage.FindPropertyRelative("material");
                    var smr = rendererProp.objectReferenceValue as SkinnedMeshRenderer;
                    var mat = matProp.objectReferenceValue as Material;
                    EditorGUILayout.LabelField($"{(smr ? smr.name : "(Missing Renderer)")} / SubMesh {subMeshProp.intValue} / {(mat ? mat.name : "(Missing Material)")}");
                }
            }

            EditorGUILayout.EndVertical();
        }
    }

    private static PreviewState GetPreviewState(NDMFVRoidMeshTrimmer trimmer)
    {
        int id = trimmer.GetInstanceID();
        if (!PreviewByInstanceId.TryGetValue(id, out var state))
        {
            state = new PreviewState();
            PreviewByInstanceId[id] = state;
        }
        return state;
    }

    private static void RequestBuildPreview(NDMFVRoidMeshTrimmer trimmer, PreviewState state, PreviewUpdateType type)
    {
        if (trimmer == null || state.queued || state.processing) return;
        EnsureAutoDetectedTargets(trimmer, !trimmer.enableTexturePadding);
        state.queued = true;
        state.processing = true;
        EditorUtility.SetDirty(trimmer);
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        EditorApplication.delayCall += () =>
        {
            state.queued = false;
            if (trimmer == null) return;

            BuildPreview(trimmer, state, type);
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        };
    }

    private static void BuildPreview(NDMFVRoidMeshTrimmer trimmer, PreviewState state, PreviewUpdateType type)
    {
        if (trimmer == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {

            if (!state.active)
            {
                state.active = true;
                CaptureOriginals(trimmer, state);
            }

            int meshCount = 0;
            int texCount = 0;

            if (type == PreviewUpdateType.MeshOnly || type == PreviewUpdateType.MeshAndTexture)
            {
                meshCount = RebuildPreviewMeshes(trimmer, state);
            }

            if (type == PreviewUpdateType.TextureOnly || type == PreviewUpdateType.MeshAndTexture)
            {
                RebuildPreviewTexturesAndMaterials(trimmer, state, ref texCount);
            }

            sw.Stop();
            Debug.Log($"[NDMF VRoid Mesh Trimmer][Preview] UpdateType={type}, Renderers={state.rendererStates.Count}, PreviewMeshes={meshCount}, PreviewTextures={texCount}, ElapsedMs={sw.ElapsedMilliseconds}");

            trimmer.PreviewActiveSerialized = true;
            EditorUtility.SetDirty(trimmer);
        }
        finally
        {
            state.processing = false;
        }
    }

    private static int RebuildPreviewMeshes(NDMFVRoidMeshTrimmer trimmer, PreviewState state)
    {
        int meshCount = 0;
        foreach (var kv in state.rendererStates)
        {
            var r = kv.Value;
            if (r.renderer == null || r.originalSharedMesh == null) continue;
            r.renderer.sharedMesh = r.originalSharedMesh;
        }

        MeshTrimProcessor.ApplyTrim(trimmer, false);
        foreach (var kv in state.rendererStates)
        {
            var r = kv.Value;
            if (r.renderer == null) continue;
            r.previewMesh = r.renderer.sharedMesh;
            if (r.previewMesh == null) continue;

            r.previewMesh.name = r.originalSharedMesh.name + " (NDMF VRoid Mesh Trimmer Preview)";
            r.previewMesh.hideFlags = HideFlags.HideAndDontSave;
            r.previewMesh.MarkDynamic();
            meshCount++;
        }

        return meshCount;
    }

    private static void CaptureOriginals(NDMFVRoidMeshTrimmer trimmer, PreviewState state)
    {
        state.rendererStates.Clear();
        trimmer.PreviewRecoveryRecords.Clear();
        foreach (var target in trimmer.targets)
        {
            foreach (var usage in target.usages)
            {
                if (usage == null || usage.renderer == null) continue;
                if (state.rendererStates.ContainsKey(usage.renderer)) continue;

                var r = new RendererPreviewState
                {
                    renderer = usage.renderer,
                    originalSharedMesh = usage.renderer.sharedMesh,
                    originalSharedMaterials = usage.renderer.sharedMaterials
                };
                state.rendererStates.Add(usage.renderer, r);
                trimmer.PreviewRecoveryRecords.Add(new NDMFVRoidMeshTrimmer.PreviewRecoveryRecord
                {
                    renderer = usage.renderer,
                    originalSharedMesh = r.originalSharedMesh,
                    originalSharedMaterials = r.originalSharedMaterials
                });
            }
        }
    }

    private static void RebuildPreviewTexturesAndMaterials(NDMFVRoidMeshTrimmer trimmer, PreviewState state, ref int textureFillExecCount)
    {
        foreach (var texState in state.textureStates.Values)
        {
            if (texState.previewTexture != null) UnityEngine.Object.DestroyImmediate(texState.previewTexture);
        }
        state.textureStates.Clear();

        var processedMap = new Dictionary<Texture2D, Texture2D>();
        foreach (var target in trimmer.targets)
        {
            if (!target.enabled || !target.enableTextureFill || target.mainTexture == null || target.texturePostProcessMode == NDMFVRoidMeshTrimmer.TexturePostProcessMode.None) continue;

            if (!processedMap.TryGetValue(target.mainTexture, out var processed))
            {
                if (!TexturePostProcessProcessor.TryCreateProcessedTextureForPreview(target.mainTexture, target.texturePostProcessMode, target.fillColor, trimmer, out processed))
                {
                    continue;
                }
                processed.name = target.mainTexture.name + " (NDMF VRoid Mesh Trimmer Preview)";
                processed.hideFlags = HideFlags.HideAndDontSave;
                processedMap[target.mainTexture] = processed;
                state.textureStates[target.mainTexture] = new TexturePreviewState { originalTexture = target.mainTexture, previewTexture = processed };
            }
        }

        foreach (var r in state.rendererStates.Values)
        {
            if (r.renderer == null) continue;
            var mats = (Material[])r.originalSharedMaterials.Clone();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (!MaterialMainTextureResolver.TryGetMainTexture(m, out var mainTex, out var prop)) continue;
                if (!processedMap.TryGetValue(mainTex, out var previewTex)) continue;

                var pm = new Material(m)
                {
                    name = m.name + " (NDMF VRoid Mesh Trimmer Preview)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                pm.SetTexture(prop, previewTex);
                mats[i] = pm;
                textureFillExecCount++;
            }

            if (r.previewMaterials != null)
            {
                foreach (var old in r.previewMaterials)
                {
                    if (old != null) UnityEngine.Object.DestroyImmediate(old);
                }
            }

            r.previewMaterials = mats;
            r.renderer.sharedMaterials = mats;
        }
    }

    private static void ClearPreview(NDMFVRoidMeshTrimmer trimmer)
    {
        if (trimmer == null) return;
        var state = GetPreviewState(trimmer);
        foreach (var r in state.rendererStates.Values)
        {
            if (r.renderer == null) continue;
            r.renderer.sharedMesh = r.originalSharedMesh;
            r.renderer.sharedMaterials = r.originalSharedMaterials;
            if (r.previewMesh != null) UnityEngine.Object.DestroyImmediate(r.previewMesh);
            if (r.previewMaterials != null)
            {
                foreach (var pm in r.previewMaterials)
                {
                    if (pm != null) UnityEngine.Object.DestroyImmediate(pm);
                }
            }
        }

        foreach (var t in state.textureStates.Values)
        {
            if (t.previewTexture != null) UnityEngine.Object.DestroyImmediate(t.previewTexture);
        }

        state.rendererStates.Clear();
        state.textureStates.Clear();
        state.active = false;
        state.pending = PreviewUpdateType.None;

        trimmer.PreviewRecoveryRecords.Clear();
        trimmer.PreviewActiveSerialized = false;
        EditorUtility.SetDirty(trimmer);
    }

    private static void RestoreOriginalsFromRecovery(NDMFVRoidMeshTrimmer trimmer)
    {
        foreach (var rec in trimmer.PreviewRecoveryRecords)
        {
            if (rec.renderer == null) continue;
            rec.renderer.sharedMesh = rec.originalSharedMesh;
            rec.renderer.sharedMaterials = rec.originalSharedMaterials;
        }
        trimmer.PreviewRecoveryRecords.Clear();
        trimmer.PreviewActiveSerialized = false;
        EditorUtility.SetDirty(trimmer);
        if (trimmer.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(trimmer.gameObject.scene);
    }

    private static bool _subscribed;
    private static void SubscribeEditorEvents()
    {
        if (_subscribed) return;
        _subscribed = true;
        EditorSceneManager.sceneSaving += (_, __) => ClearAllPreviews();
        EditorApplication.quitting += ClearAllPreviews;
        AssemblyReloadEvents.beforeAssemblyReload += ClearAllPreviews;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode) ClearAllPreviews();
        };
    }

    internal static void ClearAllPreviews()
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType<NDMFVRoidMeshTrimmer>())
        {
            ClearPreview(obj);
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
            if (renderer.sharedMesh == null) continue;
            Material[] mats = renderer.sharedMaterials;
            int scanCount = Mathf.Min(renderer.sharedMesh.subMeshCount, mats.Length);
            for (int sub = 0; sub < scanCount; sub++)
            {
                Material mat = mats[sub];
                if (mat == null) continue;
                if (!ShouldProcessMaterial(mat)) continue;
                if (!MaterialMainTextureResolver.TryGetMainTexture(mat, out Texture2D tex, out _)) continue;

                if (!grouped.TryGetValue(tex, out var targetSettings))
                {
                    targetSettings = new NDMFVRoidMeshTrimmer.TextureTargetSettings
                    {
                        enabled = true,
                        mainTexture = tex,
                        enableTextureFill = true,
                        texturePostProcessMode = NDMFVRoidMeshTrimmer.TexturePostProcessMode.Solidify,
                        usages = new List<NDMFVRoidMeshTrimmer.RendererSubMeshRef>()
                    };
                    grouped[tex] = targetSettings;
                }

                targetSettings.usages.Add(new NDMFVRoidMeshTrimmer.RendererSubMeshRef { renderer = renderer, subMeshIndex = sub, material = mat });
            }
        }

        trimmer.targets.AddRange(grouped.Values);
        EditorUtility.SetDirty(trimmer);
    }

    internal static void EnsureAutoDetectedTargets(NDMFVRoidMeshTrimmer trimmer, bool forceRefresh)
    {
        if (trimmer == null) return;
        if (forceRefresh || trimmer.targets == null || trimmer.targets.Count == 0) AutoDetectTargets(trimmer);
    }

    private static bool ShouldProcessMaterial(Material mat)
    {
        if (mat == null) return false;
        if (mat.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent) return true;

        string renderType = mat.GetTag("RenderType", false, string.Empty);
        if (string.Equals(renderType, "Transparent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(renderType, "TransparentCutout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (mat.HasProperty("_MToonSurface")) return Mathf.RoundToInt(mat.GetFloat("_MToonSurface")) != 0;
        if (mat.HasProperty("_BlendMode")) return Mathf.RoundToInt(mat.GetFloat("_BlendMode")) != 0; // legacy MToon
        if (mat.HasProperty("_TransparentMode")) return Mathf.RoundToInt(mat.GetFloat("_TransparentMode")) != 0; // lilToon
        if (mat.HasProperty("_Surface")) return Mathf.RoundToInt(mat.GetFloat("_Surface")) != 0; // URP/HDRP

        if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
        {
            return true;
        }

        return false;
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
            if (avatarRoot == null) return;
            var trimmers = avatarRoot.GetComponentsInChildren<NDMFVRoidMeshTrimmer>(true);
            NDMFVRoidMeshTrimmerEditor.ClearAllPreviews();
            foreach (var trimmer in trimmers)
            {
                if (trimmer == null || !IsEnabledForCurrentBuildTarget(trimmer)) continue;
                NDMFVRoidMeshTrimmerEditor.EnsureAutoDetectedTargets(trimmer, !trimmer.enableTexturePadding);
                MeshTrimProcessor.ApplyTrim(trimmer, true);
                TexturePostProcessProcessor.ApplyBuildTimeReplacement(trimmer);
            }
        });
    }

    private static bool IsEnabledForCurrentBuildTarget(NDMFVRoidMeshTrimmer trimmer)
    {
        switch (EditorUserBuildSettings.activeBuildTarget)
        {
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64:
                return trimmer.enableForWindows;
            case BuildTarget.Android:
                return trimmer.enableForAndroid;
            case BuildTarget.iOS:
                return trimmer.enableForiOS;
            default:
                return false;
        }
    }
}
