using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public static class MeshTrimProcessor
{
    private class FourPointDebugCandidate
    {
        public float suspicion;
        public string logBlock;
    }
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

    public struct VertexData
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector4 tangent;
        public Color color;
        public BoneWeight boneWeight;
    }

    private class SubMeshTask
    {
        public AlphaMaskProcessor.AlphaMaskData maskData;
        public Texture2D texture;
        public bool enablePreSubdivide;
        public int preSubdivideLevel;
        public bool preSubdivideQuadAware;
    }


    private enum TriangleTrimState
    {
        StrongKeep,
        StrongTrim,
        Clipped,
        Ambiguous
    }

    private struct TriangleTrimResult
    {
        public int triangleIndex;
        public TriangleTrimState state;
        public float originalAreaUv;
        public float keptAreaUv;
        public float removedAreaUv;
        public float keptAreaRatio;
        public float removedAreaRatio;
        public int cutEdges;
        public int[] cutPoints;
        public Vector2 keptRegionCentroidUv;
        public Vector2 removedRegionCentroidUv;
        public int keptRegionContactEdges;
        public int removedRegionContactEdges;
        public int generatedTriangles;
    }

    private struct BoundaryPoint
    {
        public int edgeIndex;
        public float localTOnEdge;
        public EdgeCrossing crossing;
        public int vertexIndex;
        public Vector2 uv;
        public float perimeterOrder;
    }

    private struct EdgeCutInfo
    {
        public long edgeKey;
        public int triangleIndex;
        public int cutPointIndex;
        public Vector2 cutPointUv;
        public int edgeA;
        public int edgeB;
        public Vector2 keptSideCentroidUv;
        public Vector2 removedSideCentroidUv;
        public bool keptSideUsesEdgeA;
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
        public int trianglesWithFourOrMoreBoundaryPoints;
        public int zeroBoundaryKeepCount;
        public int zeroBoundaryDeleteCount;
        public int centroidOnlyDeletedCount;
        public int zeroBoundaryMixedKeepCount;
        public int zeroBoundaryMixedDeleteCount;
        public int twoBoundaryProcessedCount;
        public int twoBoundaryKeptRegionCount;
        public int twoBoundaryGeneratedTrianglesCount;
        public int twoBoundaryFallbackCount;
        public int fourPlusBoundaryFallbackCount;
        public int fourPlusBoundaryKeepCount;
        public int fourPlusBoundaryDeleteCount;
        public int fourPointClipSuccessCount;
        public int fourPointClipFallbackCount;
        public int fourPointClipGeneratedTriangles;
        public int fourPointClipGeneratedZeroFallbackCount;
        public int fourPointGeneratedTriangleInsideCount;
        public int fourPointGeneratedTriangleOutsideCount;
        public int fourPointReversedSideSuspectedCount;
        public int fourPointFallbackDueToInvalidSampleCount;
        public int fourPointPairingCandidatesTestedCount;
        public int fourPointPairingASelectedCount;
        public int fourPointPairingBSelectedCount;
        public int fourPointPairingCSelectedCount;
        public int fourPointPairingRejectedByIntersectionCount;
        public int fourPointPairingRejectedBySameEdgeLineCount;
        public int fourPointPairingRejectedByDegenerateCount;
        public int fourPointPairingFallbackCount;
        public float fourPointBestScore;
        public int fourPointGeneratedTrianglesCount;
        public int fourPointKeptTrianglesCount;
        public int fourPointDiscardedTrianglesCount;
        public int twoEdgeTwoCrossingsDetectedCount;
        public int twoEdgeTwoCrossingsStripKeptCount;
        public int twoEdgeTwoCrossingsStripDiscardedCount;
        public int twoEdgeTwoCrossingsIntervalMismatchCount;
        public int twoEdgeTwoCrossingsStripRejectedByMidpointContainmentCount;
        public int twoEdgeTwoCrossingsAltOutsideUsedCount;
        public int twoEdgeTwoCrossingsOutsideDirectOutputCount;
        public int fallbackToLegacySingleCutCount;
        public int legacySingleCutSucceededAfterTwoLineFailureCount;
        public int legacySingleCutFailedCount;
        public int legacySingleCutAttemptCount;
        public int fallbackToOldKeepDeleteCount;
        public int finalPathTwoLineCount;
        public int finalPathLegacySingleCutCount;
        public int finalPathOldFallbackCount;
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
        Debug.Log($"[NDMF VRoid Mesh Trimmer][4pt-debug] ApplyTrim entered. RendererCountTargets={trimmer.targets?.Count ?? 0}, debugFourPointClipDetails={trimmer.debugFourPointClipDetails}, max={trimmer.debugFourPointClipMaxTriangles}, filter={trimmer.debugFourPointMaterialFilter}, preserveBlendShapes={preserveBlendShapes}");

        Dictionary<Texture2D, AlphaMaskProcessor.AlphaMaskData> maskCache = new Dictionary<Texture2D, AlphaMaskProcessor.AlphaMaskData>();
        Dictionary<SkinnedMeshRenderer, Dictionary<int, SubMeshTask>> tasksByRenderer = new Dictionary<SkinnedMeshRenderer, Dictionary<int, SubMeshTask>>();
        if (trimmer.debugFourPointClipDetails)
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][4pt-debug] Debug enabled. MaxTriangles={trimmer.debugFourPointClipMaxTriangles}, MaterialFilter={trimmer.debugFourPointMaterialFilter}");
        }
        int preSubdivideEnabledTargetCount = 0;
        int quadAwareEnabledTargetCount = 0;

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

            if (target.enablePreSubdivide) preSubdivideEnabledTargetCount++;
            if (target.enablePreSubdivide && target.preSubdivideQuadAware) quadAwareEnabledTargetCount++;

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

                if (!subTasks.TryGetValue(usage.subMeshIndex, out var existingTask))
                {
                    existingTask = new SubMeshTask();
                    subTasks[usage.subMeshIndex] = existingTask;
                }

                existingTask.maskData = maskData;
                existingTask.texture = target.mainTexture;
                existingTask.enablePreSubdivide = existingTask.enablePreSubdivide || target.enablePreSubdivide;
                existingTask.preSubdivideLevel = Mathf.Max(existingTask.preSubdivideLevel, target.preSubdivideLevel);
                existingTask.preSubdivideQuadAware = existingTask.preSubdivideQuadAware || target.preSubdivideQuadAware;
            }
        }

        if (trimmer.debugFourPointClipDetails && tasksByRenderer.Count == 0)
        {
            Debug.Log("[NDMF VRoid Mesh Trimmer][4pt-debug] No trim tasks were built. Check target/usages/active renderer/material bindings.");
        }

        Debug.Log($"[NDMF VRoid Mesh Trimmer] Trim task renderers={tasksByRenderer.Count}, PreSubdivideEnabledTargetCount={preSubdivideEnabledTargetCount}, QuadAwareEnabledTargetCount={quadAwareEnabledTargetCount}");
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

            int[] workingIndices = srcIndices;
            int triBeforeSub = srcIndices.Length / 3;
            int preAddedVertices = 0;
            int quadCandidates = 0;
            int acceptedQuads = 0;
            int rejectedQuads = 0;
            int triFallback = 0;
            var swPre = System.Diagnostics.Stopwatch.StartNew();
            if (task.enablePreSubdivide && task.preSubdivideLevel > 0)
            {
                // Always use midpoint triangle subdivision.
                // Quad-aware pre-subdivide is intentionally disabled due shape drift on non-planar quads.
                workingIndices = PreSubdivideIndices(srcIndices, task.preSubdivideLevel, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref preAddedVertices);
            }
            swPre.Stop();

            string materialName = (renderer.sharedMaterials != null && sub < renderer.sharedMaterials.Length && renderer.sharedMaterials[sub] != null) ? renderer.sharedMaterials[sub].name : "(null)";
            string textureName = task.texture != null ? task.texture.name : "(null)";
            TrimStats stats = ProcessSubMesh(
                workingIndices,
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
                newSubMeshIndices[sub],
                renderer.name,
                sub,
                materialName,
                textureName);

            stats.addedVertices = vertices.Count - baseVertexCount;
            baseVertexCount = vertices.Count;

            Debug.Log($"[NDMF VRoid Mesh Trimmer] Renderer={renderer.name}, SubMesh={sub}, Texture={task.texture.name}, PreSubdivideEnabled={task.enablePreSubdivide}, PreSubdivideLevel={task.preSubdivideLevel}, QuadAware={task.preSubdivideQuadAware}, QuadCandidates={quadCandidates}, AcceptedQuads={acceptedQuads}, RejectedQuadCandidates={rejectedQuads}, TriangleFallbackCount={triFallback}, TrianglesBeforePreSubdivide={triBeforeSub}, TrianglesAfterPreSubdivide={workingIndices.Length / 3}, PreSubdivideAddedVertices={preAddedVertices}, PreSubdivideMs={swPre.ElapsedMilliseconds}, " +
                      $"OriginalTriangles={stats.originalTriangles}, OutputTriangles={stats.outputTriangles}, RemovedTriangles={stats.removedTriangles}, " +
                      $"AddedVertices={stats.addedVertices}, Intersections={stats.intersections}, " +
                      $"AllInsideButInteriorOutside={stats.allInsideButInteriorOutside}, AllOutsideButInteriorInside={stats.allOutsideButInteriorInside}, " +
                      $"CentroidOnlyInsidePreserved={stats.centroidOnlyInsidePreserved}, SingleEdgeMidpointInsideDiscarded={stats.singleEdgeMidpointInsideDiscarded}, " +
                      $"SingleEdgeMidpointAndCentroidInsidePreserved={stats.singleEdgeMidpointAndCentroidInsidePreserved}, TwoEdgeMidpointsInsideClipped={stats.twoEdgeMidpointsInsideClipped}, " +
                      $"AllEdgeMidpointsInsidePreserved={stats.allEdgeMidpointsInsidePreserved}, FallbackPreserved={stats.fallbackPreserved}, TrianglesAfterTrim={stats.outputTriangles}");
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

    private static int[] PreSubdivideIndices(
        int[] srcIndices,
        int level,
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
        ref int addedVertices)
    {
        var indices = srcIndices;
        for (int lv = 0; lv < level; lv++)
        {
            var cache = new Dictionary<long, int>();
            var next = new List<int>(indices.Length * 4);
            for (int i = 0; i < indices.Length; i += 3)
            {
                int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                int m01 = GetOrCreateMid(i0, i1, cache, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref addedVertices);
                int m12 = GetOrCreateMid(i1, i2, cache, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref addedVertices);
                int m20 = GetOrCreateMid(i2, i0, cache, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref addedVertices);
                next.Add(i0); next.Add(m01); next.Add(m20);
                next.Add(m01); next.Add(i1); next.Add(m12);
                next.Add(m20); next.Add(m12); next.Add(i2);
                next.Add(m01); next.Add(m12); next.Add(m20);
            }
            indices = next.ToArray();
        }
        return indices;
    }

    private static int GetOrCreateMid(int a, int b, Dictionary<long, int> cache,
        List<Vector3> vertices, List<Vector3> normals, List<Vector4> tangents, List<Vector2> uv, List<Vector2> uv2, List<Vector2> uv3, List<Vector2> uv4,
        List<Color> colors, List<BoneWeight> boneWeights, bool hasNormals, bool hasTangents, bool hasUv2, bool hasUv3, bool hasUv4, bool hasColors, bool hasBoneWeights,
        List<VertexSource> vertexSources, ref int addedVertices)
    {
        int lo = Math.Min(a,b), hi=Math.Max(a,b); long key=((long)lo<<32)|(uint)hi;
        if (cache.TryGetValue(key, out int idx)) return idx;
        TrimStats dummy = default;
        idx = AddInterpolatedVertex(a,b,0.5f,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources, ref dummy);
        cache[key]=idx; addedVertices++; return idx;
    }



    private struct QuadCandidate { public int triA; public int triB; public int v0; public int v1; public int v2; public int v3; public float score; }

    private static int[] PreSubdivideIndicesQuadAware(
        int[] srcIndices, int level,
        List<Vector3> vertices, List<Vector3> normals, List<Vector4> tangents, List<Vector2> uv, List<Vector2> uv2, List<Vector2> uv3, List<Vector2> uv4,
        List<Color> colors, List<BoneWeight> boneWeights, bool hasNormals, bool hasTangents, bool hasUv2, bool hasUv3, bool hasUv4, bool hasColors, bool hasBoneWeights,
        List<VertexSource> vertexSources, ref int addedVertices, out int quadCandidates, out int acceptedQuads, out int rejectedQuads, out int triFallback)
    {
        int[] indices = srcIndices;
        quadCandidates = 0; acceptedQuads = 0; rejectedQuads = 0; triFallback = 0;
        int rejZero = 0, rejSelf = 0, rejWind = 0;
        for (int lv = 0; lv < level; lv++)
        {
            int triCount = indices.Length / 3;
            var edgeMap = new Dictionary<long, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int i0 = indices[t*3], i1 = indices[t*3+1], i2 = indices[t*3+2];
                AddTriEdgeIndex(edgeMap, i0, i1, t);
                AddTriEdgeIndex(edgeMap, i1, i2, t);
                AddTriEdgeIndex(edgeMap, i2, i0, t);
            }

            var candidates = new List<QuadCandidate>();
            foreach (var kv in edgeMap)
            {
                if (kv.Value.Count != 2) continue;
                int ta = kv.Value[0], tb = kv.Value[1];
                int a0=indices[ta*3],a1=indices[ta*3+1],a2=indices[ta*3+2];
                int b0=indices[tb*3],b1=indices[tb*3+1],b2=indices[tb*3+2];
                if (!TryBuildRelaxedQuad(a0,a1,a2,b0,b1,b2,uv,out int q0,out int q1,out int q2,out int q3,out float score,out int rej))
                {
                    if (rej==1) rejZero++; else if (rej==2) rejSelf++; else rejWind++;
                    continue;
                }
                candidates.Add(new QuadCandidate{triA=ta,triB=tb,v0=q0,v1=q1,v2=q2,v3=q3,score=score});
            }

            candidates.Sort((x,y)=>y.score.CompareTo(x.score));
            quadCandidates = candidates.Count;
            var used = new bool[triCount];
            var cache = new Dictionary<long,int>();
            var next = new List<int>(indices.Length*4);

            foreach (var c in candidates)
            {
                if (used[c.triA] || used[c.triB]) continue;
                int q0=c.v0,q1=c.v1,q2=c.v2,q3=c.v3;
                int m01 = GetOrCreateMid(q0,q1,cache,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources,ref addedVertices);
                int m12 = GetOrCreateMid(q1,q2,cache,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources,ref addedVertices);
                int m23 = GetOrCreateMid(q2,q3,cache,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources,ref addedVertices);
                int m30 = GetOrCreateMid(q3,q0,cache,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources,ref addedVertices);
                int center = GetOrCreateMid(m01,m23,cache,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources,ref addedVertices);
                AddQuadAsTris(next,q0,m01,center,m30);
                AddQuadAsTris(next,m01,q1,m12,center);
                AddQuadAsTris(next,center,m12,q2,m23);
                AddQuadAsTris(next,m30,center,m23,q3);
                used[c.triA]=used[c.triB]=true; acceptedQuads++;
            }

            for (int t=0;t<triCount;t++)
            {
                if (used[t]) continue;
                triFallback++;
                int i0=indices[t*3],i1=indices[t*3+1],i2=indices[t*3+2];
                int m01=GetOrCreateMid(i0,i1,cache,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources,ref addedVertices);
                int m12=GetOrCreateMid(i1,i2,cache,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources,ref addedVertices);
                int m20=GetOrCreateMid(i2,i0,cache,vertices,normals,tangents,uv,uv2,uv3,uv4,colors,boneWeights,hasNormals,hasTangents,hasUv2,hasUv3,hasUv4,hasColors,hasBoneWeights,vertexSources,ref addedVertices);
                next.Add(i0); next.Add(m01); next.Add(m20);
                next.Add(m01); next.Add(i1); next.Add(m12);
                next.Add(m20); next.Add(m12); next.Add(i2);
                next.Add(m01); next.Add(m12); next.Add(m20);
            }
            rejectedQuads = Math.Max(0, quadCandidates - acceptedQuads);
            Debug.Log($"[NDMF VRoid Mesh Trimmer] QuadRelaxed stats: TotalTriangles={triCount}, QuadCandidates={quadCandidates}, AcceptedQuads={acceptedQuads}, RejectedByZeroArea={rejZero}, RejectedBySelfIntersection={rejSelf}, RejectedByInvalidWinding={rejWind}, FallbackTriangles={triFallback}");
            indices = next.ToArray();
        }
        return indices;
    }

    private static void AddTriEdgeIndex(Dictionary<long, List<int>> map, int a, int b, int tri)
    {
        int lo=Math.Min(a,b),hi=Math.Max(a,b); long key=((long)lo<<32)|(uint)hi;
        if(!map.TryGetValue(key,out var list)){list=new List<int>(2); map[key]=list;}
        if (!list.Contains(tri)) list.Add(tri);
    }

    private static bool TryBuildRelaxedQuad(int a0,int a1,int a2,int b0,int b1,int b2,List<Vector2> uv,out int q0,out int q1,out int q2,out int q3,out float score,out int reject)
    {
        q0=q1=q2=q3=-1; score=0f; reject=0;
        var uniq = new List<int>(4);
        AddUnique(uniq,a0); AddUnique(uniq,a1); AddUnique(uniq,a2); AddUnique(uniq,b0); AddUnique(uniq,b1); AddUnique(uniq,b2);
        if (uniq.Count != 4) { reject=3; return false; }
        float ta = Mathf.Abs(SignedArea(uv[a0],uv[a1],uv[a2]));
        float tb = Mathf.Abs(SignedArea(uv[b0],uv[b1],uv[b2]));
        if (ta < 1e-8f || tb < 1e-8f) { reject=1; return false; }
        var center = (uv[uniq[0]]+uv[uniq[1]]+uv[uniq[2]]+uv[uniq[3]])*0.25f;
        uniq.Sort((i,j)=>Mathf.Atan2(uv[i].y-center.y,uv[i].x-center.x).CompareTo(Mathf.Atan2(uv[j].y-center.y,uv[j].x-center.x)));
        q0=uniq[0]; q1=uniq[1]; q2=uniq[2]; q3=uniq[3];
        float area = SignedArea(uv[q0],uv[q1],uv[q2])+SignedArea(uv[q0],uv[q2],uv[q3]);
        if (Mathf.Abs(area) < 1e-8f) { reject=1; return false; }
        if (area < 0f) { int t=q1; q1=q3; q3=t; }
        if (SegmentsIntersect(uv[q0],uv[q1],uv[q2],uv[q3])) { reject=2; return false; }
        float quadArea = Mathf.Abs(SignedArea(uv[q0],uv[q1],uv[q2])) + Mathf.Abs(SignedArea(uv[q0],uv[q2],uv[q3]));
        float balance = Mathf.Min(ta,tb)/Mathf.Max(ta,tb);
        score = quadArea * (0.5f + 0.5f*balance);
        return true;
    }

    private static void AddUnique(List<int> list, int v){ if (!list.Contains(v)) list.Add(v); }

    private static bool IsSafeQuadUv(Vector2 q00, Vector2 q10, Vector2 q11, Vector2 q01)
    {
        float area = Mathf.Abs(SignedArea(q00, q10, q11)) + Mathf.Abs(SignedArea(q00, q11, q01));
        if (area < 1e-8f) return false;
        if (SegmentsIntersect(q00, q10, q11, q01)) return false;
        if (!IsConvexLike(q00, q10, q11, q01)) return false;
        return true;
    }

    private static float SignedArea(Vector2 a, Vector2 b, Vector2 c)
    {
        return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }

    private static bool IsConvexLike(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float z1 = SignedArea(a, b, c);
        float z2 = SignedArea(b, c, d);
        float z3 = SignedArea(c, d, a);
        float z4 = SignedArea(d, a, b);
        int pos = (z1 > 0 ? 1 : 0) + (z2 > 0 ? 1 : 0) + (z3 > 0 ? 1 : 0) + (z4 > 0 ? 1 : 0);
        int neg = (z1 < 0 ? 1 : 0) + (z2 < 0 ? 1 : 0) + (z3 < 0 ? 1 : 0) + (z4 < 0 ? 1 : 0);
        return pos == 0 || neg == 0 || pos == 1 || neg == 1;
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        float o1 = SignedArea(p1, p2, q1);
        float o2 = SignedArea(p1, p2, q2);
        float o3 = SignedArea(q1, q2, p1);
        float o4 = SignedArea(q1, q2, p2);
        return (o1 * o2 < 0f) && (o3 * o4 < 0f);
    }

    private static void GetShared(int a0,int a1,int a2,int b0,int b1,int b2,out int s0,out int s1){s0=-1;s1=-1;int[] a={a0,a1,a2};int[] b={b0,b1,b2};foreach(var x in a){if(x==b0||x==b1||x==b2){if(s0<0)s0=x; else{s1=x;return;}}}}
    private static int GetOther(int a0,int a1,int a2,int s0,int s1){if(a0!=s0&&a0!=s1)return a0; if(a1!=s0&&a1!=s1)return a1; if(a2!=s0&&a2!=s1)return a2; return -1;}
    private static void AddQuadAsTris(List<int> dst,int q00,int q10,int q11,int q01){dst.Add(q00);dst.Add(q10);dst.Add(q11); dst.Add(q00);dst.Add(q11);dst.Add(q01);}

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
        List<int> dstIndices,
        string rendererName,
        int subMeshIndex,
        string materialName,
        string textureName)
    {
        var debugCandidates = new List<FourPointDebugCandidate>();
        TrimStats stats = new TrimStats();
        var triangleResults = new List<TriangleTrimResult>(srcIndices.Length / 3);
        var edgeCuts = new List<EdgeCutInfo>();
        EdgeCrossingCache cache = new EdgeCrossingCache();
        int trianglesWith0BoundaryPoints = 0;
        int trianglesWith1BoundaryPoint = 0;
        int trianglesWith2BoundaryPoints = 0;
        int trianglesWith3BoundaryPoints = 0;
        int trianglesWith4BoundaryPoints = 0;
        int trianglesWith5OrMoreBoundaryPoints = 0;
        int maxBoundaryPointsOnTriangle = 0;

        // Prepass: generate and record all direct edge intersections first.
        for (int i = 0; i < srcIndices.Length; i += 3)
        {
            int triIndex = i / 3;
            int i0 = srcIndices[i];
            int i1 = srcIndices[i + 1];
            int i2 = srcIndices[i + 2];
            bool in0 = AlphaMaskProcessor.SampleMask(maskData, uv[i0]);
            bool in1 = AlphaMaskProcessor.SampleMask(maskData, uv[i1]);
            bool in2 = AlphaMaskProcessor.SampleMask(maskData, uv[i2]);
            int insideCount = (in0 ? 1 : 0) + (in1 ? 1 : 0) + (in2 ? 1 : 0);

            int[] idx = { i0, i1, i2 };
            bool[] inside = { in0, in1, in2 };
            EnsureEdgeCrossings(new EdgeKey(i0, i1), maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
            EnsureEdgeCrossings(new EdgeKey(i1, i2), maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
            EnsureEdgeCrossings(new EdgeKey(i2, i0), maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);

            if (insideCount == 1)
            {
                int insideV = -1, outA = -1, outB = -1;
                for (int k = 0; k < 3; k++)
                {
                    if (inside[k]) insideV = idx[k];
                    else if (outA < 0) outA = idx[k];
                    else outB = idx[k];
                }
                int cutA;
                bool hasCutA = TryGetSingleIntersection(insideV, outA, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources, cache, ref stats, out cutA);
                int cutB;
                bool hasCutB = TryGetSingleIntersection(insideV, outB, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources, cache, ref stats, out cutB);
                if (hasCutA) AddEdgeCut(edgeCuts, triIndex, insideV, outA, cutA, uv);
                if (hasCutB) AddEdgeCut(edgeCuts, triIndex, insideV, outB, cutB, uv);
            }
            else if (insideCount == 2)
            {
                int outsideV = -1, inA = -1, inB = -1;
                for (int k = 0; k < 3; k++)
                {
                    if (!inside[k]) outsideV = idx[k];
                    else if (inA < 0) inA = idx[k];
                    else inB = idx[k];
                }
                int cutA;
                bool hasCutA = TryGetSingleIntersection(inA, outsideV, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources, cache, ref stats, out cutA);
                int cutB;
                bool hasCutB = TryGetSingleIntersection(inB, outsideV, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources, cache, ref stats, out cutB);
                if (hasCutA) AddEdgeCut(edgeCuts, triIndex, inA, outsideV, cutA, uv);
                if (hasCutB) AddEdgeCut(edgeCuts, triIndex, inB, outsideV, cutB, uv);
            }
        }

        for (int i = 0; i < srcIndices.Length; i += 3)
        {
            int triIndex = i / 3;
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
            Vector2 s01025 = Vector2.Lerp(uv[i0], uv[i1], 0.25f);
            Vector2 s01050 = Vector2.Lerp(uv[i0], uv[i1], 0.5f);
            Vector2 s01075 = Vector2.Lerp(uv[i0], uv[i1], 0.75f);
            Vector2 s12025 = Vector2.Lerp(uv[i1], uv[i2], 0.25f);
            Vector2 s12050 = Vector2.Lerp(uv[i1], uv[i2], 0.5f);
            Vector2 s12075 = Vector2.Lerp(uv[i1], uv[i2], 0.75f);
            Vector2 s20025 = Vector2.Lerp(uv[i2], uv[i0], 0.25f);
            Vector2 s20050 = Vector2.Lerp(uv[i2], uv[i0], 0.5f);
            Vector2 s20075 = Vector2.Lerp(uv[i2], uv[i0], 0.75f);
            Vector2 centroid = (uv[i0] + uv[i1] + uv[i2]) / 3f;
            bool m01In = AlphaMaskProcessor.SampleMask(maskData, m01);
            bool m12In = AlphaMaskProcessor.SampleMask(maskData, m12);
            bool m20In = AlphaMaskProcessor.SampleMask(maskData, m20);
            bool centroidIn = AlphaMaskProcessor.SampleMask(maskData, centroid);
            var boundaryPoints = CollectBoundaryPointsForTriangle(i0, i1, i2, maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
            int boundaryPointsDetected = boundaryPoints.Count;
            if (boundaryPointsDetected == 0) trianglesWith0BoundaryPoints++;
            else if (boundaryPointsDetected == 1) trianglesWith1BoundaryPoint++;
            else if (boundaryPointsDetected == 2) trianglesWith2BoundaryPoints++;
            else if (boundaryPointsDetected == 3) trianglesWith3BoundaryPoints++;
            else if (boundaryPointsDetected == 4) trianglesWith4BoundaryPoints++;
            else trianglesWith5OrMoreBoundaryPoints++;
            if (boundaryPointsDetected > maxBoundaryPointsOnTriangle)
            {
                maxBoundaryPointsOnTriangle = boundaryPointsDetected;
            }
            if (boundaryPointsDetected >= 4)
            {
                stats.trianglesWithFourOrMoreBoundaryPoints++;
            }

            int insideCount = (in0 ? 1 : 0) + (in1 ? 1 : 0) + (in2 ? 1 : 0);

            bool attemptedTwoLine = false;
            bool twoLineSucceeded = false;
            bool fellBackToLegacySingleCut = false;
            bool legacySingleCutSucceeded = false;
            if (boundaryPointsDetected == 4)
            {
                attemptedTwoLine = true;
                if (TryProcessFourBoundaryTriangle(i0, i1, i2, triIndex, boundaryPoints, maskData, trimmer, vertices, uv, dstIndices, ref stats, debugCandidates, rendererName, subMeshIndex, materialName, textureName))
                {
                    twoLineSucceeded = true;
                    if (trimmer.debugFourPointClipDetails) Debug.Log($"[NDMF VRoid Mesh Trimmer][4pt-debug] triangleIndex={triIndex}, attemptedTwoLine={attemptedTwoLine}, twoLineSucceeded={twoLineSucceeded}, fellBackToLegacySingleCut={fellBackToLegacySingleCut}, legacySingleCutSucceeded={legacySingleCutSucceeded}, finalPath=twoLine");
                    stats.finalPathTwoLineCount++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.5f, 2));
                    continue;
                }
                fellBackToLegacySingleCut = true;
                stats.fallbackToLegacySingleCutCount++;
                stats.legacySingleCutAttemptCount++;
                if (TryProcessLegacySingleCutFromFourBoundary(i0, i1, i2, boundaryPoints, insideCount, maskData, trimmer, vertices, uv, dstIndices, ref stats))
                {
                    legacySingleCutSucceeded = true;
                    stats.legacySingleCutSucceededAfterTwoLineFailureCount++;
                    if (trimmer.debugFourPointClipDetails) Debug.Log($"[NDMF VRoid Mesh Trimmer][4pt-debug] triangleIndex={triIndex}, attemptedTwoLine={attemptedTwoLine}, twoLineSucceeded={twoLineSucceeded}, fellBackToLegacySingleCut={fellBackToLegacySingleCut}, legacySingleCutSucceeded={legacySingleCutSucceeded}, finalPath=legacySingleCut");
                    stats.finalPathLegacySingleCutCount++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.5f, 1));
                    continue;
                }
                stats.legacySingleCutFailedCount++;
            }

            if (boundaryPointsDetected >= 4)
            {
                stats.fourPlusBoundaryFallbackCount++;
                stats.fourPointClipFallbackCount++;
                stats.fallbackToOldKeepDeleteCount++;
                if (boundaryPointsDetected == 4) stats.finalPathOldFallbackCount++;
                if (boundaryPointsDetected == 4 && trimmer.debugFourPointClipDetails) Debug.Log($"[NDMF VRoid Mesh Trimmer][4pt-debug] triangleIndex={triIndex}, attemptedTwoLine={attemptedTwoLine}, twoLineSucceeded={twoLineSucceeded}, fellBackToLegacySingleCut={fellBackToLegacySingleCut}, legacySingleCutSucceeded={legacySingleCutSucceeded}, finalPath=oldFallback");
                const int sampleCount = 13;
                int expandedInsideCount = CountExpandedInsideSamples(maskData, uv[i0], uv[i1], uv[i2], in0, in1, in2, centroidIn);
                bool keepFourPlusBoundaryFallback = expandedInsideCount >= (sampleCount * 0.5f);

                if (keepFourPlusBoundaryFallback)
                {
                    AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                    stats.fourPlusBoundaryKeepCount++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 0));
                }
                else
                {
                    stats.removedTriangles++;
                    stats.fourPlusBoundaryDeleteCount++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 0f, 0));
                }
                continue;
            }

            if (boundaryPointsDetected == 2 && TryProcessTwoBoundaryTriangle(i0, i1, i2, boundaryPoints, insideCount, maskData, trimmer, vertices, uv, dstIndices, ref stats))
            {
                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.5f, 1));
                continue;
            }

            if (insideCount == 3)
            {
                if (!m01In || !m12In || !m20In || !centroidIn)
                {
                    stats.allInsideButInteriorOutside++;
                }

                AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.StrongKeep, i0, i1, i2, uv, 1f, 0));
                continue;
            }

            if (insideCount == 0)
            {
                if (boundaryPointsDetected == 0)
                {
                    bool s01025In = AlphaMaskProcessor.SampleMask(maskData, s01025);
                    bool s01050In = AlphaMaskProcessor.SampleMask(maskData, s01050);
                    bool s01075In = AlphaMaskProcessor.SampleMask(maskData, s01075);
                    bool s12025In = AlphaMaskProcessor.SampleMask(maskData, s12025);
                    bool s12050In = AlphaMaskProcessor.SampleMask(maskData, s12050);
                    bool s12075In = AlphaMaskProcessor.SampleMask(maskData, s12075);
                    bool s20025In = AlphaMaskProcessor.SampleMask(maskData, s20025);
                    bool s20050In = AlphaMaskProcessor.SampleMask(maskData, s20050);
                    bool s20075In = AlphaMaskProcessor.SampleMask(maskData, s20075);

                    int sampleCount = 13;
                    int expandedInsideCount =
                        (in0 ? 1 : 0) + (in1 ? 1 : 0) + (in2 ? 1 : 0) +
                        (s01025In ? 1 : 0) + (s01050In ? 1 : 0) + (s01075In ? 1 : 0) +
                        (s12025In ? 1 : 0) + (s12050In ? 1 : 0) + (s12075In ? 1 : 0) +
                        (s20025In ? 1 : 0) + (s20050In ? 1 : 0) + (s20075In ? 1 : 0) +
                        (centroidIn ? 1 : 0);

                    bool keepZeroBoundary;
                    if (expandedInsideCount == 0) keepZeroBoundary = false;
                    else if (expandedInsideCount == sampleCount) keepZeroBoundary = true;
                    else if (expandedInsideCount <= 1) keepZeroBoundary = false;
                    else if (expandedInsideCount >= sampleCount - 1) keepZeroBoundary = true;
                    else keepZeroBoundary = ((float)expandedInsideCount / sampleCount) >= 0.5f;

                    if (keepZeroBoundary)
                    {
                        AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                        stats.zeroBoundaryKeepCount++;
                        if (expandedInsideCount > 1 && expandedInsideCount < sampleCount - 1)
                        {
                            stats.zeroBoundaryMixedKeepCount++;
                        }
                        triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 0));
                    }
                    else
                    {
                        stats.removedTriangles++;
                        stats.zeroBoundaryDeleteCount++;
                        if (expandedInsideCount == 1 && centroidIn)
                        {
                            stats.centroidOnlyDeletedCount++;
                        }
                        if (expandedInsideCount > 1 && expandedInsideCount < sampleCount - 1)
                        {
                            stats.zeroBoundaryMixedDeleteCount++;
                        }
                        triangleResults.Add(BuildResult(triIndex, TriangleTrimState.StrongTrim, i0, i1, i2, uv, 0f, 0));
                    }
                    continue;
                }

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
                        triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 0));
                    }
                    else
                    {
                        stats.removedTriangles++;
                        triangleResults.Add(BuildResult(triIndex, TriangleTrimState.StrongTrim, i0, i1, i2, uv, 0f, 0));
                    }
                    continue;
                }

                if (edgeInsideCount == 1)
                {
                    if (centroidIn)
                    {
                        AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                        stats.singleEdgeMidpointAndCentroidInsidePreserved++;
                        triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 1));
                    }
                    else
                    {
                        stats.removedTriangles++;
                        stats.singleEdgeMidpointInsideDiscarded++;
                        triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 0f, 1));
                    }
                    continue;
                }

                if (edgeInsideCount == 3)
                {
                    AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                    stats.allEdgeMidpointsInsidePreserved++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 3));
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
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 2));
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
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 2));
                    continue;
                }

                AddEdgeCut(edgeCuts, triIndex, edgeA1, edgeB1, cutA, uv);
                AddEdgeCut(edgeCuts, triIndex, edgeA2, edgeB2, cutB, uv);
                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, virtualInside, cutA, cutB, vertices, uv, trimmer, ref stats);
                stats.twoEdgeMidpointsInsideClipped++;
                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.5f, 2));
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

                int cutA;
                bool hasCutA = TryGetSingleIntersection(insideV, outA, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources,
                    cache, ref stats, out cutA);

                int cutB;
                bool hasCutB = TryGetSingleIntersection(insideV, outB, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources,
                    cache, ref stats, out cutB);
                if (!hasCutA || !hasCutB)
                {
                    AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                    stats.fallbackPreserved++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 0));
                    continue;
                }
                AddEdgeCut(edgeCuts, triIndex, insideV, outA, cutA, uv);
                AddEdgeCut(edgeCuts, triIndex, insideV, outB, cutB, uv);
                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, insideV, cutA, cutB, vertices, uv, trimmer, ref stats);
                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.33f, 2));
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

                int cutA;
                bool hasCutA = TryGetSingleIntersection(inA, outsideV, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources,
                    cache, ref stats, out cutA);

                int cutB;
                bool hasCutB = TryGetSingleIntersection(inB, outsideV, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources,
                    cache, ref stats, out cutB);

                if (!hasCutA || !hasCutB)
                {
                    AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                    stats.fallbackPreserved++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 0));
                    continue;
                }
                AddEdgeCut(edgeCuts, triIndex, inA, outsideV, cutA, uv);
                AddEdgeCut(edgeCuts, triIndex, inB, outsideV, cutB, uv);
                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, inA, inB, cutA, vertices, uv, trimmer, ref stats);
                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, inB, cutB, cutA, vertices, uv, trimmer, ref stats);
                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.66f, 2));
            }
        }

        // Fill extended per-triangle metadata from collected cut records.
        var cutPointsByTri = new Dictionary<int, List<int>>();
        for (int i = 0; i < edgeCuts.Count; i++)
        {
            var e = edgeCuts[i];
            if (!cutPointsByTri.TryGetValue(e.triangleIndex, out var list))
            {
                list = new List<int>(2);
                cutPointsByTri[e.triangleIndex] = list;
            }
            if (!list.Contains(e.cutPointIndex)) list.Add(e.cutPointIndex);
        }
        for (int i = 0; i < triangleResults.Count; i++)
        {
            var r = triangleResults[i];
            int triBase = r.triangleIndex * 3;
            if (triBase >= 0 && triBase + 2 < srcIndices.Length)
            {
                int ti0 = srcIndices[triBase];
                int ti1 = srcIndices[triBase + 1];
                int ti2 = srcIndices[triBase + 2];
                Vector2 triCentroid = (uv[ti0] + uv[ti1] + uv[ti2]) / 3f;
                r.keptRegionCentroidUv = triCentroid;
                r.removedRegionCentroidUv = triCentroid;
            }
            if (cutPointsByTri.TryGetValue(r.triangleIndex, out var cps))
            {
                r.cutPoints = cps.ToArray();
                r.keptRegionContactEdges = cps.Count;
                r.removedRegionContactEdges = cps.Count;
                if (cps.Count >= 2 && triBase >= 0 && triBase + 2 < srcIndices.Length)
                {
                    int ti0 = srcIndices[triBase];
                    int ti1 = srcIndices[triBase + 1];
                    int ti2 = srcIndices[triBase + 2];
                    Vector2 cp0 = uv[cps[0]];
                    Vector2 cp1 = uv[cps[1]];
                    // Approximate split centroids for clipped-triangle metadata.
                    r.keptRegionCentroidUv = (uv[ti0] + cp0 + cp1) / 3f;
                    r.removedRegionCentroidUv = (uv[ti1] + uv[ti2] + cp0 + cp1) * 0.25f;
                }
            }
            else
            {
                r.keptRegionContactEdges = r.cutEdges;
                r.removedRegionContactEdges = r.cutEdges;
            }
            if (r.state == TriangleTrimState.StrongTrim) r.generatedTriangles = 0;
            else if (r.state == TriangleTrimState.StrongKeep) r.generatedTriangles = 1;
            else if (r.state == TriangleTrimState.Clipped) r.generatedTriangles = r.keptAreaRatio > 0.5f ? 2 : 1;
            else if (r.state == TriangleTrimState.Ambiguous) r.generatedTriangles = r.keptAreaRatio > 0f ? 1 : 0;
            triangleResults[i] = r;
        }

        // Backfill edge kept/removed side centroids from triangle metadata for continuity scoring/diagnostics.
        var triByIndex = new Dictionary<int, TriangleTrimResult>(triangleResults.Count);
        for (int i = 0; i < triangleResults.Count; i++)
        {
            triByIndex[triangleResults[i].triangleIndex] = triangleResults[i];
        }
        for (int i = 0; i < edgeCuts.Count; i++)
        {
            var e = edgeCuts[i];
            if (!triByIndex.TryGetValue(e.triangleIndex, out var tr)) continue;
            // Keep edge-side ownership from cut-generation phase (edgeA=kept side convention).
            // Only refresh centroid hints for diagnostics/scoring.
            e.keptSideCentroidUv = tr.keptRegionCentroidUv;
            e.removedSideCentroidUv = tr.removedRegionCentroidUv;
            edgeCuts[i] = e;
        }
        stats.outputTriangles = dstIndices.Count / 3;
        int edgesWith0Crossings = 0;
        int edgesWith1Crossing = 0;
        int edgesWith2Crossings = 0;
        int maxCrossingsOnEdge = 0;
        foreach (var kv in cache.Entries)
        {
            int crossingCount = kv.Value.Count;
            if (crossingCount == 0) edgesWith0Crossings++;
            else if (crossingCount == 1) edgesWith1Crossing++;
            else if (crossingCount == 2) edgesWith2Crossings++;
            if (crossingCount > maxCrossingsOnEdge) maxCrossingsOnEdge = crossingCount;
        }

        Debug.Log($"[NDMF VRoid Mesh Trimmer] EdgeCrossingCache created count={cache.CreatedCount}, hit count={cache.HitCount}, shared edge crossings reused count={cache.ReusedCount}, edges with 0 crossings={edgesWith0Crossings}, edges with 1 crossing={edgesWith1Crossing}, edges with 2 crossings={edgesWith2Crossings}, max crossings on edge={maxCrossingsOnEdge}, triangles with 4+ boundary points detected={stats.trianglesWithFourOrMoreBoundaryPoints}");
        Debug.Log($"[NDMF VRoid Mesh Trimmer] Triangle boundary points summary: triangles with 0 boundary points={trianglesWith0BoundaryPoints}, triangles with 1 boundary point={trianglesWith1BoundaryPoint}, triangles with 2 boundary points={trianglesWith2BoundaryPoints}, triangles with 3 boundary points={trianglesWith3BoundaryPoints}, triangles with 4 boundary points={trianglesWith4BoundaryPoints}, triangles with 5+ boundary points={trianglesWith5OrMoreBoundaryPoints}, max boundary points on triangle={maxBoundaryPointsOnTriangle}");
        Debug.Log($"[NDMF VRoid Mesh Trimmer] Zero-boundary sampling summary: zero-boundary keep count={stats.zeroBoundaryKeepCount}, zero-boundary delete count={stats.zeroBoundaryDeleteCount}, centroid-only deleted count={stats.centroidOnlyDeletedCount}, zero-boundary mixed keep count={stats.zeroBoundaryMixedKeepCount}, zero-boundary mixed delete count={stats.zeroBoundaryMixedDeleteCount}");
        Debug.Log($"[NDMF VRoid Mesh Trimmer] Two-boundary summary: two-boundary processed count={stats.twoBoundaryProcessedCount}, two-boundary kept region count={stats.twoBoundaryKeptRegionCount}, two-boundary generated triangles count={stats.twoBoundaryGeneratedTrianglesCount}, two-boundary fallback count={stats.twoBoundaryFallbackCount}");
        Debug.Log($"[NDMF VRoid Mesh Trimmer] Four-plus-boundary fallback summary: four-plus-boundary fallback count={stats.fourPlusBoundaryFallbackCount}, four-plus-boundary keep count={stats.fourPlusBoundaryKeepCount}, four-plus-boundary delete count={stats.fourPlusBoundaryDeleteCount}");
        Debug.Log($"[NDMF VRoid Mesh Trimmer] 4-point clip summary: triangles with 4 boundary points={trianglesWith4BoundaryPoints}, 4-point clip success count={stats.fourPointClipSuccessCount}, 4-point clip fallback count={stats.fourPointClipFallbackCount}, generated triangles by 4-point clip={stats.fourPointClipGeneratedTriangles}, four-point clip generated zero fallback count={stats.fourPointClipGeneratedZeroFallbackCount}, four-point generated triangle inside count={stats.fourPointGeneratedTriangleInsideCount}, four-point generated triangle outside count={stats.fourPointGeneratedTriangleOutsideCount}, four-point reversed-side suspected count={stats.fourPointReversedSideSuspectedCount}, four-point fallback due to invalid sample count={stats.fourPointFallbackDueToInvalidSampleCount}, rescued single-midpoint count={stats.singleEdgeMidpointAndCentroidInsidePreserved}");
        Debug.Log($"[NDMF VRoid Mesh Trimmer] 4-point pairing summary: four-point pairing candidates tested count={stats.fourPointPairingCandidatesTestedCount}, four-point pairing A selected count={stats.fourPointPairingASelectedCount}, four-point pairing B selected count={stats.fourPointPairingBSelectedCount}, four-point pairing C selected count={stats.fourPointPairingCSelectedCount}, four-point pairing rejected by same-edge-line count={stats.fourPointPairingRejectedBySameEdgeLineCount}, four-point pairing rejected by intersection count={stats.fourPointPairingRejectedByIntersectionCount}, four-point pairing rejected by degenerate count={stats.fourPointPairingRejectedByDegenerateCount}, four-point pairing fallback count={stats.fourPointPairingFallbackCount}, four-point best score={stats.fourPointBestScore}, four-point generated triangles count={stats.fourPointGeneratedTrianglesCount}, four-point kept triangles count={stats.fourPointKeptTrianglesCount}, four-point discarded triangles count={stats.fourPointDiscardedTrianglesCount}, twoEdgeTwoCrossings detected count={stats.twoEdgeTwoCrossingsDetectedCount}, twoEdgeTwoCrossings strip kept count={stats.twoEdgeTwoCrossingsStripKeptCount}, twoEdgeTwoCrossings strip discarded count={stats.twoEdgeTwoCrossingsStripDiscardedCount}, twoEdgeTwoCrossings interval mismatch count={stats.twoEdgeTwoCrossingsIntervalMismatchCount}, strip rejected by midpoint containment count={stats.twoEdgeTwoCrossingsStripRejectedByMidpointContainmentCount}, twoEdgeTwoCrossings alt outside used count={stats.twoEdgeTwoCrossingsAltOutsideUsedCount}, twoEdgeTwoCrossings outside direct output count={stats.twoEdgeTwoCrossingsOutsideDirectOutputCount}");
        Debug.Log($"[NDMF VRoid Mesh Trimmer] 4-point fallback path summary: fallback to legacy single-cut count={stats.fallbackToLegacySingleCutCount}, legacy single-cut attempt count={stats.legacySingleCutAttemptCount}, legacy single-cut succeeded after two-line failure count={stats.legacySingleCutSucceededAfterTwoLineFailureCount}, legacy single-cut failed count={stats.legacySingleCutFailedCount}, fallback to old keep/delete count={stats.fallbackToOldKeepDeleteCount}");
        Debug.Log($"[NDMF VRoid Mesh Trimmer] 4-point final path summary: finalPath=twoLine count={stats.finalPathTwoLineCount}, finalPath=legacySingleCut count={stats.finalPathLegacySingleCutCount}, finalPath=oldFallback count={stats.finalPathOldFallbackCount}");
        if (trimmer != null && trimmer.debugFourPointClipDetails && debugCandidates.Count > 0)
        {
            debugCandidates.Sort((a, b) => b.suspicion.CompareTo(a.suspicion));
            int maxCount = Mathf.Max(0, trimmer.debugFourPointClipMaxTriangles);
            int emit = Mathf.Min(maxCount, debugCandidates.Count);
            for (int i = 0; i < emit; i++) Debug.Log(debugCandidates[i].logBlock);
        }
        else if (trimmer != null && trimmer.debugFourPointClipDetails)
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][4pt-debug] No debug candidates emitted. Renderer={rendererName}, SubMesh={subMeshIndex}, Material={materialName}, Filter={trimmer.debugFourPointMaterialFilter}, TrianglesWith4BoundaryPoints={trianglesWith4BoundaryPoints}");
        }
        return stats;
    }

    private static int CountExpandedInsideSamples(
        AlphaMaskProcessor.AlphaMaskData maskData,
        Vector2 uv0,
        Vector2 uv1,
        Vector2 uv2,
        bool in0,
        bool in1,
        bool in2,
        bool centroidIn)
    {
        bool s01025In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv0, uv1, 0.25f));
        bool s01050In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv0, uv1, 0.5f));
        bool s01075In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv0, uv1, 0.75f));
        bool s12025In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv1, uv2, 0.25f));
        bool s12050In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv1, uv2, 0.5f));
        bool s12075In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv1, uv2, 0.75f));
        bool s20025In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv2, uv0, 0.25f));
        bool s20050In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv2, uv0, 0.5f));
        bool s20075In = AlphaMaskProcessor.SampleMask(maskData, Vector2.Lerp(uv2, uv0, 0.75f));

        return
            (in0 ? 1 : 0) + (in1 ? 1 : 0) + (in2 ? 1 : 0) +
            (s01025In ? 1 : 0) + (s01050In ? 1 : 0) + (s01075In ? 1 : 0) +
            (s12025In ? 1 : 0) + (s12050In ? 1 : 0) + (s12075In ? 1 : 0) +
            (s20025In ? 1 : 0) + (s20050In ? 1 : 0) + (s20075In ? 1 : 0) +
            (centroidIn ? 1 : 0);
    }

    private static List<BoundaryPoint> CollectBoundaryPointsForTriangle(
        int i0,
        int i1,
        int i2,
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
        EdgeCrossingCache cache,
        ref TrimStats stats)
    {
        var boundaryPoints = new List<BoundaryPoint>();
        AddBoundaryPointsForEdge(boundaryPoints, 0, i0, i1, maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
        AddBoundaryPointsForEdge(boundaryPoints, 1, i1, i2, maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
        AddBoundaryPointsForEdge(boundaryPoints, 2, i2, i0, maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
        boundaryPoints.Sort((x, y) => x.perimeterOrder.CompareTo(y.perimeterOrder));
        return boundaryPoints;
    }

    private static void AddBoundaryPointsForEdge(
        List<BoundaryPoint> boundaryPoints,
        int edgeIndex,
        int edgeA,
        int edgeB,
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
        EdgeCrossingCache cache,
        ref TrimStats stats)
    {
        var orderedCrossings = GetCrossingsForTriangleEdge(edgeA, edgeB, maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
            hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
        var edgeKey = new EdgeKey(edgeA, edgeB);
        bool localMatchesCanonical = edgeA == edgeKey.a;

        foreach (var crossing in orderedCrossings)
        {
            float localTOnEdge = localMatchesCanonical ? crossing.tCanonical : 1f - crossing.tCanonical;
            boundaryPoints.Add(new BoundaryPoint
            {
                edgeIndex = edgeIndex,
                localTOnEdge = localTOnEdge,
                crossing = crossing,
                vertexIndex = crossing.vertexIndex,
                uv = crossing.uv,
                perimeterOrder = edgeIndex + localTOnEdge
            });
        }
    }


    private static long MakeEdgeKey(int a, int b)
    {
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);
        return ((long)lo << 32) | (uint)hi;
    }

    private static void AddEdgeCut(List<EdgeCutInfo> edgeCuts, int triangleIndex, int edgeA, int edgeB, int cutPointIndex, List<Vector2> uv)
    {
        if (cutPointIndex < 0 || cutPointIndex >= uv.Count) return;
        long key = MakeEdgeKey(edgeA, edgeB);
        for (int i = 0; i < edgeCuts.Count; i++)
        {
            if (edgeCuts[i].triangleIndex == triangleIndex && edgeCuts[i].edgeKey == key)
            {
                return;
            }
        }

        edgeCuts.Add(new EdgeCutInfo
        {
            edgeKey = key,
            triangleIndex = triangleIndex,
            cutPointIndex = cutPointIndex,
            cutPointUv = uv[cutPointIndex],
            edgeA = edgeA,
            edgeB = edgeB,
            keptSideCentroidUv = uv[edgeA],
            removedSideCentroidUv = uv[edgeB],
            keptSideUsesEdgeA = true
        });
    }

    private static TriangleTrimResult BuildResult(int triangleIndex, TriangleTrimState state, int i0, int i1, int i2, List<Vector2> uv, float keptAreaRatio, int cutEdges)
    {
        float area = Mathf.Abs(SignedArea(uv[i0], uv[i1], uv[i2])) * 0.5f;
        float kept = area * Mathf.Clamp01(keptAreaRatio);
        float removed = Mathf.Max(0f, area - kept);
        return new TriangleTrimResult
        {
            triangleIndex = triangleIndex,
            state = state,
            originalAreaUv = area,
            keptAreaUv = kept,
            removedAreaUv = removed,
            keptAreaRatio = area > 0f ? kept / area : 0f,
            removedAreaRatio = area > 0f ? removed / area : 0f,
            cutEdges = cutEdges,
            cutPoints = Array.Empty<int>(),
            keptRegionCentroidUv = (uv[i0] + uv[i1] + uv[i2]) / 3f,
            removedRegionCentroidUv = (uv[i0] + uv[i1] + uv[i2]) / 3f,
            keptRegionContactEdges = cutEdges,
            removedRegionContactEdges = cutEdges,
            generatedTriangles = state == TriangleTrimState.Clipped ? 1 : (state == TriangleTrimState.StrongKeep ? 1 : 0)
        };
    }

    private static bool TryGetSingleIntersection(
        int edgeA,
        int edgeB,
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
        EdgeCrossingCache cache,
        ref TrimStats stats,
        out int cutPointIndex)
    {
        cutPointIndex = -1;
        var crossings = GetCrossingsForTriangleEdge(edgeA, edgeB, maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
            hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
        if (crossings.Count != 1) return false;
        cutPointIndex = crossings[0].vertexIndex;
        return true;
    }

    private static List<EdgeCrossing> GetCrossingsForTriangleEdge(
        int edgeA,
        int edgeB,
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
        EdgeCrossingCache cache,
        ref TrimStats stats)
    {
        var edgeKey = new EdgeKey(edgeA, edgeB);
        var canonical = EnsureEdgeCrossings(edgeKey, maskData, trimmer, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
            hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, cache, ref stats);
        if (edgeA == edgeKey.a)
        {
            return canonical;
        }

        var reversed = new List<EdgeCrossing>(canonical.Count);
        for (int i = canonical.Count - 1; i >= 0; i--)
        {
            reversed.Add(canonical[i]);
        }
        return reversed;
    }

    private static List<EdgeCrossing> EnsureEdgeCrossings(
        EdgeKey edgeKey,
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
        EdgeCrossingCache cache,
        ref TrimStats stats)
    {
        var crossings = cache.GetOrCreateEdgeCrossings(edgeKey);
        if (crossings.Count > 0) return crossings;

        int canonicalStart = edgeKey.a;
        int canonicalEnd = edgeKey.b;
        Vector2 uvStart = uv[canonicalStart];
        Vector2 uvEnd = uv[canonicalEnd];
        int segmentCount = Mathf.Max(1, trimmer.edgeSampleSegments);
        int maxCrossingsPerEdge = Mathf.Max(1, trimmer.maxCrossingsPerEdge);
        float step = 1f / segmentCount;
        var sampleInside = new bool[segmentCount + 1];
        for (int s = 0; s <= segmentCount; s++)
        {
            float t = s * step;
            sampleInside[s] = AlphaMaskProcessor.SampleMask(maskData, Vector2.LerpUnclamped(uvStart, uvEnd, t));
        }

        for (int s = 0; s < segmentCount; s++)
        {
            bool fromInside = sampleInside[s];
            bool toInside = sampleInside[s + 1];
            if (fromInside == toInside) continue;

            float lo = s * step;
            float hi = (s + 1) * step;
            for (int i = 0; i < 10; i++)
            {
                float mid = (lo + hi) * 0.5f;
                Vector2 uvMid = Vector2.LerpUnclamped(uvStart, uvEnd, mid);
                bool midInside = AlphaMaskProcessor.SampleMask(maskData, uvMid);
                if (midInside == fromInside) lo = mid;
                else hi = mid;
            }

            float tCanonical = lo;
            int newIndex;
            if (tCanonical <= trimmer.minIntersectionT) newIndex = canonicalStart;
            else if (tCanonical >= trimmer.maxIntersectionT) newIndex = canonicalEnd;
            else
            {
                newIndex = AddInterpolatedVertex(canonicalStart, canonicalEnd, tCanonical, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref stats);
            }

            crossings.Add(new EdgeCrossing
            {
                tCanonical = tCanonical,
                vertexIndex = newIndex,
                uv = uv[newIndex],
                vertexData = BuildVertexData(newIndex, vertices, normals, tangents, colors, boneWeights, hasNormals, hasTangents, hasColors, hasBoneWeights),
                weightedSource = vertexSources[newIndex]
            });
        }

        crossings.Sort((x, y) => x.tCanonical.CompareTo(y.tCanonical));
        if (crossings.Count > maxCrossingsPerEdge)
        {
            Debug.LogWarning($"[NDMF VRoid Mesh Trimmer] Edge {edgeKey.a}-{edgeKey.b} detected {crossings.Count} crossings. Limiting to {maxCrossingsPerEdge}.");
            crossings.RemoveRange(maxCrossingsPerEdge, crossings.Count - maxCrossingsPerEdge);
        }

        return crossings;
    }

    private static VertexData BuildVertexData(
        int index,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector4> tangents,
        List<Color> colors,
        List<BoneWeight> boneWeights,
        bool hasNormals,
        bool hasTangents,
        bool hasColors,
        bool hasBoneWeights)
    {
        return new VertexData
        {
            position = vertices[index],
            normal = hasNormals ? normals[index] : Vector3.zero,
            tangent = hasTangents ? tangents[index] : Vector4.zero,
            color = hasColors ? colors[index] : Color.white,
            boneWeight = hasBoneWeights ? boneWeights[index] : default
        };
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


    private static bool TryProcessTwoBoundaryTriangle(
        int i0,
        int i1,
        int i2,
        List<BoundaryPoint> boundaryPoints,
        int insideCount,
        AlphaMaskProcessor.AlphaMaskData maskData,
        NDMFVRoidMeshTrimmer trimmer,
        List<Vector3> vertices,
        List<Vector2> uv,
        List<int> dstIndices,
        ref TrimStats stats)
    {
        if (boundaryPoints == null || boundaryPoints.Count != 2) return false;
        var tri = new int[] { i0, i1, i2 };
        BoundaryPoint p0 = boundaryPoints[0];
        BoundaryPoint p1 = boundaryPoints[1];
        int e0 = p0.edgeIndex;
        int e1 = p1.edgeIndex;
        if (e0 < 0 || e0 > 2 || e1 < 0 || e1 > 2 || e0 == e1)
        {
            stats.twoBoundaryFallbackCount++;
            return false;
        }

        List<int> BuildRegion(int startEdge, int endEdge, int startPoint, int endPoint)
        {
            var poly = new List<int>(4) { startPoint };
            int edge = startEdge;
            while (true)
            {
                poly.Add(tri[(edge + 1) % 3]);
                if (edge == endEdge) break;
                edge = (edge + 1) % 3;
            }
            poly.Add(endPoint);
            return poly;
        }

        var regionA = BuildRegion(e0, e1, p0.vertexIndex, p1.vertexIndex);
        var regionB = BuildRegion(e1, e0, p1.vertexIndex, p0.vertexIndex);

        bool keepA = AlphaMaskProcessor.SampleMask(maskData, AverageUv(regionA, uv));
        bool keepB = AlphaMaskProcessor.SampleMask(maskData, AverageUv(regionB, uv));
        if (!keepA && !keepB)
        {
            if (insideCount > 0) keepA = true;
            else return true;
        }

        stats.twoBoundaryProcessedCount++;
        int before = dstIndices.Count / 3;
        if (keepA) { TriangulateRegion(i0, i1, i2, regionA, vertices, uv, trimmer, dstIndices, ref stats); stats.twoBoundaryKeptRegionCount++; }
        if (keepB) { TriangulateRegion(i0, i1, i2, regionB, vertices, uv, trimmer, dstIndices, ref stats); stats.twoBoundaryKeptRegionCount++; }
        stats.twoBoundaryGeneratedTrianglesCount += (dstIndices.Count / 3) - before;
        return true;
    }


    private static bool TryProcessFourBoundaryTriangle(
        int i0, int i1, int i2, int triangleIndex, List<BoundaryPoint> boundaryPoints, AlphaMaskProcessor.AlphaMaskData maskData, NDMFVRoidMeshTrimmer trimmer,
        List<Vector3> vertices, List<Vector2> uv, List<int> dstIndices, ref TrimStats stats, List<FourPointDebugCandidate> debugCandidates, string rendererName, int subMeshIndex, string materialName, string textureName)
    {
        if (boundaryPoints == null || boundaryPoints.Count != 4) return false;
        boundaryPoints.Sort((a, b) => a.perimeterOrder.CompareTo(b.perimeterOrder));
        if (TryProcessTwoEdgeTwoCrossings(i0, i1, i2, triangleIndex, boundaryPoints, maskData, trimmer, vertices, uv, dstIndices, ref stats, debugCandidates, materialName))
        {
            return true;
        }
        int[][] pairings =
        {
            new[] { 0, 1, 2, 3 }, // A
            new[] { 3, 0, 1, 2 }, // B (p0-p3, p1-p2)
            new[] { 0, 2, 1, 3 }  // C (p0-p2, p1-p3)
        };
        float bestScore = float.NegativeInfinity;
        int bestPairingIndex = -1;
        var bestAccepted = new List<int>();
        int bestInside = 0;
        int bestOutside = 0;
        string[] candidateReasons = { "notTested", "notTested", "notTested" };
        float[] candidateScores = { float.NaN, float.NaN, float.NaN };

        for (int pairIdx = 0; pairIdx < pairings.Length; pairIdx++)
        {
            var pairing = pairings[pairIdx];
            stats.fourPointPairingCandidatesTestedCount++;
            var p0 = boundaryPoints[pairing[0]];
            var p1 = boundaryPoints[pairing[1]];
            var p2 = boundaryPoints[pairing[2]];
            var p3 = boundaryPoints[pairing[3]];
            float lenA = (uv[p0.vertexIndex] - uv[p1.vertexIndex]).magnitude;
            float lenB = (uv[p2.vertexIndex] - uv[p3.vertexIndex]).magnitude;
            if (p0.edgeIndex == p1.edgeIndex || p2.edgeIndex == p3.edgeIndex)
            {
                stats.fourPointPairingRejectedBySameEdgeLineCount++;
                candidateReasons[pairIdx] = "rejectedBySameEdgeLine";
                continue;
            }
            if (lenA <= 1e-6f || lenB <= 1e-6f)
            {
                stats.fourPointPairingRejectedByDegenerateCount++;
                candidateReasons[pairIdx] = "rejectedByZeroLengthLine";
                continue;
            }
            if (SegmentsIntersect(uv[p0.vertexIndex], uv[p1.vertexIndex], uv[p2.vertexIndex], uv[p3.vertexIndex]))
            {
                stats.fourPointPairingRejectedByIntersectionCount++;
                candidateReasons[pairIdx] = "rejectedByLineIntersection";
                continue;
            }
            BuildFourBoundaryRegions(i0, i1, i2, p0, p1, p2, p3, out var rA, out var rB, out var rMid);
            if (rA.Count < 3 || rB.Count < 3 || rMid.Count < 3)
            {
                stats.fourPointPairingRejectedByDegenerateCount++;
                candidateReasons[pairIdx] = "rejectedByDegenerate";
                continue;
            }
            bool keepA = EstimateKeep(maskData, rA, uv);
            bool keepB = EstimateKeep(maskData, rB, uv);
            bool keepMid = EstimateKeep(maskData, rMid, uv);
            if (!keepA && !keepB && !keepMid)
            {
                stats.fourPointPairingRejectedByDegenerateCount++;
                candidateReasons[pairIdx] = "rejectedByNoKeepRegion";
                continue;
            }
            var generatedIndices = new List<int>();
            if (keepA) TriangulateRegion(i0, i1, i2, rA, vertices, uv, trimmer, generatedIndices, ref stats);
            if (keepB) TriangulateRegion(i0, i1, i2, rB, vertices, uv, trimmer, generatedIndices, ref stats);
            if (keepMid) TriangulateRegion(i0, i1, i2, rMid, vertices, uv, trimmer, generatedIndices, ref stats);
            var acceptedTriangles = new List<int>();
            int insideTriCount = 0;
            int outsideTriCount = 0;
            float score = 0f;
            bool hasInvalidSampling = false;
            for (int t = 0; t + 2 < generatedIndices.Count; t += 3)
            {
                int a = generatedIndices[t];
                int b = generatedIndices[t + 1];
                int c = generatedIndices[t + 2];
                if (!TrySampleTriangleInsideRatio(maskData, uv[a], uv[b], uv[c], out float ratio))
                {
                    hasInvalidSampling = true;
                    continue;
                }
                float area = Mathf.Abs(SignedArea(uv[a], uv[b], uv[c])) * 0.5f;
                score += Mathf.Max(ratio, 1f - ratio) * area;
                if (ratio >= 0.5f)
                {
                    acceptedTriangles.Add(a);
                    acceptedTriangles.Add(b);
                    acceptedTriangles.Add(c);
                    insideTriCount++;
                }
                else
                {
                    outsideTriCount++;
                }
            }
            int generatedTriCount = generatedIndices.Count / 3;
            if (hasInvalidSampling || generatedTriCount <= 0 || acceptedTriangles.Count == 0)
            {
                if (hasInvalidSampling) stats.fourPointFallbackDueToInvalidSampleCount++;
                stats.fourPointPairingRejectedByDegenerateCount++;
                candidateReasons[pairIdx] = hasInvalidSampling ? "rejectedByInvalidSampleCount" : "rejectedByInvalidArea";
                continue;
            }

            float tieBreaker = -outsideTriCount * 1e-4f;
            float finalScore = score + tieBreaker;
            candidateReasons[pairIdx] = "accepted";
            candidateScores[pairIdx] = finalScore;
            if (finalScore > bestScore)
            {
                bestScore = finalScore;
                bestPairingIndex = pairIdx;
                bestAccepted = acceptedTriangles;
                bestInside = insideTriCount;
                bestOutside = outsideTriCount;
            }
        }
        if (bestPairingIndex < 0)
        {
            stats.fourPointPairingFallbackCount++;
            stats.fourPointClipGeneratedZeroFallbackCount++;
            TryAddFourPointDebugCandidate(trimmer, debugCandidates, materialName, 220f, $"[NDMF VRoid Mesh Trimmer][4pt-debug] Triangle={triangleIndex}, Renderer={rendererName}, SubMesh={subMeshIndex}, Material={materialName}, Texture={textureName}, fallbackUsed=true, fallbackReason=no valid pairing, bestScore=-inf, PairingA:reason={candidateReasons[0]},score={candidateScores[0]}, PairingB:reason={candidateReasons[1]},score={candidateScores[1]}, PairingC:reason={candidateReasons[2]},score={candidateScores[2]}");
            return false;
        }
        if (bestPairingIndex == 0) stats.fourPointPairingASelectedCount++;
        else if (bestPairingIndex == 1) stats.fourPointPairingBSelectedCount++;
        else if (bestPairingIndex == 2) stats.fourPointPairingCSelectedCount++;
        if (bestOutside > bestInside) stats.fourPointReversedSideSuspectedCount++;
        stats.fourPointBestScore = Mathf.Max(stats.fourPointBestScore, bestScore);
        stats.fourPointGeneratedTriangleInsideCount += bestInside;
        stats.fourPointGeneratedTriangleOutsideCount += bestOutside;
        stats.fourPointKeptTrianglesCount += bestInside;
        stats.fourPointDiscardedTrianglesCount += bestOutside;
        dstIndices.AddRange(bestAccepted);
        int generated = bestAccepted.Count / 3;
        stats.fourPointGeneratedTrianglesCount += generated;
        stats.fourPointClipSuccessCount++;
        stats.fourPointClipGeneratedTriangles += generated;
        string pairingName = bestPairingIndex == 0 ? "A" : (bestPairingIndex == 1 ? "B" : "C");
        float suspicion = (pairingName != "A" ? 20f : 0f) + (bestOutside == 0 ? 30f : 0f) + (bestInside == 0 ? 50f : 0f) + ((bestScore < 0.05f) ? 20f : 0f);
        string bp = "";
        for (int bi = 0; bi < boundaryPoints.Count; bi++)
        {
            var p = boundaryPoints[bi];
            int edgeA = (p.edgeIndex == 0) ? i0 : (p.edgeIndex == 1 ? i1 : i2);
            int edgeB = (p.edgeIndex == 0) ? i1 : (p.edgeIndex == 1 ? i2 : i0);
            bp += $" p{bi}:(edge={p.edgeIndex}, t={p.localTOnEdge:F4}, po={p.perimeterOrder:F4}, uv={p.uv}, vi={p.vertexIndex}, in={AlphaMaskProcessor.SampleMask(maskData, p.uv)}, edgeKey=({Math.Min(edgeA, edgeB)},{Math.Max(edgeA, edgeB)}), tc={p.crossing.tCanonical:F4})";
        }
        TryAddFourPointDebugCandidate(trimmer, debugCandidates, materialName, suspicion,
            $"[NDMF VRoid Mesh Trimmer][4pt-debug] Triangle={triangleIndex}, Renderer={rendererName}, SubMesh={subMeshIndex}, Material={materialName}, Texture={textureName}, selectedPairing={pairingName}, fallbackUsed=false, bestScore={bestScore:F6}, keptTriangles={bestInside}, discardedTriangles={bestOutside}, generatedTriangles={generated}, PairingA:reason={candidateReasons[0]},score={candidateScores[0]}, PairingB:reason={candidateReasons[1]},score={candidateScores[1]}, PairingC:reason={candidateReasons[2]},score={candidateScores[2]}.{bp}");
        return true;
    }

    private static bool TryProcessLegacySingleCutFromFourBoundary(
        int i0, int i1, int i2, List<BoundaryPoint> boundaryPoints, int insideCount,
        AlphaMaskProcessor.AlphaMaskData maskData, NDMFVRoidMeshTrimmer trimmer, List<Vector3> vertices, List<Vector2> uv, List<int> dstIndices, ref TrimStats stats)
    {
        if (boundaryPoints == null || boundaryPoints.Count < 2) return false;
        boundaryPoints.Sort((a, b) => a.perimeterOrder.CompareTo(b.perimeterOrder));
        var pairCandidates = new List<(BoundaryPoint a, BoundaryPoint b, float score)>();
        for (int i = 0; i < boundaryPoints.Count; i++)
        {
            for (int j = i + 1; j < boundaryPoints.Count; j++)
            {
                if (boundaryPoints[i].edgeIndex == boundaryPoints[j].edgeIndex) continue;
                float edgeSpan = Mathf.Abs(boundaryPoints[i].perimeterOrder - boundaryPoints[j].perimeterOrder);
                float uvDist = Vector2.Distance(boundaryPoints[i].uv, boundaryPoints[j].uv);
                pairCandidates.Add((boundaryPoints[i], boundaryPoints[j], edgeSpan + uvDist));
            }
        }
        pairCandidates.Sort((x, y) => y.score.CompareTo(x.score));
        for (int k = 0; k < pairCandidates.Count; k++)
        {
            var candidate = new List<BoundaryPoint> { pairCandidates[k].a, pairCandidates[k].b };
            if (TryProcessTwoBoundaryTriangle(i0, i1, i2, candidate, insideCount, maskData, trimmer, vertices, uv, dstIndices, ref stats))
            {
                if (trimmer != null && trimmer.debugFourPointClipDetails)
                {
                    Debug.Log($"[NDMF VRoid Mesh Trimmer][4pt-debug] legacySingleCut selected pair: edgeA={pairCandidates[k].a.edgeIndex}, edgeB={pairCandidates[k].b.edgeIndex}, uvA={pairCandidates[k].a.uv}, uvB={pairCandidates[k].b.uv}, score={pairCandidates[k].score:F6}");
                }
                return true;
            }
        }
        return false;
    }

    private static bool TryProcessTwoEdgeTwoCrossings(
        int i0, int i1, int i2, int triangleIndex, List<BoundaryPoint> boundaryPoints,
        AlphaMaskProcessor.AlphaMaskData maskData, NDMFVRoidMeshTrimmer trimmer,
        List<Vector3> vertices, List<Vector2> uv, List<int> dstIndices, ref TrimStats stats,
        List<FourPointDebugCandidate> debugCandidates, string materialName)
    {
        int[] counts = { 0, 0, 0 };
        for (int i = 0; i < boundaryPoints.Count; i++) counts[Mathf.Clamp(boundaryPoints[i].edgeIndex, 0, 2)]++;
        int edgeA = -1, edgeB = -1;
        for (int e = 0; e < 3; e++) if (counts[e] == 2) { if (edgeA < 0) edgeA = e; else edgeB = e; }
        if (edgeA < 0 || edgeB < 0) return false;
        stats.twoEdgeTwoCrossingsDetectedCount++;
        var edgeAPoints = boundaryPoints.FindAll(p => p.edgeIndex == edgeA);
        var edgeBPoints = boundaryPoints.FindAll(p => p.edgeIndex == edgeB);
        edgeAPoints.Sort((x, y) => x.localTOnEdge.CompareTo(y.localTOnEdge));
        edgeBPoints.Sort((x, y) => x.localTOnEdge.CompareTo(y.localTOnEdge));
        var a0 = edgeAPoints[0];
        var a1 = edgeAPoints[1];
        var b0 = edgeBPoints[0];
        var b1 = edgeBPoints[1];
        Vector2 aMid = (edgeAPoints[0].uv + edgeAPoints[1].uv) * 0.5f;
        Vector2 bMid = (edgeBPoints[0].uv + edgeBPoints[1].uv) * 0.5f;
        bool aIn = AlphaMaskProcessor.SampleMask(maskData, aMid);
        bool bIn = AlphaMaskProcessor.SampleMask(maskData, bMid);

        var strip1 = new List<int> { a0.vertexIndex, a1.vertexIndex, b1.vertexIndex, b0.vertexIndex };
        var strip2 = new List<int> { a0.vertexIndex, a1.vertexIndex, b0.vertexIndex, b1.vertexIndex };
        EvaluateStripCandidate(strip1, i0, i1, i2, vertices, uv, trimmer, maskData, aMid, bMid, out bool s1Valid, out string s1Reason, out float s1AreaUv, out float s1AreaWorld, out bool s1Intersect, out bool s1ContainsMidA, out bool s1ContainsMidB, out float s1InsideRatio, out List<int> s1KeptTriangles, out List<int> s1DiscardTriangles);
        EvaluateStripCandidate(strip2, i0, i1, i2, vertices, uv, trimmer, maskData, aMid, bMid, out bool s2Valid, out string s2Reason, out float s2AreaUv, out float s2AreaWorld, out bool s2Intersect, out bool s2ContainsMidA, out bool s2ContainsMidB, out float s2InsideRatio, out List<int> s2KeptTriangles, out List<int> s2DiscardTriangles);
        bool keepStripPreferred = aIn && bIn;
        bool discardStripPreferred = !aIn && !bIn;
        if (aIn != bIn)
        {
            stats.twoEdgeTwoCrossingsIntervalMismatchCount++;
        }

        List<int> selectedTriangles = null;
        List<int> selectedOutsideTriangles = null;
        List<int> selectedOutsideTrianglesAlt = null;
        string selectedStripCandidate = "none";
        if (s1Valid && (!s1ContainsMidA || !s1ContainsMidB)) { s1Valid = false; s1Reason = "rejectedByMidpointContainment"; stats.twoEdgeTwoCrossingsStripRejectedByMidpointContainmentCount++; }
        if (s2Valid && (!s2ContainsMidA || !s2ContainsMidB)) { s2Valid = false; s2Reason = "rejectedByMidpointContainment"; stats.twoEdgeTwoCrossingsStripRejectedByMidpointContainmentCount++; }
        if (s1Valid || s2Valid)
        {
            float s1Score = (s1ContainsMidA && s1ContainsMidB ? 1000f : 0f) + s1InsideRatio * 10f + s1AreaUv;
            float s2Score = (s2ContainsMidA && s2ContainsMidB ? 1000f : 0f) + s2InsideRatio * 10f + s2AreaUv;
            if (!s2Valid || (s1Valid && s1Score >= s2Score))
            {
                selectedTriangles = s1KeptTriangles;
                selectedOutsideTriangles = s1DiscardTriangles;
                selectedOutsideTrianglesAlt = s2DiscardTriangles;
                selectedStripCandidate = "Strip1";
            }
            else
            {
                selectedTriangles = s2KeptTriangles;
                selectedOutsideTriangles = s2DiscardTriangles;
                selectedOutsideTrianglesAlt = s1DiscardTriangles;
                selectedStripCandidate = "Strip2";
            }
        }

        if (keepStripPreferred)
        {
            if (selectedTriangles == null || selectedTriangles.Count == 0) return false;
            dstIndices.AddRange(selectedTriangles);
            stats.twoEdgeTwoCrossingsStripKeptCount++;
            TryAddFourPointDebugCandidate(trimmer, debugCandidates, materialName, 60f, $"[NDMF VRoid Mesh Trimmer][4pt-debug] Triangle={triangleIndex}, twoEdgeTwoCrossings=true, stripKept=true, mismatch={aIn!=bIn}, edgeA={edgeA}, edgeA points:a0(t={a0.localTOnEdge:F3},uv={a0.uv}) a1(t={a1.localTOnEdge:F3},uv={a1.uv}), edgeB={edgeB}, edgeB points:b0(t={b0.localTOnEdge:F3},uv={b0.uv}) b1(t={b1.localTOnEdge:F3},uv={b1.uv}), midA={aMid}, midAInside={aIn}, midB={bMid}, midBInside={bIn}, Strip1 vertices=[a0,a1,b1,b0], areaUv={s1AreaUv:F6}, areaWorld={s1AreaWorld:F6}, valid={s1Valid}, reason={s1Reason}, selfIntersect={s1Intersect}, containsMidA={s1ContainsMidA}, containsMidB={s1ContainsMidB}, insideRatio={s1InsideRatio:F3}, Strip2 vertices=[a0,a1,b0,b1], areaUv={s2AreaUv:F6}, areaWorld={s2AreaWorld:F6}, valid={s2Valid}, reason={s2Reason}, selfIntersect={s2Intersect}, containsMidA={s2ContainsMidA}, containsMidB={s2ContainsMidB}, insideRatio={s2InsideRatio:F3}, selectedStrip={selectedStripCandidate}");
            return true;
        }

        if (discardStripPreferred)
        {
            stats.twoEdgeTwoCrossingsStripDiscardedCount++;
            int currentOutsideCount = selectedOutsideTriangles != null ? selectedOutsideTriangles.Count : 0;
            if (selectedOutsideTrianglesAlt != null && selectedOutsideTrianglesAlt.Count > currentOutsideCount)
            {
                selectedOutsideTriangles = selectedOutsideTrianglesAlt;
                selectedStripCandidate += "+altOutside";
                stats.twoEdgeTwoCrossingsAltOutsideUsedCount++;
            }
            if (selectedOutsideTriangles != null && selectedOutsideTriangles.Count > 0)
            {
                dstIndices.AddRange(selectedOutsideTriangles);
                stats.twoEdgeTwoCrossingsOutsideDirectOutputCount++;
                return true;
            }
            TryAddFourPointDebugCandidate(trimmer, debugCandidates, materialName, 80f, $"[NDMF VRoid Mesh Trimmer][4pt-debug] Triangle={triangleIndex}, twoEdgeTwoCrossings=true, stripKept=false, mismatch={aIn!=bIn}, edgeA={edgeA}, edgeA points:a0(t={a0.localTOnEdge:F3},uv={a0.uv}) a1(t={a1.localTOnEdge:F3},uv={a1.uv}), edgeB={edgeB}, edgeB points:b0(t={b0.localTOnEdge:F3},uv={b0.uv}) b1(t={b1.localTOnEdge:F3},uv={b1.uv}), midA={aMid}, midAInside={aIn}, midB={bMid}, midBInside={bIn}, Strip1 valid={s1Valid}, reason={s1Reason}, containsMidA={s1ContainsMidA}, containsMidB={s1ContainsMidB}, Strip2 valid={s2Valid}, reason={s2Reason}, containsMidA={s2ContainsMidA}, containsMidB={s2ContainsMidB}, selectedStrip={selectedStripCandidate}, fallbackReason=strip outside");
            return false;
        }

        stats.twoEdgeTwoCrossingsStripDiscardedCount++;
        TryAddFourPointDebugCandidate(trimmer, debugCandidates, materialName, 80f, $"[NDMF VRoid Mesh Trimmer][4pt-debug] Triangle={triangleIndex}, twoEdgeTwoCrossings=true, stripKept=false, mismatch={aIn!=bIn}, edgeA={edgeA}, edgeB={edgeB}, midA={aMid}, midAInside={aIn}, midB={bMid}, midBInside={bIn}, Strip1 valid={s1Valid}, reason={s1Reason}, containsMidA={s1ContainsMidA}, containsMidB={s1ContainsMidB}, Strip2 valid={s2Valid}, reason={s2Reason}, containsMidA={s2ContainsMidA}, containsMidB={s2ContainsMidB}, selectedStrip={selectedStripCandidate}, fallbackReason=two-edge interval mismatch");
        return false;
    }

    private static void EvaluateStripCandidate(List<int> poly, int i0, int i1, int i2, List<Vector3> vertices, List<Vector2> uv, NDMFVRoidMeshTrimmer trimmer, AlphaMaskProcessor.AlphaMaskData maskData, Vector2 midA, Vector2 midB, out bool valid, out string reason, out float areaUv, out float areaWorld, out bool selfIntersect, out bool containsMidA, out bool containsMidB, out float insideRatio, out List<int> keptTriangles, out List<int> discardTriangles)
    {
        keptTriangles = new List<int>();
        discardTriangles = new List<int>();
        reason = "valid";
        areaUv = Mathf.Abs(SignedArea(uv[poly[0]], uv[poly[1]], uv[poly[2]])) * 0.5f + Mathf.Abs(SignedArea(uv[poly[0]], uv[poly[2]], uv[poly[3]])) * 0.5f;
        selfIntersect = SegmentsIntersect(uv[poly[0]], uv[poly[1]], uv[poly[2]], uv[poly[3]]) || SegmentsIntersect(uv[poly[1]], uv[poly[2]], uv[poly[3]], uv[poly[0]]);
        containsMidA = PointInTriangleUv(midA, uv[poly[0]], uv[poly[1]], uv[poly[2]]) || PointInTriangleUv(midA, uv[poly[0]], uv[poly[2]], uv[poly[3]]);
        containsMidB = PointInTriangleUv(midB, uv[poly[0]], uv[poly[1]], uv[poly[2]]) || PointInTriangleUv(midB, uv[poly[0]], uv[poly[2]], uv[poly[3]]);
        areaWorld = Vector3.Cross(vertices[poly[1]] - vertices[poly[0]], vertices[poly[2]] - vertices[poly[0]]).magnitude * 0.5f
                  + Vector3.Cross(vertices[poly[2]] - vertices[poly[0]], vertices[poly[3]] - vertices[poly[0]]).magnitude * 0.5f;
        if (selfIntersect || areaUv < trimmer.minTriangleUvArea)
        {
            valid = false; reason = selfIntersect ? "invalidSelfIntersect" : "invalidAreaUv"; insideRatio = 0f; return;
        }
        if (areaWorld < trimmer.minTriangleWorldArea) { valid = false; reason = "invalidAreaWorld"; insideRatio = 0f; return; }
        var generated = new List<int>();
        var dummy = new TrimStats();
        TriangulateRegion(i0, i1, i2, poly, vertices, uv, trimmer, generated, ref dummy);
        float sum = 0f; int n = 0;
        for (int t = 0; t + 2 < generated.Count; t += 3)
        {
            if (TrySampleTriangleInsideRatio(maskData, uv[generated[t]], uv[generated[t + 1]], uv[generated[t + 2]], out float r))
            {
                sum += r; n++;
                if (r >= 0.5f)
                {
                    keptTriangles.Add(generated[t]); keptTriangles.Add(generated[t + 1]); keptTriangles.Add(generated[t + 2]);
                }
                else
                {
                    discardTriangles.Add(generated[t]); discardTriangles.Add(generated[t + 1]); discardTriangles.Add(generated[t + 2]);
                }
            }
        }
        insideRatio = n > 0 ? sum / n : 0f;
        valid = generated.Count >= 3;
        if (!valid) reason = "invalidTriangulation";
    }

    private static void TryAddFourPointDebugCandidate(NDMFVRoidMeshTrimmer trimmer, List<FourPointDebugCandidate> list, string materialName, float suspicion, string block)
    {
        if (trimmer == null || !trimmer.debugFourPointClipDetails || list == null) return;
        string filter = trimmer.debugFourPointMaterialFilter ?? "";
        if (!string.IsNullOrEmpty(filter) && (materialName == null || materialName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) return;
        list.Add(new FourPointDebugCandidate { suspicion = suspicion, logBlock = block });
    }

    private static bool TrySampleTriangleInsideRatio(AlphaMaskProcessor.AlphaMaskData maskData, Vector2 uv0, Vector2 uv1, Vector2 uv2, out float insideRatio)
    {
        int samples = 0;
        int inside = 0;
        Vector2 centroid = (uv0 + uv1 + uv2) / 3f;
        Vector2 m01 = (uv0 + uv1) * 0.5f;
        Vector2 m12 = (uv1 + uv2) * 0.5f;
        Vector2 m20 = (uv2 + uv0) * 0.5f;
        Vector2[] points = { centroid, m01, m12, m20 };
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 p = points[i];
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y))
            {
                continue;
            }
            if (AlphaMaskProcessor.SampleMask(maskData, p)) inside++;
            samples++;
        }
        if (samples <= 0)
        {
            insideRatio = 0f;
            return false;
        }
        insideRatio = (float)inside / samples;
        return true;
    }

    private static void BuildFourBoundaryRegions(
        int i0, int i1, int i2, BoundaryPoint p0, BoundaryPoint p1, BoundaryPoint p2, BoundaryPoint p3,
        out List<int> rA, out List<int> rB, out List<int> rMid)
    {
        int[] tri = { i0, i1, i2 };
        List<int> BuildCap(BoundaryPoint a, BoundaryPoint b)
        {
            var poly = new List<int>() { a.vertexIndex };
            int e = a.edgeIndex;
            while (true)
            {
                poly.Add(tri[(e + 1) % 3]);
                if (e == b.edgeIndex) break;
                e = (e + 1) % 3;
            }
            poly.Add(b.vertexIndex);
            return poly;
        }

        rA = BuildCap(p0, p1);
        rB = BuildCap(p2, p3);
        rMid = new List<int>() { p1.vertexIndex };
        int eMid = p1.edgeIndex;
        while (true)
        {
            rMid.Add(tri[(eMid + 1) % 3]);
            if (eMid == p2.edgeIndex) break;
            eMid = (eMid + 1) % 3;
        }
        rMid.Add(p2.vertexIndex);
        rMid.Add(p3.vertexIndex);
        eMid = p3.edgeIndex;
        while (true)
        {
            rMid.Add(tri[(eMid + 1) % 3]);
            if (eMid == p0.edgeIndex) break;
            eMid = (eMid + 1) % 3;
        }
        rMid.Add(p0.vertexIndex);
    }

    private static bool EstimateKeep(AlphaMaskProcessor.AlphaMaskData maskData, List<int> region, List<Vector2> uv)
    {
        int inside = 0;
        int samples = 0;
        for (int i = 0; i < region.Count; i++)
        {
            if (AlphaMaskProcessor.SampleMask(maskData, uv[region[i]])) inside++;
            samples++;
        }

        Vector2 centroid = AverageUv(region, uv);
        if (AlphaMaskProcessor.SampleMask(maskData, centroid)) inside++;
        samples++;

        for (int i = 0; i < region.Count; i++)
        {
            Vector2 a = uv[region[i]];
            Vector2 b = uv[region[(i + 1) % region.Count]];
            Vector2 mid = (a + b) * 0.5f;
            if (AlphaMaskProcessor.SampleMask(maskData, mid)) inside++;
            samples++;
            Vector2 centerBlend = (mid + centroid) * 0.5f;
            if (AlphaMaskProcessor.SampleMask(maskData, centerBlend)) inside++;
            samples++;
        }

        return inside >= (samples * 0.5f);
    }


    private static Vector2 AverageUv(List<int> polygon, List<Vector2> uv)
    {
        Vector2 sum = Vector2.zero;
        for (int i = 0; i < polygon.Count; i++) sum += uv[polygon[i]];
        return sum / Mathf.Max(1, polygon.Count);
    }

    private static void TriangulateRegion(int srcA, int srcB, int srcC, List<int> poly, List<Vector3> vertices, List<Vector2> uv, NDMFVRoidMeshTrimmer trimmer, List<int> dstIndices, ref TrimStats stats)
    {
        if (poly == null || poly.Count < 3) return;

        if (poly.Count == 3)
        {
            AddTrianglePreserveWinding(dstIndices, srcA, srcB, srcC, poly[0], poly[1], poly[2], vertices, uv, trimmer, ref stats);
            return;
        }

        if (TryTriangulateEarClipping(srcA, srcB, srcC, poly, vertices, uv, trimmer, dstIndices, ref stats))
        {
            return;
        }

        for (int i = 1; i < poly.Count - 1; i++)
        {
            AddTrianglePreserveWinding(dstIndices, srcA, srcB, srcC, poly[0], poly[i], poly[i + 1], vertices, uv, trimmer, ref stats);
        }
    }

    private static bool TryTriangulateEarClipping(int srcA, int srcB, int srcC, List<int> poly, List<Vector3> vertices, List<Vector2> uv, NDMFVRoidMeshTrimmer trimmer, List<int> dstIndices, ref TrimStats stats)
    {
        var working = new List<int>(poly);
        float area = 0f;
        for (int i = 0; i < working.Count; i++)
        {
            Vector2 a = uv[working[i]];
            Vector2 b = uv[working[(i + 1) % working.Count]];
            area += (a.x * b.y) - (a.y * b.x);
        }
        bool ccw = area > 0f;
        int guard = 0;
        while (working.Count > 3 && guard++ < 64)
        {
            bool foundEar = false;
            for (int i = 0; i < working.Count; i++)
            {
                int iPrev = working[(i - 1 + working.Count) % working.Count];
                int iCurr = working[i];
                int iNext = working[(i + 1) % working.Count];
                float cross = SignedArea(uv[iPrev], uv[iCurr], uv[iNext]);
                if (ccw ? cross <= 1e-8f : cross >= -1e-8f) continue;

                bool contains = false;
                for (int j = 0; j < working.Count; j++)
                {
                    int test = working[j];
                    if (test == iPrev || test == iCurr || test == iNext) continue;
                    if (PointInTriangleUv(uv[test], uv[iPrev], uv[iCurr], uv[iNext]))
                    {
                        contains = true;
                        break;
                    }
                }
                if (contains) continue;

                AddTrianglePreserveWinding(dstIndices, srcA, srcB, srcC, iPrev, iCurr, iNext, vertices, uv, trimmer, ref stats);
                working.RemoveAt(i);
                foundEar = true;
                break;
            }
            if (!foundEar) return false;
        }

        if (working.Count == 3)
        {
            AddTrianglePreserveWinding(dstIndices, srcA, srcB, srcC, working[0], working[1], working[2], vertices, uv, trimmer, ref stats);
            return true;
        }
        return false;
    }

    private static bool PointInTriangleUv(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float s1 = SignedArea(a, b, p);
        float s2 = SignedArea(b, c, p);
        float s3 = SignedArea(c, a, p);
        bool hasNeg = (s1 < 0f) || (s2 < 0f) || (s3 < 0f);
        bool hasPos = (s1 > 0f) || (s2 > 0f) || (s3 > 0f);
        return !(hasNeg && hasPos);
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
