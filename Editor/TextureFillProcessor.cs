using System.Collections.Generic;
using UnityEngine;

public static class TexturePostProcessProcessor
{
    public static void ApplyBuildTimeReplacement(NDMFVRoidMeshTrimmer trimmer)
    {
        if (trimmer == null)
        {
            return;
        }

        var processedTextureCache = new Dictionary<Texture2D, Texture2D>();
        var materialCache = new Dictionary<int, Material>();

        foreach (var target in trimmer.targets)
        {
            if (target == null || !target.enabled || target.mainTexture == null ||
                target.texturePostProcessMode == NDMFVRoidMeshTrimmer.TexturePostProcessMode.None)
            {
                continue;
            }

            if (!processedTextureCache.TryGetValue(target.mainTexture, out Texture2D processedTexture))
            {
                if (!TryCreateProcessedTexture(target.mainTexture, target.texturePostProcessMode, target.fillColor, trimmer, out processedTexture))
                {
                    continue;
                }

                processedTextureCache[target.mainTexture] = processedTexture;
            }

            foreach (var usage in target.usages)
            {
                if (usage == null || usage.renderer == null || usage.material == null)
                {
                    continue;
                }

                Material[] sharedMaterials = usage.renderer.sharedMaterials;
                if (usage.subMeshIndex < 0 || usage.subMeshIndex >= sharedMaterials.Length)
                {
                    continue;
                }

                int key = HashMaterialKey(usage.material, processedTexture);
                if (!materialCache.TryGetValue(key, out Material replacement))
                {
                    if (!MaterialMainTextureResolver.TryGetMainTexture(usage.material, out _, out string textureProperty))
                    {
                        continue;
                    }

                    replacement = new Material(usage.material)
                    {
                        name = usage.material.name + "_NDMFVRoidProcessed"
                    };
                    replacement.SetTexture(textureProperty, processedTexture);
                    materialCache[key] = replacement;
                }

                if (sharedMaterials[usage.subMeshIndex] != replacement)
                {
                    sharedMaterials[usage.subMeshIndex] = replacement;
                    usage.renderer.sharedMaterials = sharedMaterials;
                }
            }

            Debug.Log($"[NDMF VRoid Mesh Trimmer] Build-time replacement applied. Texture={target.mainTexture.name}, Mode={target.texturePostProcessMode}");
        }
    }

    private static int HashMaterialKey(Material material, Texture2D texture)
    {
        unchecked
        {
            return (material.GetInstanceID() * 397) ^ texture.GetInstanceID();
        }
    }

    private static bool TryCreateProcessedTexture(
        Texture2D source,
        NDMFVRoidMeshTrimmer.TexturePostProcessMode mode,
        Color fillColor,
        NDMFVRoidMeshTrimmer trimmer,
        out Texture2D processed)
    {
        processed = null;

        if (!AlphaMaskProcessor.TryBuildMask(source, trimmer, out var maskData))
        {
            return false;
        }

        Color[] pixels;
        try
        {
            pixels = source.GetPixels();
        }
        catch (UnityException)
        {
            Debug.LogWarning($"[NDMF VRoid Mesh Trimmer] Texture post-process skipped (non-readable): {source.name}");
            return false;
        }

        int width = source.width;
        int height = source.height;

        if (mode == NDMFVRoidMeshTrimmer.TexturePostProcessMode.FillColor)
        {
            ApplyFillColor(maskData.mask, pixels, width, height, fillColor);
        }
        else if (mode == NDMFVRoidMeshTrimmer.TexturePostProcessMode.Solidify)
        {
            ApplySolidify(maskData.mask, pixels, width, height);
        }

        bool linear = false;
#if UNITY_2020_1_OR_NEWER
        linear = !source.isDataSRGB;
#endif
        processed = CreateWritableTexture(width, height, source, linear);
        if (processed == null)
        {
            Debug.LogWarning($"[NDMF VRoid Mesh Trimmer] Texture post-process skipped (failed to create writable texture): {source.name}");
            return false;
        }

        try
        {
            processed.SetPixels(pixels);
            processed.Apply(source.mipmapCount > 1, false);
            return true;
        }
        catch (UnityException ex)
        {
            Debug.LogWarning($"[NDMF VRoid Mesh Trimmer] Texture post-process skipped (SetPixels failed): {source.name} - {ex.Message}");
            Object.DestroyImmediate(processed);
            processed = null;
            return false;
        }
    }

    private static Texture2D CreateWritableTexture(int width, int height, Texture2D source, bool linear)
    {
        bool mipChain = source.mipmapCount > 1;
        Texture2D tex = null;

        // 圧縮/非対応フォーマットで SetPixels が失敗するケースがあるため、常に書き込み可能な標準フォーマットで作成する。
        TextureFormat[] formats =
        {
            TextureFormat.RGBA32,
            TextureFormat.ARGB32
        };

        foreach (var format in formats)
        {
            try
            {
                tex = new Texture2D(width, height, format, mipChain, linear)
                {
                    name = source.name + "_NDMFVRoidProcessed",
                    wrapMode = source.wrapMode,
                    filterMode = source.filterMode,
                    anisoLevel = source.anisoLevel
                };
                return tex;
            }
            catch (UnityException)
            {
                if (tex != null)
                {
                    Object.DestroyImmediate(tex);
                    tex = null;
                }
            }
        }

        return null;
    }

    private static void ApplyFillColor(bool[,] mask, Color[] pixels, int width, int height, Color fillColor)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (mask[x, y])
                {
                    continue;
                }

                int index = y * width + x;
                Color c = pixels[index];
                c.r = fillColor.r;
                c.g = fillColor.g;
                c.b = fillColor.b;
                pixels[index] = c;
            }
        }
    }

    private static void ApplySolidify(bool[,] mask, Color[] pixels, int width, int height)
    {
        int size = width * height;
        bool[] isSeed = new bool[size];
        int[] nearest = new int[size];
        Queue<int> queue = new Queue<int>(size);

        for (int i = 0; i < size; i++)
        {
            nearest[i] = -1;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (mask[x, y])
                {
                    isSeed[index] = true;
                    nearest[index] = index;
                    queue.Enqueue(index);
                }
            }
        }

        int[] nx = { 1, -1, 0, 0 };
        int[] ny = { 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int cx = current % width;
            int cy = current / width;

            for (int i = 0; i < 4; i++)
            {
                int tx = cx + nx[i];
                int ty = cy + ny[i];
                if (tx < 0 || ty < 0 || tx >= width || ty >= height)
                {
                    continue;
                }

                int tIndex = ty * width + tx;
                if (nearest[tIndex] >= 0)
                {
                    continue;
                }

                nearest[tIndex] = nearest[current];
                queue.Enqueue(tIndex);
            }
        }

        for (int i = 0; i < size; i++)
        {
            if (isSeed[i])
            {
                continue;
            }

            int n = nearest[i];
            if (n < 0)
            {
                continue;
            }

            Color src = pixels[n];
            Color dst = pixels[i];
            dst.r = src.r;
            dst.g = src.g;
            dst.b = src.b;
            pixels[i] = dst;
        }
    }
}
