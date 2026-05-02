using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public static class MeshTrimProcessor
{
    public struct VertexSource
    {
        public int index0; public float weight0;
        public int index1; public float weight1;
        public int index2; public float weight2;
        public int index3; public float weight3;

        public static VertexSource Original(int originalIndex)
        {
            return new VertexSource { index0 = originalIndex, weight0 = 1f, index1 = -1, index2 = -1, index3 = -1 };
        }
    }

    private class SubMeshTask
    {
        public AlphaMaskProcessor.AlphaMaskData maskData;
        public Texture2D texture;
    }

    private struct TrimStats
    {
        public int originalTriangles;
        public int outputTriangles;
        public int removedTriangles;
        public int addedVertices;
        public int intersections;
        public int allInsideButInteriorOutside;
        public int allOutsideButInteriorInside;
        public int centroidOnlyInsidePreserved;
        public int singleEdgeMidpointInsideDiscarded;
        public int singleEdgeMidpointAndCentroidInsidePreserved;
        public int twoEdgeMidpointsInsideClipped;
        public int allEdgeMidpointsInsidePreserved;
        public int fallbackPreserved;
    }

    public static void ApplyTrim(NDMFVRoidMeshTrimmer trimmer)
    {
        ApplyTrim(trimmer, true);
    }

    public static void ApplyTrim(NDMFVRoidMeshTrimmer trimmer, bool preserveBlendShapes)
    {
        if (trimmer == null)
        {
            Debug.Log("[NDMF VRoid Mesh Trimmer] Trimmer is null. Skip.");
            return;
        }

        Dictionary<Texture2D, AlphaMaskProcessor.AlphaMaskData> maskCache = new Dictionary<Texture2D, AlphaMaskProcessor.AlphaMaskData>();
        Dictionary<SkinnedMeshRenderer, Dictionary<int, SubMeshTask>> tasksByRenderer = new Dictionary<SkinnedMeshRenderer, Dictionary<int, SubMeshTask>>();

        foreach (var target in trimmer.targets)
        {
            if (target == null || !target.enabled || target.mainTexture == null)
            {
                continue;
            }

            if (!maskCache.TryGetValue(target.mainTexture, out var maskData))
            {
                if (!AlphaMaskProcessor.TryBuildMask(target.mainTexture, trimmer, out maskData))
                {
                    continue;
                }
                maskCache[target.mainTexture] = maskData;
            }

            foreach (var usage in target.usages)
            {
                if (usage == null || usage.renderer == null || !usage.renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!tasksByRenderer.TryGetValue(usage.renderer, out var subTasks))
                {
                    subTasks = new Dictionary<int, SubMeshTask>();
                    tasksByRenderer[usage.renderer] = subTasks;
                }

                subTasks[usage.subMeshIndex] = new SubMeshTask
                {
                    maskData = maskData,
                    texture = target.mainTexture
                };
            }
        }

        foreach (var kv in tasksByRenderer)
        {
            ProcessRenderer(kv.Key, kv.Value, trimmer, preserveBlendShapes);
        }
    }

    private static void ProcessRenderer(
        SkinnedMeshRenderer renderer,
        Dictionary<int, SubMeshTask> tasks,
        NDMFVRoidMeshTrimmer trimmer,
        bool preserveBlendShapes)
    {
        Mesh src = renderer.sharedMesh;
        if (src == null)
        {
            return;
        }

        var vertices = new List<Vector3>(src.vertices);
        var normals = new List<Vector3>(src.normals);
        var tangents = new List<Vector4>(src.tangents);
        var uv = new List<Vector2>(src.uv);
        var uv2 = new List<Vector2>(src.uv2);
        var uv3 = new List<Vector2>(src.uv3);
        var uv4 = new List<Vector2>(src.uv4);
        var colors = new List<Color>(src.colors);
        var boneWeights = new List<BoneWeight>(src.boneWeights);
        var vertexSources = new List<VertexSource>(vertices.Count);
        for (int v = 0; v < vertices.Count; v++)
        {
            vertexSources.Add(VertexSource.Original(v));
        }

        bool hasNormals = normals.Count == vertices.Count;
        bool hasTangents = tangents.Count == vertices.Count;
        bool hasUv = uv.Count == vertices.Count;
        bool hasUv2 = uv2.Count == vertices.Count;
        bool hasUv3 = uv3.Count == vertices.Count;
        bool hasUv4 = uv4.Count == vertices.Count;
        bool hasColors = colors.Count == vertices.Count;
        bool hasBoneWeights = boneWeights.Count == vertices.Count;

        if (!hasUv)
        {
            Debug.LogWarning($"[NDMF VRoid Mesh Trimmer] UV missing. Renderer skipped: {renderer.name}");
            return;
        }

        int baseVertexCount = vertices.Count;
        List<int>[] newSubMeshIndices = new List<int>[src.subMeshCount];

        for (int sub = 0; sub < src.subMeshCount; sub++)
        {
            int[] srcIndices = src.GetTriangles(sub);
            newSubMeshIndices[sub] = new List<int>(srcIndices.Length);

            if (!tasks.TryGetValue(sub, out var task))
            {
                newSubMeshIndices[sub].AddRange(srcIndices);
                continue;
            }

            TrimStats stats = ProcessSubMesh(
                srcIndices,
                task.maskData,
                trimmer,
                vertices,
                normals,
                tangents,
                uv,
                uv2,
                uv3,
                uv4,
                colors,
                boneWeights,
                hasNormals,
                hasTangents,
                hasUv2,
                hasUv3,
                hasUv4,
                hasColors,
                hasBoneWeights,
                vertexSources,
                newSubMeshIndices[sub]);

            stats.addedVertices = vertices.Count - baseVertexCount;
            baseVertexCount = vertices.Count;

            Debug.Log($"[NDMF VRoid Mesh Trimmer] Renderer={renderer.name}, SubMesh={sub}, Texture={task.texture.name}, " +
                      $"OriginalTriangles={stats.originalTriangles}, OutputTriangles={stats.outputTriangles}, RemovedTriangles={stats.removedTriangles}, " +
                      $"AddedVertices={stats.addedVertices}, Intersections={stats.intersections}, " +
                      $"AllInsideButInteriorOutside={stats.allInsideButInteriorOutside}, AllOutsideButInteriorInside={stats.allOutsideButInteriorInside}, " +
                      $"CentroidOnlyInsidePreserved={stats.centroidOnlyInsidePreserved}, SingleEdgeMidpointInsideDiscarded={stats.singleEdgeMidpointInsideDiscarded}, " +
                      $"SingleEdgeMidpointAndCentroidInsidePreserved={stats.singleEdgeMidpointAndCentroidInsidePreserved}, TwoEdgeMidpointsInsideClipped={stats.twoEdgeMidpointsInsideClipped}, " +
                      $"AllEdgeMidpointsInsidePreserved={stats.allEdgeMidpointsInsidePreserved}, FallbackPreserved={stats.fallbackPreserved}");
        }

        Mesh dst = new Mesh
        {
            name = src.name + "_NDMFVRoidTrimmed",
            indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            subMeshCount = src.subMeshCount,
            bindposes = src.bindposes
        };

        dst.SetVertices(vertices);
        if (hasNormals) dst.SetNormals(normals);
        if (hasTangents) dst.SetTangents(tangents);
        if (hasUv) dst.SetUVs(0, uv);
        if (hasUv2) dst.SetUVs(1, uv2);
        if (hasUv3) dst.SetUVs(2, uv3);
        if (hasUv4) dst.SetUVs(3, uv4);
        if (hasColors) dst.SetColors(colors);
        if (hasBoneWeights) dst.boneWeights = boneWeights.ToArray();

        for (int sub = 0; sub < src.subMeshCount; sub++)
        {
            dst.SetTriangles(newSubMeshIndices[sub], sub, true);
        }

        float[] savedBlendShapeWeights = SaveBlendShapeWeights(renderer, src);
        string[] savedBlendShapeNames = SaveBlendShapeNames(src);
        CopyBlendShapes(src, dst, vertexSources, renderer.name, preserveBlendShapes);

        dst.RecalculateBounds();
        renderer.sharedMesh = dst;
        RestoreBlendShapeWeights(renderer, dst, savedBlendShapeNames, savedBlendShapeWeights);
    }

    private static TrimStats ProcessSubMesh(
        int[] srcIndices,
        AlphaMaskProcessor.AlphaMaskData maskData,
        NDMFVRoidMeshTrimmer trimmer,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector4> tangents,
        List<Vector2> uv,
        List<Vector2> uv2,
        List<Vector2> uv3,
        List<Vector2> uv4,
        List<Color> colors,
        List<BoneWeight> boneWeights,
        bool hasNormals,
        bool hasTangents,
        bool hasUv2,
        bool hasUv3,
        bool hasUv4,
        bool hasColors,
        bool hasBoneWeights,
        List<VertexSource> vertexSources,
        List<int> dstIndices)
    {
        TrimStats stats = new TrimStats();
        EdgeIntersectionCache cache = new EdgeIntersectionCache();

        for (int i = 0; i < srcIndices.Length; i += 3)
        {
            int i0 = srcIndices[i];
            int i1 = srcIndices[i + 1];
            int i2 = srcIndices[i + 2];
            stats.originalTriangles++;

            bool in0 = AlphaMaskProcessor.SampleMask(maskData, uv[i0]);
            bool in1 = AlphaMaskProcessor.SampleMask(maskData, uv[i1]);
            bool in2 = AlphaMaskProcessor.SampleMask(maskData, uv[i2]);
            Vector2 m01 = (uv[i0] + uv[i1]) * 0.5f;
            Vector2 m12 = (uv[i1] + uv[i2]) * 0.5f;
            Vector2 m20 = (uv[i2] + uv[i0]) * 0.5f;
            Vector2 centroid = (uv[i0] + uv[i1] + uv[i2]) / 3f;
            bool m01In = AlphaMaskProcessor.SampleMask(maskData, m01);
            bool m12In = AlphaMaskProcessor.SampleMask(maskData, m12);
            bool m20In = AlphaMaskProcessor.SampleMask(maskData, m20);
            bool centroidIn = AlphaMaskProcessor.SampleMask(maskData, centroid);

            int insideCount = (in0 ? 1 : 0) + (in1 ? 1 : 0) + (in2 ? 1 : 0);

            if (insideCount == 3)
            {
                if (!m01In || !m12In || !m20In || !centroidIn)
                {
                    stats.allInsideButInteriorOutside++;
                }

                AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                continue;
            }

            if (insideCount == 0)
            {
                int edgeInsideCount = (m01In ? 1 : 0) + (m12In ? 1 : 0) + (m20In ? 1 : 0);
                if (m01In || m12In || m20In || centroidIn)
                {
                    stats.allOutsideButInteriorInside++;
                }

                if (edgeInsideCount == 0)
                {
                    if (centroidIn)
                    {
                        AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                        stats.centroidOnlyInsidePreserved++;
                    }
                    else
                    {
                        stats.removedTriangles++;
                    }
                    continue;
                }

                if (edgeInsideCount == 1)
                {
                    if (centroidIn)
                    {
                        AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                        stats.singleEdgeMidpointAndCentroidInsidePreserved++;
                    }
                    else
                    {
                        stats.removedTriangles++;
                        stats.singleEdgeMidpointInsideDiscarded++;
                    }
                    continue;
                }

                if (edgeInsideCount == 3)
                {
                    AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                    stats.allEdgeMidpointsInsidePreserved++;
                    continue;
                }

                int virtualInside = -1;
                int edgeA1 = -1, edgeB1 = -1;
                int edgeA2 = -1, edgeB2 = -1;
                if (m01In && m20In)
                {
                    virtualInside = i0;
                    edgeA1 = i0; edgeB1 = i1;
                    edgeA2 = i0; edgeB2 = i2;
                }
                else if (m01In && m12In)
                {
                    virtualInside = i1;
                    edgeA1 = i1; edgeB1 = i0;
                    edgeA2 = i1; edgeB2 = i2;
                }
                else if (m12In && m20In)
                {
                    virtualInside = i2;
                    edgeA1 = i2; edgeB1 = i1;
                    edgeA2 = i2; edgeB2 = i0;
                }
                else
                {
                    AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                    stats.fallbackPreserved++;
                    continue;
                }

                if (!TryCreateIntersectionFromInsideSegment(maskData, trimmer, edgeA1, edgeB1, 0.5f, 1f,
                        vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                        hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                        vertexSources,
                        ref stats, out int cutA)
                    || !TryCreateIntersectionFromInsideSegment(maskData, trimmer, edgeA2, edgeB2, 0.5f, 1f,
                        vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                        hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                        vertexSources,
                        ref stats, out int cutB))
                {
                    AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                    stats.fallbackPreserved++;
                    continue;
                }

                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, virtualInside, cutA, cutB, vertices, uv, trimmer, ref stats);
                stats.twoEdgeMidpointsInsideClipped++;
                continue;
            }

            int[] idx = { i0, i1, i2 };
            bool[] inside = { in0, in1, in2 };

            if (insideCount == 1)
            {
                int insideV = -1;
                int outA = -1;
                int outB = -1;
                for (int k = 0; k < 3; k++)
                {
                    if (inside[k])
                    {
                        insideV = idx[k];
                    }
                    else if (outA < 0)
                    {
                        outA = idx[k];
                    }
                    else
                    {
                        outB = idx[k];
                    }
                }

                int cutA = GetOrCreateIntersection(insideV, outA, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources,
                    cache, ref stats);

                int cutB = GetOrCreateIntersection(insideV, outB, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources,
                    cache, ref stats);

                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, insideV, cutA, cutB, vertices, uv, trimmer, ref stats);
            }
            else if (insideCount == 2)
            {
                int outsideV = -1;
                int inA = -1;
                int inB = -1;
                for (int k = 0; k < 3; k++)
                {
                    if (!inside[k])
                    {
                        outsideV = idx[k];
                    }
                    else if (inA < 0)
                    {
                        inA = idx[k];
                    }
                    else
                    {
                        inB = idx[k];
                    }
                }

                int cutA = GetOrCreateIntersection(inA, outsideV, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources,
                    cache, ref stats);

                int cutB = GetOrCreateIntersection(inB, outsideV, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources,
                    cache, ref stats);

                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, inA, inB, cutA, vertices, uv, trimmer, ref stats);
                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, inB, cutB, cutA, vertices, uv, trimmer, ref stats);
            }
        }

        stats.outputTriangles = dstIndices.Count / 3;
        return stats;
    }

    private static int GetOrCreateIntersection(
        int insideIndex,
        int outsideIndex,
        AlphaMaskProcessor.AlphaMaskData maskData,
        NDMFVRoidMeshTrimmer trimmer,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector4> tangents,
        List<Vector2> uv,
        List<Vector2> uv2,
        List<Vector2> uv3,
        List<Vector2> uv4,
        List<Color> colors,
        List<BoneWeight> boneWeights,
        bool hasNormals,
        bool hasTangents,
        bool hasUv2,
        bool hasUv3,
        bool hasUv4,
        bool hasColors,
        bool hasBoneWeights,
        List<VertexSource> vertexSources,
        EdgeIntersectionCache cache,
        ref TrimStats stats)
    {
        if (cache.TryGet(insideIndex, outsideIndex, out int cached))
        {
            return cached;
        }

        Vector2 uvIn = uv[insideIndex];
        Vector2 uvOut = uv[outsideIndex];

        float lo = 0f;
        float hi = 1f;
        for (int i = 0; i < 10; i++)
        {
            float mid = (lo + hi) * 0.5f;
            Vector2 uvMid = Vector2.LerpUnclamped(uvIn, uvOut, mid);
            bool inside = AlphaMaskProcessor.SampleMask(maskData, uvMid);
            if (inside)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        float t = lo;

        if (t <= trimmer.minIntersectionT)
        {
            cache.Set(insideIndex, outsideIndex, insideIndex);
            return insideIndex;
        }

        if (t >= trimmer.maxIntersectionT)
        {
            cache.Set(insideIndex, outsideIndex, outsideIndex);
            return outsideIndex;
        }

        int newIndex = AddInterpolatedVertex(insideIndex, outsideIndex, t, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
            hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref stats);
        cache.Set(insideIndex, outsideIndex, newIndex);
        return newIndex;
    }

    private static bool TryCreateIntersectionFromInsideSegment(
        AlphaMaskProcessor.AlphaMaskData maskData,
        NDMFVRoidMeshTrimmer trimmer,
        int edgeA,
        int edgeB,
        float insideT,
        float outsideT,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector4> tangents,
        List<Vector2> uv,
        List<Vector2> uv2,
        List<Vector2> uv3,
        List<Vector2> uv4,
        List<Color> colors,
        List<BoneWeight> boneWeights,
        bool hasNormals,
        bool hasTangents,
        bool hasUv2,
        bool hasUv3,
        bool hasUv4,
        bool hasColors,
        bool hasBoneWeights,
        List<VertexSource> vertexSources,
        ref TrimStats stats,
        out int index)
    {
        index = -1;
        Vector2 uvA = uv[edgeA];
        Vector2 uvB = uv[edgeB];
        Vector2 uvIn = Vector2.LerpUnclamped(uvA, uvB, insideT);
        Vector2 uvOut = Vector2.LerpUnclamped(uvA, uvB, outsideT);

        bool startInside = AlphaMaskProcessor.SampleMask(maskData, uvIn);
        bool endOutside = !AlphaMaskProcessor.SampleMask(maskData, uvOut);
        if (!startInside || !endOutside)
        {
            return false;
        }

        float lo = 0f;
        float hi = 1f;
        for (int i = 0; i < 10; i++)
        {
            float mid = (lo + hi) * 0.5f;
            Vector2 uvMid = Vector2.LerpUnclamped(uvIn, uvOut, mid);
            if (AlphaMaskProcessor.SampleMask(maskData, uvMid))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        float localT = lo;
        if (localT >= trimmer.maxIntersectionT)
        {
            index = edgeB;
            return true;
        }

        float clampedLocal = localT <= trimmer.minIntersectionT ? 0f : localT;
        float globalT = Mathf.LerpUnclamped(insideT, outsideT, clampedLocal);
        index = AddInterpolatedVertex(edgeA, edgeB, globalT, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
            hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref stats);
        return true;
    }

    private static int AddInterpolatedVertex(
        int a,
        int b,
        float t,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector4> tangents,
        List<Vector2> uv,
        List<Vector2> uv2,
        List<Vector2> uv3,
        List<Vector2> uv4,
        List<Color> colors,
        List<BoneWeight> boneWeights,
        bool hasNormals,
        bool hasTangents,
        bool hasUv2,
        bool hasUv3,
        bool hasUv4,
        bool hasColors,
        bool hasBoneWeights,
        List<VertexSource> vertexSources,
        ref TrimStats stats)
    {
        int newIndex = vertices.Count;
        vertices.Add(MeshAttributeInterpolator.Lerp(vertices[a], vertices[b], t));
        if (hasNormals) normals.Add(MeshAttributeInterpolator.Lerp(normals[a], normals[b], t).normalized);
        if (hasTangents) tangents.Add(MeshAttributeInterpolator.Lerp(tangents[a], tangents[b], t));
        uv.Add(MeshAttributeInterpolator.Lerp(uv[a], uv[b], t));
        if (hasUv2) uv2.Add(MeshAttributeInterpolator.Lerp(uv2[a], uv2[b], t));
        if (hasUv3) uv3.Add(MeshAttributeInterpolator.Lerp(uv3[a], uv3[b], t));
        if (hasUv4) uv4.Add(MeshAttributeInterpolator.Lerp(uv4[a], uv4[b], t));
        if (hasColors) colors.Add(MeshAttributeInterpolator.Lerp(colors[a], colors[b], t));
        if (hasBoneWeights) boneWeights.Add(MeshAttributeInterpolator.Lerp(boneWeights[a], boneWeights[b], t));
        vertexSources.Add(LerpVertexSource(vertexSources[a], vertexSources[b], t));
        stats.intersections++;
        return newIndex;
    }

    private static void CopyBlendShapes(
        Mesh sourceMesh,
        Mesh newMesh,
        List<VertexSource> vertexSources,
        string rendererName,
        bool preserveBlendShapes)
    {
        if (!preserveBlendShapes || sourceMesh.blendShapeCount == 0)
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer] Renderer={rendererName}, Preserve BlendShapes={preserveBlendShapes}, BlendShapeCount=0, TotalFrameCount=0, ProcessedDeltaVertexCount=0, ElapsedMs=0");
            return;
        }

        if (vertexSources.Count != newMesh.vertexCount)
        {
            Debug.LogWarning($"[NDMF VRoid Mesh Trimmer] Vertex source count mismatch. vertexSources={vertexSources.Count}, newVertexCount={newMesh.vertexCount}");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int sourceVertexCount = sourceMesh.vertexCount;
        int newVertexCount = newMesh.vertexCount;
        int totalFrames = 0;

        Vector3[] oldDeltaVertices = new Vector3[sourceVertexCount];
        Vector3[] oldDeltaNormals = new Vector3[sourceVertexCount];
        Vector3[] oldDeltaTangents = new Vector3[sourceVertexCount];
        Vector3[] newDeltaVertices = new Vector3[newVertexCount];
        Vector3[] newDeltaNormals = new Vector3[newVertexCount];
        Vector3[] newDeltaTangents = new Vector3[newVertexCount];

        for (int shapeIndex = 0; shapeIndex < sourceMesh.blendShapeCount; shapeIndex++)
        {
            string shapeName = sourceMesh.GetBlendShapeName(shapeIndex);
            int frameCount = sourceMesh.GetBlendShapeFrameCount(shapeIndex);

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                totalFrames++;
                float weight = sourceMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                sourceMesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, oldDeltaVertices, oldDeltaNormals, oldDeltaTangents);

                for (int i = 0; i < newVertexCount; i++)
                {
                    VertexSource src = vertexSources[i];
                    newDeltaVertices[i] = WeightedDelta(oldDeltaVertices, src);
                    newDeltaNormals[i] = WeightedDelta(oldDeltaNormals, src);
                    newDeltaTangents[i] = WeightedDelta(oldDeltaTangents, src);
                }

                newMesh.AddBlendShapeFrame(shapeName, weight, newDeltaVertices, newDeltaNormals, newDeltaTangents);
            }
        }

        sw.Stop();
        Debug.Log($"[NDMF VRoid Mesh Trimmer] Renderer={rendererName}, Preserve BlendShapes={preserveBlendShapes}, BlendShapeCount={sourceMesh.blendShapeCount}, TotalFrameCount={totalFrames}, ProcessedDeltaVertexCount={newVertexCount}, ElapsedMs={sw.ElapsedMilliseconds}");
    }


    private static Vector3 WeightedDelta(Vector3[] deltas, VertexSource src)
    {
        Vector3 v = Vector3.zero;
        if (src.index0 >= 0 && src.weight0 > 0f) v += deltas[src.index0] * src.weight0;
        if (src.index1 >= 0 && src.weight1 > 0f) v += deltas[src.index1] * src.weight1;
        if (src.index2 >= 0 && src.weight2 > 0f) v += deltas[src.index2] * src.weight2;
        if (src.index3 >= 0 && src.weight3 > 0f) v += deltas[src.index3] * src.weight3;
        return v;
    }

    private static VertexSource LerpVertexSource(VertexSource a, VertexSource b, float t)
    {
        float wa = 1f - t;
        float wb = t;
        int[] idx = { -1, -1, -1, -1 };
        float[] w = { 0f, 0f, 0f, 0f };

        AddWeighted(ref idx, ref w, a.index0, a.weight0 * wa);
        AddWeighted(ref idx, ref w, a.index1, a.weight1 * wa);
        AddWeighted(ref idx, ref w, a.index2, a.weight2 * wa);
        AddWeighted(ref idx, ref w, a.index3, a.weight3 * wa);

        AddWeighted(ref idx, ref w, b.index0, b.weight0 * wb);
        AddWeighted(ref idx, ref w, b.index1, b.weight1 * wb);
        AddWeighted(ref idx, ref w, b.index2, b.weight2 * wb);
        AddWeighted(ref idx, ref w, b.index3, b.weight3 * wb);

        KeepTop4(ref idx, ref w);
        Normalize(ref w);

        return new VertexSource { index0 = idx[0], weight0 = w[0], index1 = idx[1], weight1 = w[1], index2 = idx[2], weight2 = w[2], index3 = idx[3], weight3 = w[3] };
    }

    private static void AddWeighted(ref int[] idx, ref float[] w, int index, float weight)
    {
        if (index < 0 || weight <= 0f) return;
        for (int i = 0; i < 4; i++)
        {
            if (idx[i] == index) { w[i] += weight; return; }
        }
        for (int i = 0; i < 4; i++)
        {
            if (idx[i] < 0) { idx[i] = index; w[i] = weight; return; }
        }
        int min = 0;
        for (int i = 1; i < 4; i++) if (w[i] < w[min]) min = i;
        if (weight > w[min]) { idx[min] = index; w[min] = weight; }
    }

    private static void KeepTop4(ref int[] idx, ref float[] w)
    {
        for (int i = 0; i < 3; i++)
        for (int j = i + 1; j < 4; j++)
        {
            if (w[j] > w[i])
            {
                float tw = w[i]; w[i] = w[j]; w[j] = tw;
                int ti = idx[i]; idx[i] = idx[j]; idx[j] = ti;
            }
        }
    }

    private static void Normalize(ref float[] w)
    {
        float sum = w[0] + w[1] + w[2] + w[3];
        if (sum <= 0f) return;
        w[0] /= sum; w[1] /= sum; w[2] /= sum; w[3] /= sum;
    }

    private static float[] SaveBlendShapeWeights(SkinnedMeshRenderer renderer, Mesh sourceMesh)
    {
        int count = sourceMesh != null ? sourceMesh.blendShapeCount : 0;
        float[] weights = new float[count];
        for (int i = 0; i < count; i++)
        {
            weights[i] = renderer.GetBlendShapeWeight(i);
        }

        return weights;
    }

    private static string[] SaveBlendShapeNames(Mesh sourceMesh)
    {
        int count = sourceMesh != null ? sourceMesh.blendShapeCount : 0;
        string[] names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = sourceMesh.GetBlendShapeName(i);
        }

        return names;
    }

    private static void RestoreBlendShapeWeights(SkinnedMeshRenderer renderer, Mesh newMesh, string[] names, float[] weights)
    {
        if (renderer == null || newMesh == null || names == null || weights == null)
        {
            return;
        }

        int count = Mathf.Min(names.Length, weights.Length);
        for (int i = 0; i < count; i++)
        {
            int newIndex = newMesh.GetBlendShapeIndex(names[i]);
            if (newIndex < 0 && i < newMesh.blendShapeCount)
            {
                newIndex = i;
            }

            if (newIndex >= 0 && newIndex < newMesh.blendShapeCount)
            {
                renderer.SetBlendShapeWeight(newIndex, weights[i]);
            }
        }
    }

    private static void AddTrianglePreserveWinding(
        List<int> indices,
        int srcA,
        int srcB,
        int srcC,
        int a,
        int b,
        int c,
        List<Vector3> vertices,
        List<Vector2> uv,
        NDMFVRoidMeshTrimmer trimmer,
        ref TrimStats stats)
    {
        Vector3 srcN = Vector3.Cross(vertices[srcB] - vertices[srcA], vertices[srcC] - vertices[srcA]);
        Vector3 newN = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
        if (Vector3.Dot(srcN, newN) < 0f)
        {
            int tmp = b;
            b = c;
            c = tmp;
        }

        AddTriangle(indices, a, b, c, vertices, uv, trimmer, ref stats);
    }

    private static void AddTriangle(
        List<int> indices,
        int a,
        int b,
        int c,
        List<Vector3> vertices,
        List<Vector2> uv,
        NDMFVRoidMeshTrimmer trimmer,
        ref TrimStats stats)
    {
        if (a == b || b == c || c == a)
        {
            stats.removedTriangles++;
            return;
        }

        Vector2 uva = uv[a];
        Vector2 uvb = uv[b];
        Vector2 uvc = uv[c];

        float uvArea = Mathf.Abs((uvb.x - uva.x) * (uvc.y - uva.y) - (uvc.x - uva.x) * (uvb.y - uva.y)) * 0.5f;
        if (uvArea < trimmer.minTriangleUvArea)
        {
            stats.removedTriangles++;
            return;
        }

        float worldArea = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude * 0.5f;
        if (worldArea < trimmer.minTriangleWorldArea)
        {
            stats.removedTriangles++;
            return;
        }

        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }
}
