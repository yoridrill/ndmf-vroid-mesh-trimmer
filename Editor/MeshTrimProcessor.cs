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

    private struct BridgeStats
    {
        public int totalTriangles;
        public int bridgeCandidatesCount;
        public int ambiguousBridgeCandidates;
        public int smallKeptAreaBridgeCandidates;
        public int smallRemovedAreaBridgeCandidates;
        public int continuityIssueBridgeCandidates;
        public int trianglesWithTwoCutEdges;
        public int bridgeCutAppliedCount;
        public int bridgeCutRejectedCount;
        public int replacedClippedResultCount;
        public int keptSideDecidedByNeighborCount;
        public int keptSideDecidedByMaskCount;
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
                if (task.preSubdivideQuadAware)
                {
                    workingIndices = PreSubdivideIndicesQuadAware(srcIndices, task.preSubdivideLevel, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref preAddedVertices, out quadCandidates, out acceptedQuads, out rejectedQuads, out triFallback);
                }
                else
                {
                    workingIndices = PreSubdivideIndices(srcIndices, task.preSubdivideLevel, vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights, hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights, vertexSources, ref preAddedVertices);
                }
            }
            swPre.Stop();

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
                newSubMeshIndices[sub]);

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
        List<int> dstIndices)
    {
        TrimStats stats = new TrimStats();
        BridgeStats bridgeStats = new BridgeStats();
        var triangleResults = new List<TriangleTrimResult>(srcIndices.Length / 3);
        var edgeCuts = new List<EdgeCutInfo>();
        EdgeIntersectionCache cache = new EdgeIntersectionCache();
        int pass2AppliedTotal = 0;

        // Prepass: generate and record all direct edge intersections first.
        // BridgeCut decisions rely on complete cut-edge context across the whole submesh.
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

            if (insideCount == 1)
            {
                int insideV = -1, outA = -1, outB = -1;
                for (int k = 0; k < 3; k++)
                {
                    if (inside[k]) insideV = idx[k];
                    else if (outA < 0) outA = idx[k];
                    else outB = idx[k];
                }
                int cutA = GetOrCreateIntersection(insideV, outA, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources, cache, ref stats);
                int cutB = GetOrCreateIntersection(insideV, outB, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources, cache, ref stats);
                AddEdgeCut(edgeCuts, triIndex, insideV, outA, cutA, uv);
                AddEdgeCut(edgeCuts, triIndex, insideV, outB, cutB, uv);
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
                int cutA = GetOrCreateIntersection(inA, outsideV, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources, cache, ref stats);
                int cutB = GetOrCreateIntersection(inB, outsideV, maskData, trimmer,
                    vertices, normals, tangents, uv, uv2, uv3, uv4, colors, boneWeights,
                    hasNormals, hasTangents, hasUv2, hasUv3, hasUv4, hasColors, hasBoneWeights,
                    vertexSources, cache, ref stats);
                AddEdgeCut(edgeCuts, triIndex, inA, outsideV, cutA, uv);
                AddEdgeCut(edgeCuts, triIndex, inB, outsideV, cutB, uv);
            }
        }
        Debug.Log($"[NDMF VRoid Mesh Trimmer] BridgeCut prepass: Triangles={srcIndices.Length / 3}, EdgeCutsRecorded={edgeCuts.Count}");

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
            Vector2 centroid = (uv[i0] + uv[i1] + uv[i2]) / 3f;
            bool m01In = AlphaMaskProcessor.SampleMask(maskData, m01);
            bool m12In = AlphaMaskProcessor.SampleMask(maskData, m12);
            bool m20In = AlphaMaskProcessor.SampleMask(maskData, m20);
            bool centroidIn = AlphaMaskProcessor.SampleMask(maskData, centroid);

            int insideCount = (in0 ? 1 : 0) + (in1 ? 1 : 0) + (in2 ? 1 : 0);
            int neighborCutEdgeCount = CountNeighborCutEdges(edgeCuts, i0, i1, i2, triIndex);

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
                int edgeInsideCount = (m01In ? 1 : 0) + (m12In ? 1 : 0) + (m20In ? 1 : 0);
                if (m01In || m12In || m20In || centroidIn)
                {
                    stats.allOutsideButInteriorInside++;
                }

                if (edgeInsideCount == 0)
                {
                    if (centroidIn)
                    {
                        if (trimmer.enableBridgeCut && neighborCutEdgeCount >= 2)
                        {
                            if (TryBridgeCutAmbiguousTriangle(triIndex, i0, i1, i2, edgeCuts, dstIndices, vertices, uv, maskData, trimmer, ref stats, out var decidedByNeighbor))
                            {
                                bridgeStats.bridgeCutAppliedCount++;
                                bridgeStats.replacedClippedResultCount++;
                                if (trimmer.bridgeUseNeighborKeptSide) bridgeStats.keptSideDecidedByNeighborCount++;
                                else bridgeStats.keptSideDecidedByMaskCount++;
                                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.5f, 2));
                            }
                            else
                            {
                                stats.removedTriangles++;
                                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 0f, 0));
                            }
                        }
                        else
                        {
                            AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                            stats.centroidOnlyInsidePreserved++;
                            triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 0));
                        }
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
                        if (trimmer.enableBridgeCut && neighborCutEdgeCount >= 2)
                        {
                            if (TryBridgeCutAmbiguousTriangle(triIndex, i0, i1, i2, edgeCuts, dstIndices, vertices, uv, maskData, trimmer, ref stats, out var decidedByNeighbor))
                            {
                                bridgeStats.bridgeCutAppliedCount++;
                                bridgeStats.replacedClippedResultCount++;
                                if (trimmer.bridgeUseNeighborKeptSide) bridgeStats.keptSideDecidedByNeighborCount++;
                                else bridgeStats.keptSideDecidedByMaskCount++;
                                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.5f, 2));
                            }
                            else
                            {
                                AddTriangle(dstIndices, i0, i1, i2, vertices, uv, trimmer, ref stats);
                                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 1f, 1));
                            }
                        }
                        else
                        {
                            stats.removedTriangles++;
                            stats.singleEdgeMidpointInsideDiscarded++;
                            triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Ambiguous, i0, i1, i2, uv, 0f, 1));
                        }
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

                AddEdgeCut(edgeCuts, triIndex, insideV, outA, cutA, uv);
                AddEdgeCut(edgeCuts, triIndex, insideV, outB, cutB, uv);
                if (trimmer.enableBridgeCut && CountNeighborCutEdges(edgeCuts, i0, i1, i2) >= 2 &&
                    TryBridgeCutAmbiguousTriangle(triIndex, i0, i1, i2, edgeCuts, dstIndices, vertices, uv, maskData, trimmer, ref stats, out var decidedByNeighbor))
                {
                    bridgeStats.bridgeCutAppliedCount++;
                    bridgeStats.replacedClippedResultCount++;
                    if (decidedByNeighbor) bridgeStats.keptSideDecidedByNeighborCount++;
                    else bridgeStats.keptSideDecidedByMaskCount++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.5f, 2));
                    continue;
                }
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

                AddEdgeCut(edgeCuts, triIndex, inA, outsideV, cutA, uv);
                AddEdgeCut(edgeCuts, triIndex, inB, outsideV, cutB, uv);
                if (trimmer.enableBridgeCut && CountNeighborCutEdges(edgeCuts, i0, i1, i2) >= 2 &&
                    TryBridgeCutAmbiguousTriangle(triIndex, i0, i1, i2, edgeCuts, dstIndices, vertices, uv, maskData, trimmer, ref stats, out var decidedByNeighbor))
                {
                    bridgeStats.bridgeCutAppliedCount++;
                    bridgeStats.replacedClippedResultCount++;
                    if (decidedByNeighbor) bridgeStats.keptSideDecidedByNeighborCount++;
                    else bridgeStats.keptSideDecidedByMaskCount++;
                    triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.5f, 2));
                    continue;
                }
                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, inA, inB, cutA, vertices, uv, trimmer, ref stats);
                AddTrianglePreserveWinding(dstIndices, i0, i1, i2, inB, cutB, cutA, vertices, uv, trimmer, ref stats);
                triangleResults.Add(BuildResult(triIndex, TriangleTrimState.Clipped, i0, i1, i2, uv, 0.66f, 2));
            }
        }

        // Pass2: revisit removed/ambiguous triangles after all edge cuts are known.
        // This fills bridge candidates that were not decidable during pass1 due to processing order.
        if (trimmer.enableBridgeCut)
        {
            int pass2CandidateCount = 0;
            int pass2AttemptCount = 0;
            int appliedBeforePass2 = bridgeStats.bridgeCutAppliedCount;
            for (int i = 0; i < triangleResults.Count; i++)
            {
                var r = triangleResults[i];
                bool removedCandidate = IsBridgeCandidate(r, trimmer) &&
                                        r.keptAreaRatio <= 0f &&
                                        r.state == TriangleTrimState.Ambiguous;
                bool smallAreaCandidate = IsBridgeCandidate(r, trimmer) &&
                                          r.state == TriangleTrimState.Clipped;

                int triBase = r.triangleIndex * 3;
                if (triBase < 0 || triBase + 2 >= srcIndices.Length) continue;
                int ti0 = srcIndices[triBase];
                int ti1 = srcIndices[triBase + 1];
                int ti2 = srcIndices[triBase + 2];
                int neighborCutEdges = CountNeighborCutEdges(edgeCuts, ti0, ti1, ti2, r.triangleIndex);
                // Pass2 appends geometry; avoid applying it to already-clipped triangles or tips become too thick.
                bool continuityCandidate = trimmer.bridgeUseNeighborKeptSide && neighborCutEdges >= 2 && removedCandidate;
                if (!removedCandidate && !smallAreaCandidate && !continuityCandidate) continue;
                if (neighborCutEdges < 2) continue;
                pass2CandidateCount++;

                pass2AttemptCount++;
                if (TryBridgeCutAmbiguousTriangle(r.triangleIndex, ti0, ti1, ti2, edgeCuts, dstIndices, vertices, uv, maskData, trimmer, ref stats, out var decidedByNeighbor))
                {
                    bridgeStats.bridgeCutAppliedCount++;
                    bridgeStats.replacedClippedResultCount++;
                    if (decidedByNeighbor) bridgeStats.keptSideDecidedByNeighborCount++;
                    else bridgeStats.keptSideDecidedByMaskCount++;

                    // Reflect pass2 recovery in recorded per-triangle result so downstream stats are consistent.
                    r.state = TriangleTrimState.Clipped;
                    r.keptAreaRatio = 0.5f;
                    r.removedAreaRatio = 0.5f;
                    r.cutEdges = 2;
                    triangleResults[i] = r;
                }
            }
            int pass2Applied = bridgeStats.bridgeCutAppliedCount - appliedBeforePass2;
            pass2AppliedTotal = pass2Applied;
            int pass2Rejected = Mathf.Max(0, pass2AttemptCount - pass2Applied);
            Debug.Log($"[NDMF VRoid Mesh Trimmer] BridgeCut pass2: Candidates={pass2CandidateCount}, Attempts={pass2AttemptCount}, Applied={pass2Applied}, Rejected={pass2Rejected}");
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
                    // Approximate split centroids for bridge scoring metadata.
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
        bridgeStats.totalTriangles = srcIndices.Length / 3;
        if (trimmer.enableBridgeCut)
        {
        for (int i = 0; i < triangleResults.Count; i++)
        {
            var r = triangleResults[i];
            bool ambiguous = r.state == TriangleTrimState.Ambiguous;
            bool smallKept = r.state == TriangleTrimState.Clipped && r.keptAreaRatio < trimmer.bridgeSmallKeptAreaRatio;
            bool smallRemoved = r.state == TriangleTrimState.Clipped && r.removedAreaRatio < trimmer.bridgeSmallRemovedAreaRatio;
            bool baseBridgeCandidate = IsBridgeCandidate(r, trimmer);
            int triBase = r.triangleIndex * 3;
            int cutCount = 0;
            if (triBase >= 0 && triBase + 2 < srcIndices.Length)
            {
                int ti0 = srcIndices[triBase];
                int ti1 = srcIndices[triBase + 1];
                int ti2 = srcIndices[triBase + 2];
                cutCount = CountNeighborCutEdges(edgeCuts, ti0, ti1, ti2, r.triangleIndex);
            }
            bool hasTwoCutEdges = cutCount >= 2;
            if (hasTwoCutEdges) bridgeStats.trianglesWithTwoCutEdges++;
            bool hasNeighborCutContinuityIssue = trimmer.bridgeUseNeighborKeptSide &&
                                                 hasTwoCutEdges &&
                                                 r.state == TriangleTrimState.Clipped &&
                                                 r.cutEdges < 2;
            if (baseBridgeCandidate || hasNeighborCutContinuityIssue) bridgeStats.bridgeCandidatesCount++;
            if (ambiguous) bridgeStats.ambiguousBridgeCandidates++;
            if (smallKept) bridgeStats.smallKeptAreaBridgeCandidates++;
            if (smallRemoved) bridgeStats.smallRemovedAreaBridgeCandidates++;
            if (hasNeighborCutContinuityIssue) bridgeStats.continuityIssueBridgeCandidates++;
        }
        if (bridgeStats.bridgeCandidatesCount > 0)
        {
            bridgeStats.bridgeCutRejectedCount = Mathf.Max(0, bridgeStats.bridgeCandidatesCount - bridgeStats.bridgeCutAppliedCount);
        }
        }
        var uniqueTri = new HashSet<int>();
        for (int i = 0; i < triangleResults.Count; i++) uniqueTri.Add(triangleResults[i].triangleIndex);
        if (uniqueTri.Count != bridgeStats.totalTriangles)
        {
            Debug.LogWarning($"[NDMF VRoid Mesh Trimmer] BridgeCut pass1 result count mismatch. TotalTriangles={bridgeStats.totalTriangles}, RecordedResults={triangleResults.Count}, UniqueRecordedTriangles={uniqueTri.Count}");
        }
        Debug.Log($"[NDMF VRoid Mesh Trimmer] BridgeCut stats: Enabled={trimmer.enableBridgeCut}, TotalTriangles={bridgeStats.totalTriangles}, RecordedResults={triangleResults.Count}, UniqueRecordedTriangles={uniqueTri.Count}, BridgeCandidatesCount={bridgeStats.bridgeCandidatesCount}, AmbiguousBridgeCandidates={bridgeStats.ambiguousBridgeCandidates}, SmallKeptAreaBridgeCandidates={bridgeStats.smallKeptAreaBridgeCandidates}, SmallRemovedAreaBridgeCandidates={bridgeStats.smallRemovedAreaBridgeCandidates}, ContinuityIssueBridgeCandidates={bridgeStats.continuityIssueBridgeCandidates}, TrianglesWithTwoCutEdges={bridgeStats.trianglesWithTwoCutEdges}, BridgeCutAppliedCount={bridgeStats.bridgeCutAppliedCount}, Pass2AppliedCount={pass2AppliedTotal}, BridgeCutRejectedCount={bridgeStats.bridgeCutRejectedCount}, ReplacedClippedResultCount={bridgeStats.replacedClippedResultCount}, KeptSideDecidedByNeighborCount={bridgeStats.keptSideDecidedByNeighborCount}, KeptSideDecidedByMaskCount={bridgeStats.keptSideDecidedByMaskCount}");
        return stats;
    }

    private static bool IsBridgeCandidate(TriangleTrimResult r, NDMFVRoidMeshTrimmer trimmer)
    {
        if (r.state == TriangleTrimState.Ambiguous) return true;
        if (r.state != TriangleTrimState.Clipped) return false;
        return r.keptAreaRatio < trimmer.bridgeSmallKeptAreaRatio ||
               r.removedAreaRatio < trimmer.bridgeSmallRemovedAreaRatio;
    }





    private static bool TryBridgeCutAmbiguousTriangle(int triIndex, int i0, int i1, int i2, List<EdgeCutInfo> edgeCuts, List<int> dstIndices,
        List<Vector3> vertices, List<Vector2> uv, AlphaMaskProcessor.AlphaMaskData maskData, NDMFVRoidMeshTrimmer trimmer, ref TrimStats stats, out bool decidedByNeighbor)
    {
        decidedByNeighbor = false;
        EdgeCutInfo c01 = default, c12 = default, c20 = default;
        bool h01=false,h12=false,h20=false;
        long e01=MakeEdgeKey(i0,i1), e12=MakeEdgeKey(i1,i2), e20=MakeEdgeKey(i2,i0);
        for (int i=0;i<edgeCuts.Count;i++)
        {
            var c=edgeCuts[i];
            if (!h01 && c.edgeKey==e01){c01=c;h01=true;}
            else if (!h12 && c.edgeKey==e12){c12=c;h12=true;}
            else if (!h20 && c.edgeKey==e20){c20=c;h20=true;}
        }
        int hits=(h01?1:0)+(h12?1:0)+(h20?1:0);
        if (hits<2) return false;

        int cutA=-1, cutB=-1, shared=-1, other1=-1, other2=-1;
        EdgeCutInfo sideA = default, sideB = default;
        if (h01 && h20){cutA=c01.cutPointIndex;cutB=c20.cutPointIndex;sideA=c01;sideB=c20;shared=i0;other1=i1;other2=i2;}
        else if (h01 && h12){cutA=c01.cutPointIndex;cutB=c12.cutPointIndex;sideA=c01;sideB=c12;shared=i1;other1=i0;other2=i2;}
        else if (h12 && h20){cutA=c12.cutPointIndex;cutB=c20.cutPointIndex;sideA=c12;sideB=c20;shared=i2;other1=i1;other2=i0;}
        else return false;
        if (cutA == cutB) return false;
        if (cutA<0||cutB<0||cutA>=uv.Count||cutB>=uv.Count) return false;

        float triArea = Mathf.Abs(SignedArea(uv[i0], uv[i1], uv[i2])) * 0.5f;
        if (triArea <= 0f) return false;
        float cutArea = Mathf.Abs(SignedArea(uv[shared], uv[cutA], uv[cutB])) * 0.5f;
        if (cutArea <= 0f) return false;

        bool keepSharedCorner;
        if (trimmer.bridgeUseNeighborKeptSide)
        {
            Vector2 sharedCentroid = (uv[shared] + uv[cutA] + uv[cutB]) / 3f;
            Vector2 oppositeCentroid = (uv[other1] + uv[other2] + uv[cutA] + uv[cutB]) * 0.25f;

            float sharedNeighborScore = 0f;
            float oppositeNeighborScore = 0f;

            sharedNeighborScore += 1f / (Vector2.Distance(sharedCentroid, sideA.keptSideCentroidUv) + 1e-5f);
            sharedNeighborScore += 1f / (Vector2.Distance(sharedCentroid, sideB.keptSideCentroidUv) + 1e-5f);
            oppositeNeighborScore += 1f / (Vector2.Distance(oppositeCentroid, sideA.keptSideCentroidUv) + 1e-5f);
            oppositeNeighborScore += 1f / (Vector2.Distance(oppositeCentroid, sideB.keptSideCentroidUv) + 1e-5f);

            // Contact-edge continuity bonus: prefer candidate side that aligns with neighbor kept-side edge endpoint.
            bool sharedMatchesA = (sideA.keptSideUsesEdgeA && shared == sideA.edgeA) || (!sideA.keptSideUsesEdgeA && shared == sideA.edgeB);
            bool sharedMatchesB = (sideB.keptSideUsesEdgeA && shared == sideB.edgeA) || (!sideB.keptSideUsesEdgeA && shared == sideB.edgeB);
            if (sharedMatchesA) sharedNeighborScore += 0.5f; else oppositeNeighborScore += 0.5f;
            if (sharedMatchesB) sharedNeighborScore += 0.5f; else oppositeNeighborScore += 0.5f;

            if (Mathf.Abs(sharedNeighborScore - oppositeNeighborScore) < 1e-4f)
            {
                // Tie: fallback to mask sampling ratio at representative points.
                int sharedInsideVotes = 0;
                if (AlphaMaskProcessor.SampleMask(maskData, sharedCentroid)) sharedInsideVotes++;
                if (AlphaMaskProcessor.SampleMask(maskData, uv[shared])) sharedInsideVotes++;
                int oppositeInsideVotes = 0;
                if (AlphaMaskProcessor.SampleMask(maskData, oppositeCentroid)) oppositeInsideVotes++;
                if (AlphaMaskProcessor.SampleMask(maskData, (uv[other1] + uv[other2]) * 0.5f)) oppositeInsideVotes++;
                if (sharedInsideVotes == oppositeInsideVotes)
                {
                    // Next priority: prefer side containing original inside samples.
                    bool sharedContainsInside = AlphaMaskProcessor.SampleMask(maskData, uv[shared]);
                    bool oppositeContainsInside = AlphaMaskProcessor.SampleMask(maskData, uv[other1]) ||
                                                  AlphaMaskProcessor.SampleMask(maskData, uv[other2]);
                    if (sharedContainsInside != oppositeContainsInside)
                    {
                        keepSharedCorner = sharedContainsInside;
                        decidedByNeighbor = false;
                    }
                    else
                    {
                        // Final fallback: larger stable area.
                        float sharedArea = Mathf.Abs(SignedArea(uv[shared], uv[cutA], uv[cutB])) * 0.5f;
                        float totalArea = Mathf.Abs(SignedArea(uv[i0], uv[i1], uv[i2])) * 0.5f;
                        float oppositeArea = Mathf.Max(0f, totalArea - sharedArea);
                        keepSharedCorner = sharedArea >= oppositeArea;
                        decidedByNeighbor = false;
            decidedByNeighbor = false;
                    }
                }
                else
                {
                    keepSharedCorner = sharedInsideVotes > oppositeInsideVotes;
                decidedByNeighbor = false;
                }
            }
            else
            {
                keepSharedCorner = sharedNeighborScore > oppositeNeighborScore;
                decidedByNeighbor = true;
            }
        }
        else
        {
            // mask-based fallback: keep larger side by UV area proxy
            float sharedArea = Mathf.Abs(SignedArea(uv[shared], uv[cutA], uv[cutB])) * 0.5f;
            float totalArea = Mathf.Abs(SignedArea(uv[i0], uv[i1], uv[i2])) * 0.5f;
            float oppositeArea = Mathf.Max(0f, totalArea - sharedArea);
            keepSharedCorner = sharedArea >= oppositeArea;
            decidedByNeighbor = false;
        }

        if (keepSharedCorner)
        {
            AddEdgeCut(edgeCuts, triIndex, shared, other1, cutA, uv);
            AddEdgeCut(edgeCuts, triIndex, shared, other2, cutB, uv);
            AddTrianglePreserveWinding(dstIndices, i0, i1, i2, shared, cutA, cutB, vertices, uv, trimmer, ref stats);
        }
        else
        {
            float a1 = Mathf.Abs(SignedArea(uv[other1], uv[other2], uv[cutA])) * 0.5f;
            float a2 = Mathf.Abs(SignedArea(uv[other2], uv[cutB], uv[cutA])) * 0.5f;
            if (a1 <= 0f || a2 <= 0f) return false;
            AddEdgeCut(edgeCuts, triIndex, shared, other1, cutA, uv);
            AddEdgeCut(edgeCuts, triIndex, shared, other2, cutB, uv);
            AddTrianglePreserveWinding(dstIndices, i0, i1, i2, other1, other2, cutA, vertices, uv, trimmer, ref stats);
            AddTrianglePreserveWinding(dstIndices, i0, i1, i2, other2, cutB, cutA, vertices, uv, trimmer, ref stats);
        }
        return true;
    }

    private static int CountNeighborCutEdges(List<EdgeCutInfo> edgeCuts, int i0, int i1, int i2, int excludeTriangleIndex = -1)
    {
        if (edgeCuts == null || edgeCuts.Count == 0) return 0;
        long e01 = MakeEdgeKey(i0, i1);
        long e12 = MakeEdgeKey(i1, i2);
        long e20 = MakeEdgeKey(i2, i0);
        bool h01 = false, h12 = false, h20 = false;
        for (int i = 0; i < edgeCuts.Count; i++)
        {
            if (edgeCuts[i].triangleIndex == excludeTriangleIndex) continue;
            long k = edgeCuts[i].edgeKey;
            if (k == e01) h01 = true;
            else if (k == e12) h12 = true;
            else if (k == e20) h20 = true;
        }

        return (h01 ? 1 : 0) + (h12 ? 1 : 0) + (h20 ? 1 : 0);
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
