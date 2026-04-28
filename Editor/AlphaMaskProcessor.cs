using System.Collections.Generic;
using UnityEngine;

public static class AlphaMaskProcessor
{
    public class AlphaMaskData
    {
        public Texture2D texture;
        public bool[,] mask;
        public int width;
        public int height;
        public TextureWrapMode wrapMode;
    }

    public static bool TryBuildMask(Texture2D texture, NDMFVRoidMeshTrimmer settings, out AlphaMaskData data)
    {
        data = null;
        if (texture == null)
        {
            return false;
        }

        Color[] pixels;
        try
        {
            pixels = texture.GetPixels();
        }
        catch (UnityException)
        {
            Debug.LogWarning($"[NDMF VRoid Mesh Trimmer] Texture is not readable and will be skipped: {texture.name}");
            return false;
        }

        int width = texture.width;
        int height = texture.height;
        bool[,] mask = new bool[width, height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float alpha = pixels[y * width + x].a;
                mask[x, y] = alpha >= settings.alphaThreshold;
            }
        }

        if (settings.maskDilatePixels > 0)
        {
            Dilate(mask, width, height, settings.maskDilatePixels);
        }

        if (settings.maskClosePixels > 0)
        {
            Close(mask, width, height, settings.maskClosePixels);
        }

        if (settings.fillSmallHolesPixels > 0)
        {
            FillSmallHoles(mask, width, height, settings.fillSmallHolesPixels);
        }

        if (settings.removeSmallIslandsPixels > 0)
        {
            RemoveSmallIslands(mask, width, height, settings.removeSmallIslandsPixels);
        }

        data = new AlphaMaskData
        {
            texture = texture,
            width = width,
            height = height,
            mask = mask,
            wrapMode = texture.wrapMode
        };
        return true;
    }

    public static bool SampleMask(AlphaMaskData data, Vector2 uv)
    {
        float u = uv.x;
        float v = uv.y;

        if (data.wrapMode == TextureWrapMode.Repeat)
        {
            u = u - Mathf.Floor(u);
            v = v - Mathf.Floor(v);
        }
        else
        {
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);
        }

        int x = Mathf.Clamp(Mathf.RoundToInt(u * (data.width - 1)), 0, data.width - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(v * (data.height - 1)), 0, data.height - 1);
        return data.mask[x, y];
    }

    private static void Dilate(bool[,] mask, int width, int height, int radius)
    {
        for (int i = 0; i < radius; i++)
        {
            bool[,] src = (bool[,])mask.Clone();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (src[x, y])
                    {
                        continue;
                    }

                    bool insideNeighbor = (x > 0 && src[x - 1, y]) ||
                                          (x < width - 1 && src[x + 1, y]) ||
                                          (y > 0 && src[x, y - 1]) ||
                                          (y < height - 1 && src[x, y + 1]);
                    if (insideNeighbor)
                    {
                        mask[x, y] = true;
                    }
                }
            }
        }
    }

    private static void Close(bool[,] mask, int width, int height, int radius)
    {
        Dilate(mask, width, height, radius);
        for (int i = 0; i < radius; i++)
        {
            bool[,] src = (bool[,])mask.Clone();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!src[x, y])
                    {
                        continue;
                    }

                    bool outsideNeighbor = (x > 0 && !src[x - 1, y]) ||
                                           (x < width - 1 && !src[x + 1, y]) ||
                                           (y > 0 && !src[x, y - 1]) ||
                                           (y < height - 1 && !src[x, y + 1]);
                    if (outsideNeighbor)
                    {
                        mask[x, y] = false;
                    }
                }
            }
        }
    }

    private static void FillSmallHoles(bool[,] mask, int width, int height, int maxArea)
    {
        bool[,] visited = new bool[width, height];
        int[] dx = { 1, -1, 0, 0 };
        int[] dy = { 0, 0, 1, -1 };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (visited[x, y] || mask[x, y])
                {
                    continue;
                }

                Queue<Vector2Int> q = new Queue<Vector2Int>();
                List<Vector2Int> region = new List<Vector2Int>();
                bool touchesEdge = false;

                visited[x, y] = true;
                q.Enqueue(new Vector2Int(x, y));

                while (q.Count > 0)
                {
                    Vector2Int p = q.Dequeue();
                    region.Add(p);

                    if (p.x == 0 || p.y == 0 || p.x == width - 1 || p.y == height - 1)
                    {
                        touchesEdge = true;
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        int nx = p.x + dx[i];
                        int ny = p.y + dy[i];
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        if (visited[nx, ny] || mask[nx, ny])
                        {
                            continue;
                        }

                        visited[nx, ny] = true;
                        q.Enqueue(new Vector2Int(nx, ny));
                    }
                }

                if (!touchesEdge && region.Count <= maxArea)
                {
                    foreach (Vector2Int p in region)
                    {
                        mask[p.x, p.y] = true;
                    }
                }
            }
        }
    }

    private static void RemoveSmallIslands(bool[,] mask, int width, int height, int maxArea)
    {
        bool[,] visited = new bool[width, height];
        int[] dx = { 1, -1, 0, 0 };
        int[] dy = { 0, 0, 1, -1 };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (visited[x, y] || !mask[x, y])
                {
                    continue;
                }

                Queue<Vector2Int> q = new Queue<Vector2Int>();
                List<Vector2Int> region = new List<Vector2Int>();

                visited[x, y] = true;
                q.Enqueue(new Vector2Int(x, y));

                while (q.Count > 0)
                {
                    Vector2Int p = q.Dequeue();
                    region.Add(p);

                    for (int i = 0; i < 4; i++)
                    {
                        int nx = p.x + dx[i];
                        int ny = p.y + dy[i];
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        if (visited[nx, ny] || !mask[nx, ny])
                        {
                            continue;
                        }

                        visited[nx, ny] = true;
                        q.Enqueue(new Vector2Int(nx, ny));
                    }
                }

                if (region.Count <= maxArea)
                {
                    foreach (Vector2Int p in region)
                    {
                        mask[p.x, p.y] = false;
                    }
                }
            }
        }
    }
}
