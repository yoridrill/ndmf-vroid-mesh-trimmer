using System;
using UnityEditor;
using UnityEngine;

public class UvPickerPopup : PopupWindowContent
{
    private readonly Texture2D _texture;
    private Vector2? _lastUv;
    private string _copiedText;

    public UvPickerPopup(Texture2D texture)
    {
        _texture = texture;
    }

    public override Vector2 GetWindowSize()
    {
        const float minW = 320f;
        const float minH = 220f;
        const float maxW = 1024f;
        const float maxH = 1024f;

        if (_texture == null) return new Vector2(minW, minH);
        float w = Mathf.Clamp(_texture.width, minW, maxW);
        float h = Mathf.Clamp(_texture.height + 56f, minH, maxH + 56f);
        return new Vector2(w, h);
    }

    public override void OnGUI(Rect rect)
    {
        if (_texture == null)
        {
            EditorGUILayout.HelpBox("No texture.", MessageType.Info);
            return;
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"{_texture.name} ({_texture.width}x{_texture.height})", EditorStyles.boldLabel);
        if (GUILayout.Button("×", GUILayout.Width(24f)))
        {
            editorWindow.Close();
            return;
        }
        EditorGUILayout.EndHorizontal();

        const float bottomAreaHeight = 24f;
        var imageRect = GUILayoutUtility.GetRect(rect.width - 8f, rect.height - 64f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        var fittedRect = FitRect(imageRect, _texture.width, _texture.height);

        EditorGUI.DrawPreviewTexture(fittedRect, _texture, null, ScaleMode.StretchToFill);
        DrawCrossMarker(fittedRect);

        HandleClick(fittedRect);

        GUILayout.Space(4f);
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(bottomAreaHeight)))
        {
            if (_lastUv.HasValue)
            {
                var uv = _lastUv.Value;
                EditorGUILayout.LabelField($"Selected: \"uv\": [{uv.x:F4}, {uv.y:F4}]", EditorStyles.miniBoldLabel);
            }
            else
            {
                EditorGUILayout.LabelField("Click image to copy UV", EditorStyles.miniLabel);
            }
        }

        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(bottomAreaHeight)))
        {
            if (!string.IsNullOrEmpty(_copiedText))
            {
                EditorGUILayout.LabelField($"Copied: {_copiedText}", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(string.Empty, EditorStyles.miniLabel);
            }
        }
    }

    private void HandleClick(Rect imageRect)
    {
        var e = Event.current;
        if (e.type != EventType.MouseDown || e.button != 0) return;
        if (!imageRect.Contains(e.mousePosition)) return;

        float localX = e.mousePosition.x - imageRect.x;
        float localY = e.mousePosition.y - imageRect.y;
        float u = Mathf.Clamp01(localX / imageRect.width);
        float v = Mathf.Clamp01(1f - (localY / imageRect.height));

        int x = Mathf.Clamp(Mathf.FloorToInt(u * _texture.width), 0, Mathf.Max(0, _texture.width - 1));
        int y = Mathf.Clamp(Mathf.FloorToInt((1f - v) * _texture.height), 0, Mathf.Max(0, _texture.height - 1));

        _lastUv = new Vector2(u, v);
        _copiedText = $"\"uv\": [{u:F4}, {v:F4}]";
        EditorGUIUtility.systemCopyBuffer = _copiedText;

        Debug.Log($"[NDMF VRoid Mesh Trimmer] UV copied. Texture={_texture.name}, UV=({u:F4}, {v:F4}), Pixel=({x}, {y})");
        editorWindow.Repaint();
        e.Use();
    }

    private void DrawCrossMarker(Rect imageRect)
    {
        if (!_lastUv.HasValue) return;
        var uv = _lastUv.Value;
        float px = imageRect.x + uv.x * imageRect.width;
        float py = imageRect.y + (1f - uv.y) * imageRect.height;
        var color = new Color(1f, 0.2f, 0.2f, 0.95f);
        EditorGUI.DrawRect(new Rect(px - 8f, py - 1f, 16f, 2f), color);
        EditorGUI.DrawRect(new Rect(px - 1f, py - 8f, 2f, 16f), color);
    }

    private static Rect FitRect(Rect outer, float texW, float texH)
    {
        if (texW <= 0 || texH <= 0) return outer;
        float outerAspect = outer.width / Mathf.Max(1f, outer.height);
        float texAspect = texW / texH;
        if (texAspect > outerAspect)
        {
            float h = outer.width / texAspect;
            float y = outer.y + (outer.height - h) * 0.5f;
            return new Rect(outer.x, y, outer.width, h);
        }

        float w = outer.height * texAspect;
        float x = outer.x + (outer.width - w) * 0.5f;
        return new Rect(x, outer.y, w, outer.height);
    }
}
