using System;
using System.Collections.Generic;
using UnityEngine;

internal static class EdgeCrossingTrimRouter
{
    internal enum TriangleRoute
    {
        WholeKeep,
        WholeTrim,
        TwoOddEdgesAsOneLine,
        TwoOddEdgesAndOneEvenEdge,
        TwoEvenEdges
    }
    internal enum EdgeParityClass
    {
        ZeroEdge,
        OddEdge,
        EvenEdge
    }

    internal readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        public readonly int a;
        public readonly int b;

        public EdgeKey(int x, int y)
        {
            if (x <= y)
            {
                a = x;
                b = y;
            }
            else
            {
                a = y;
                b = x;
            }
        }

        public bool Equals(EdgeKey other) => a == other.a && b == other.b;
        public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
        public override int GetHashCode() => (a * 397) ^ b;
    }

    internal struct EdgeCrossing
    {
        public EdgeKey edge;
        public float t;
        public bool isBeforeInside;
    }

    internal struct LocalCrossing
    {
        public int edgeIndex;
        public int edgeStart;
        public int edgeEnd;
        public float t;
        public bool isBeforeInside;
    }

    internal struct EdgeInfo
    {
        public int edgeIndex;
        public int edgeStart;
        public int edgeEnd;
        public List<LocalCrossing> crossings;
        public EdgeParityClass parityClass;
    }

    // Stage-1 scaffolding for segment-graph based loop extraction.
    // Not wired into existing routing outputs yet.
    internal readonly struct LoopNode : IEquatable<LoopNode>
    {
        public readonly bool isOriginalVertex;
        public readonly int originalVertexId;
        public readonly int activeCrossingIndex;
        public readonly LocalCrossing crossing;

        private LoopNode(bool isOriginalVertex, int originalVertexId, int activeCrossingIndex, LocalCrossing crossing)
        {
            this.isOriginalVertex = isOriginalVertex;
            this.originalVertexId = originalVertexId;
            this.activeCrossingIndex = activeCrossingIndex;
            this.crossing = crossing;
        }

        public static LoopNode MakeOriginalNode(int originalVertexId) => new LoopNode(true, originalVertexId, -1, default);
        public static LoopNode MakeCrossingNode(LocalCrossing crossing, int activeCrossingIndex = -1) => new LoopNode(false, -1, activeCrossingIndex, crossing);

        public bool Equals(LoopNode other) => SameNode(this, other);
        public override bool Equals(object obj) => obj is LoopNode other && Equals(other);
        public override int GetHashCode()
        {
            if (isOriginalVertex) return originalVertexId;
            unchecked
            {
                int h = 17;
                h = h * 31 + activeCrossingIndex;
                return h;
            }
        }
    }

    internal readonly struct LoopSegment
    {
        public readonly LoopNode a;
        public readonly LoopNode b;
        public LoopSegment(LoopNode a, LoopNode b) { this.a = a; this.b = b; }
    }

    internal sealed class LoopGraph
    {
        public readonly List<LoopNode> nodes = new List<LoopNode>();
        public readonly List<LoopSegment> segments = new List<LoopSegment>();
    }

    internal readonly struct Chord
    {
        public readonly LocalCrossing a;
        public readonly LocalCrossing b;
        public Chord(LocalCrossing a, LocalCrossing b) { this.a = a; this.b = b; }
    }

    internal struct TriangleContext
    {
        public int v0;
        public int v1;
        public int v2;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector2 uv2;
        public Func<Vector2, bool> SampleInside;
        public Dictionary<EdgeKey, List<EdgeCrossing>> sharedCrossings;
    }

    internal readonly struct TriangleProcessResult
    {
        internal readonly struct PolygonVertexRef
        {
            public readonly bool isOriginalVertex;
            public readonly int originalVertexId;
            public readonly LocalCrossing crossing;
            public PolygonVertexRef(int originalVertexId) { isOriginalVertex = true; this.originalVertexId = originalVertexId; crossing = default; }
            public PolygonVertexRef(LocalCrossing crossing) { isOriginalVertex = false; originalVertexId = -1; this.crossing = crossing; }
        }
        public readonly TriangleRoute route;
        public readonly bool keepWholeTriangle;
        public readonly bool hasOneLineSplit;
        public readonly LocalCrossing splitCrossingA;
        public readonly LocalCrossing splitCrossingB;
        public readonly int[] keptInsideVertices;
        public readonly bool hasTwoLineSplit;
        public readonly LocalCrossing evenCrossingMin;
        public readonly LocalCrossing evenCrossingMax;
        public readonly LocalCrossing oddCrossing0;
        public readonly LocalCrossing oddCrossing1;
        public readonly bool pairingIsDirect;
        public readonly bool middleInside;
        public readonly LocalCrossing evenBCrossingMin;
        public readonly LocalCrossing evenBCrossingMax;
        public readonly PolygonVertexRef[][] insidePolygons;

        public TriangleProcessResult(TriangleRoute route, bool keepWholeTriangle)
        {
            this.route = route;
            this.keepWholeTriangle = keepWholeTriangle;
            hasOneLineSplit = false;
            splitCrossingA = default;
            splitCrossingB = default;
            keptInsideVertices = Array.Empty<int>();
            hasTwoLineSplit = false;
            evenCrossingMin = default;
            evenCrossingMax = default;
            oddCrossing0 = default;
            oddCrossing1 = default;
            pairingIsDirect = false;
            middleInside = false;
            evenBCrossingMin = default;
            evenBCrossingMax = default;
            insidePolygons = Array.Empty<PolygonVertexRef[]>();
        }

        public TriangleProcessResult(LocalCrossing splitCrossingA, LocalCrossing splitCrossingB, int[] keptInsideVertices)
        {
            route = TriangleRoute.TwoOddEdgesAsOneLine;
            keepWholeTriangle = false;
            hasOneLineSplit = true;
            this.splitCrossingA = splitCrossingA;
            this.splitCrossingB = splitCrossingB;
            this.keptInsideVertices = keptInsideVertices ?? Array.Empty<int>();
            hasTwoLineSplit = false;
            evenCrossingMin = default;
            evenCrossingMax = default;
            oddCrossing0 = default;
            oddCrossing1 = default;
            pairingIsDirect = false;
            middleInside = false;
            evenBCrossingMin = default;
            evenBCrossingMax = default;
            insidePolygons = new[] { new[] { new PolygonVertexRef(splitCrossingA), new PolygonVertexRef(splitCrossingB) } };
        }

        public TriangleProcessResult(LocalCrossing evenACrossingMin, LocalCrossing evenACrossingMax, LocalCrossing evenBCrossingMin, LocalCrossing evenBCrossingMax, bool pairingIsDirect, bool middleInside)
        {
            route = TriangleRoute.TwoEvenEdges;
            keepWholeTriangle = false;
            hasOneLineSplit = false;
            splitCrossingA = default;
            splitCrossingB = default;
            keptInsideVertices = Array.Empty<int>();
            hasTwoLineSplit = true;
            evenCrossingMin = evenACrossingMin;
            evenCrossingMax = evenACrossingMax;
            oddCrossing0 = default;
            oddCrossing1 = default;
            this.pairingIsDirect = pairingIsDirect;
            this.middleInside = middleInside;
            this.evenBCrossingMin = evenBCrossingMin;
            this.evenBCrossingMax = evenBCrossingMax;
            insidePolygons = Array.Empty<PolygonVertexRef[]>();
        }

        public TriangleProcessResult(TriangleRoute route, LocalCrossing crossing0, LocalCrossing crossing1, LocalCrossing crossing2, LocalCrossing crossing3, bool pairingIsDirect, bool middleInside, PolygonVertexRef[][] insidePolygons)
        {
            this.route = route;
            keepWholeTriangle = false;
            hasOneLineSplit = false;
            splitCrossingA = default;
            splitCrossingB = default;
            keptInsideVertices = Array.Empty<int>();
            hasTwoLineSplit = true;
            evenCrossingMin = crossing0;
            evenCrossingMax = crossing1;
            this.oddCrossing0 = route == TriangleRoute.TwoOddEdgesAndOneEvenEdge ? crossing2 : default;
            this.oddCrossing1 = route == TriangleRoute.TwoOddEdgesAndOneEvenEdge ? crossing3 : default;
            this.pairingIsDirect = pairingIsDirect;
            this.middleInside = middleInside;
            evenBCrossingMin = route == TriangleRoute.TwoEvenEdges ? crossing2 : default;
            evenBCrossingMax = route == TriangleRoute.TwoEvenEdges ? crossing3 : default;
            this.insidePolygons = insidePolygons ?? Array.Empty<PolygonVertexRef[]>();
        }
    }

    internal static List<LocalCrossing> BuildLocalCrossings(TriangleContext triangle)
    {
        var local = new List<LocalCrossing>();
        AppendEdgeLocalCrossings(triangle, 0, triangle.v0, triangle.v1, local);
        AppendEdgeLocalCrossings(triangle, 1, triangle.v1, triangle.v2, local);
        AppendEdgeLocalCrossings(triangle, 2, triangle.v2, triangle.v0, local);
        return local;
    }

    internal static List<EdgeInfo> BuildEdgeInfos(TriangleContext triangle)
    {
        var allLocal = BuildLocalCrossings(triangle);
        var infos = new List<EdgeInfo>(3);
        for (int i = 0; i < 3; i++)
        {
            infos.Add(new EdgeInfo
            {
                edgeIndex = i,
                edgeStart = i == 0 ? triangle.v0 : (i == 1 ? triangle.v1 : triangle.v2),
                edgeEnd = i == 0 ? triangle.v1 : (i == 1 ? triangle.v2 : triangle.v0),
                crossings = new List<LocalCrossing>()
            });
        }

        for (int i = 0; i < allLocal.Count; i++)
        {
            var c = allLocal[i];
            var info = infos[c.edgeIndex];
            info.crossings.Add(c);
            infos[c.edgeIndex] = info;
        }

        for (int i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            info.crossings.Sort((x, y) => x.t.CompareTo(y.t));
            NormalizeCrossingsInPlace(info.crossings);
            info.parityClass = ClassifyEdge(info.crossings.Count);
            infos[i] = info;
        }

        return infos;
    }

    internal static TriangleProcessResult ProcessTriangle(TriangleContext triangle)
    {
        var edgeInfos = BuildEdgeInfos(triangle);
        int odd = 0;
        int even = 0;
        for (int i = 0; i < edgeInfos.Count; i++)
        {
            if (edgeInfos[i].parityClass == EdgeParityClass.OddEdge) odd++;
            else if (edgeInfos[i].parityClass == EdgeParityClass.EvenEdge) even++;
        }

        if (odd == 2)
        {
            if (even == 0) return ProcessTwoOddEdgesAsOneLine(triangle, edgeInfos);
            if (even == 1) return ProcessTwoOddEdgesAndOneEvenEdge(triangle, edgeInfos);
            return MakeWholeBySevenPointMajority(triangle);
        }

        if (odd == 0 && even == 2) return ProcessTwoEvenEdges(triangle, edgeInfos);

        return MakeWholeBySevenPointMajority(triangle);
    }

    internal static Vector2 GetNodeUv(TriangleContext triangle, LoopNode node)
    {
        return node.isOriginalVertex
            ? GetVertexUv(triangle, node.originalVertexId)
            : GetLocalCrossingUv(triangle, node.crossing);
    }

    internal static bool SameNode(LoopNode a, LoopNode b)
    {
        if (a.isOriginalVertex != b.isOriginalVertex) return false;
        if (a.isOriginalVertex) return a.originalVertexId == b.originalVertexId;
        if (a.activeCrossingIndex >= 0 || b.activeCrossingIndex >= 0)
            return a.activeCrossingIndex == b.activeCrossingIndex;
        return a.crossing.edgeIndex == b.crossing.edgeIndex && Mathf.Abs(a.crossing.t - b.crossing.t) <= 1e-6f;
    }

    internal static bool AddSegmentIfValid(TriangleContext triangle, LoopGraph graph, LoopNode a, LoopNode b, float epsilon)
    {
        Vector2 ua = GetNodeUv(triangle, a);
        Vector2 ub = GetNodeUv(triangle, b);
        if ((ua - ub).sqrMagnitude <= epsilon * epsilon) return false;
        if (SameNode(a, b)) return false;
        for (int i = 0; i < graph.segments.Count; i++)
        {
            var s = graph.segments[i];
            if ((SameNode(s.a, a) && SameNode(s.b, b)) || (SameNode(s.a, b) && SameNode(s.b, a)))
                return false;
        }
        graph.segments.Add(new LoopSegment(a, b));
        if (!graph.nodes.Contains(a)) graph.nodes.Add(a);
        if (!graph.nodes.Contains(b)) graph.nodes.Add(b);
        return true;
    }

    internal static LoopNode MakeOriginalNode(int originalVertexId) => LoopNode.MakeOriginalNode(originalVertexId);
    internal static LoopNode MakeCrossingNode(LocalCrossing crossing, int activeCrossingIndex = -1) => LoopNode.MakeCrossingNode(crossing, activeCrossingIndex);

    internal static bool TryBuildInsideBoundarySegments(
        TriangleContext triangle,
        List<EdgeInfo> edgeInfos,
        IReadOnlyCollection<LocalCrossing> activeCrossings,
        IReadOnlyDictionary<int, LoopNode> activeNodes,
        out List<LoopSegment> boundarySegments)
    {
        return TryBuildInsideBoundarySegments(triangle, edgeInfos, activeCrossings, activeNodes, out boundarySegments, out _);
    }

    internal static bool TrySelectUniqueChordPairing(
        TriangleContext triangle,
        IReadOnlyList<LocalCrossing> activeCrossings,
        out Chord chord0,
        out Chord chord1,
        out string failReason)
    {
        chord0 = default;
        chord1 = default;
        failReason = "none";
        if (activeCrossings == null || activeCrossings.Count != 4)
        {
            failReason = "active_crossing_count_not_4";
            return false;
        }

        var c = activeCrossings;
        var candidates = new (int a, int b, int c0, int d)[]
        {
            (0, 1, 2, 3),
            (0, 2, 1, 3),
            (0, 3, 1, 2),
        };

        int validCount = 0;
        const float epsilon = 1e-6f;
        for (int i = 0; i < candidates.Length; i++)
        {
            var p = candidates[i];
            var x0 = c[p.a];
            var x1 = c[p.b];
            var y0 = c[p.c0];
            var y1 = c[p.d];

            if (x0.edgeIndex == x1.edgeIndex || y0.edgeIndex == y1.edgeIndex) continue;

            Vector2 x0Uv = GetLocalCrossingUv(triangle, x0);
            Vector2 x1Uv = GetLocalCrossingUv(triangle, x1);
            Vector2 y0Uv = GetLocalCrossingUv(triangle, y0);
            Vector2 y1Uv = GetLocalCrossingUv(triangle, y1);

            if ((x0Uv - x1Uv).sqrMagnitude <= epsilon * epsilon) continue;
            if ((y0Uv - y1Uv).sqrMagnitude <= epsilon * epsilon) continue;
            if (SegmentsProperlyIntersect(x0Uv, x1Uv, y0Uv, y1Uv, epsilon)) continue;

            validCount++;
            chord0 = new Chord(x0, x1);
            chord1 = new Chord(y0, y1);
        }

        if (validCount == 0) failReason = "no_valid_pairing";
        else if (validCount > 1) failReason = "pairing_not_unique";
        return validCount == 1;
    }

    internal static bool TryExtractInsideLoops(
        TriangleContext triangle,
        IReadOnlyList<LoopSegment> boundarySegments,
        IReadOnlyDictionary<int, LoopNode> activeNodes,
        Chord chord0,
        Chord chord1,
        out LoopNode[][] loops,
        out string failReason)
    {
        loops = Array.Empty<LoopNode[]>();
        failReason = "none";
        const float epsilon = 1e-6f;
        const float minArea = 1e-10f;

        var graph = new LoopGraph();
        if (boundarySegments != null)
        {
            for (int i = 0; i < boundarySegments.Count; i++)
            {
                AddSegmentIfValid(triangle, graph, boundarySegments[i].a, boundarySegments[i].b, epsilon);
            }
        }
        if (!TryGetActiveNode(activeNodes, chord0.a, out var c0a) || !TryGetActiveNode(activeNodes, chord0.b, out var c0b)
            || !AddSegmentIfValid(triangle, graph, c0a, c0b, epsilon))
        {
            failReason = "invalid_chord0";
            return false;
        }
        if (!TryGetActiveNode(activeNodes, chord1.a, out var c1a) || !TryGetActiveNode(activeNodes, chord1.b, out var c1b)
            || !AddSegmentIfValid(triangle, graph, c1a, c1b, epsilon))
        {
            failReason = "invalid_chord1";
            return false;
        }

        var adj = BuildAdjacency(graph);
        foreach (var kv in adj)
        {
            int degree = kv.Value.Count;
            if (degree != 2)
            {
                failReason = degree < 2 ? "open_path_or_degree1" : "degree3_or_more";
                return false;
            }
        }

        var loopList = new List<LoopNode[]>();
        var visited = new HashSet<LoopNode>();
        foreach (var start in adj.Keys)
        {
            if (visited.Contains(start)) continue;
            if (!TraceCycle(adj, start, out var loop, out failReason)) return false;
            foreach (var n in loop) visited.Add(n);
            if (!ValidateExtractedLoop(triangle, loop, epsilon, minArea, out failReason)) return false;
            loopList.Add(loop.ToArray());
        }

        loops = loopList.ToArray();
        return loops.Length > 0;
    }

    private static Dictionary<LoopNode, List<LoopNode>> BuildAdjacency(LoopGraph graph)
    {
        var adj = new Dictionary<LoopNode, List<LoopNode>>();
        for (int i = 0; i < graph.segments.Count; i++)
        {
            var s = graph.segments[i];
            if (!adj.TryGetValue(s.a, out var la)) { la = new List<LoopNode>(); adj[s.a] = la; }
            if (!adj.TryGetValue(s.b, out var lb)) { lb = new List<LoopNode>(); adj[s.b] = lb; }
            la.Add(s.b);
            lb.Add(s.a);
        }
        return adj;
    }

    private static bool TraceCycle(Dictionary<LoopNode, List<LoopNode>> adj, LoopNode start, out List<LoopNode> loop, out string failReason)
    {
        loop = new List<LoopNode> { start };
        failReason = "none";
        LoopNode prev = default;
        LoopNode cur = start;
        var localVisited = new HashSet<LoopNode>();
        int guard = 0;
        while (guard++ < 128)
        {
            var neighbors = adj[cur];
            LoopNode next = SameNode(neighbors[0], prev) ? neighbors[1] : neighbors[0];
            if (!SameNode(cur, start) && localVisited.Contains(cur)) { failReason = "cycle_not_closed"; return false; }
            localVisited.Add(cur);
            prev = cur;
            cur = next;
            if (SameNode(cur, start)) return true;
            if (!adj.ContainsKey(cur)) { failReason = "cycle_next_not_found"; return false; }
            loop.Add(cur);
        }
        failReason = "cycle_guard_exceeded";
        return false;
    }

    private static bool ValidateExtractedLoop(TriangleContext triangle, List<LoopNode> loop, float epsilon, float minArea, out string failReason)
    {
        return ValidateExtractedLoop(triangle, loop, epsilon, minArea, out failReason, out _, out _);
    }

    private static bool ValidateExtractedLoop(TriangleContext triangle, List<LoopNode> loop, float epsilon, float minArea, out string failReason, out float loopUvArea, out List<float> fanUvAreas)
    {
        failReason = "none";
        loopUvArea = 0f;
        fanUvAreas = new List<float>();
        if (loop == null || loop.Count < 3) { failReason = "loop_vertex_count_lt3"; return false; }
        for (int i = 0; i < loop.Count; i++)
        {
            for (int j = i + 1; j < loop.Count; j++)
            {
                if (SameNode(loop[i], loop[j])) { failReason = "duplicate_node"; return false; }
                if ((GetNodeUv(triangle, loop[i]) - GetNodeUv(triangle, loop[j])).sqrMagnitude <= epsilon * epsilon)
                { failReason = "duplicate_uv"; return false; }
            }
        }
        var poly = new List<Vector2>(loop.Count);
        for (int i = 0; i < loop.Count; i++) poly.Add(GetNodeUv(triangle, loop[i]));
        float area = Mathf.Abs(SignedArea(poly));
        loopUvArea = area;
        if (area <= minArea) { failReason = "area_too_small"; return false; }
        if (PolygonHasSelfIntersection(poly, epsilon)) { failReason = "self_intersection"; return false; }
        for (int i = 1; i + 1 < loop.Count; i++)
        {
            Vector2 a = poly[0];
            Vector2 b = poly[i];
            Vector2 c = poly[i + 1];
            float triArea = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
            fanUvAreas.Add(triArea);
            if (triArea <= minArea) { failReason = "fan_triangle_invalid"; return false; }
        }
        return true;
    }

    private static string BuildOneLineDebugDump(TriangleContext triangle, IReadOnlyList<LoopSegment> boundarySegments, Dictionary<LoopNode, List<LoopNode>> adj, List<LoopNode> loop, float loopUvArea, List<float> fanUvAreas, float minArea)
    {
        var seg = new List<string>();
        if (boundarySegments != null) for (int i = 0; i < boundarySegments.Count; i++) seg.Add($"{FormatNode(boundarySegments[i].a)}->{FormatNode(boundarySegments[i].b)}");
        var deg = new List<string>();
        foreach (var kv in adj) deg.Add($"{FormatNode(kv.Key)}:deg={kv.Value.Count}");
        var nodes = new List<string>();
        if (loop != null)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var n = loop[i];
                Vector2 uv = GetNodeUv(triangle, n);
                nodes.Add($"{FormatNode(n)} edgeIndex={n.crossing.edgeIndex} t={n.crossing.t:F6} before={(n.crossing.isBeforeInside ? 1 : 0)} uv={uv}");
            }
        }
        string fan = fanUvAreas == null ? "" : string.Join(",", fanUvAreas);
        return $"boundarySegments=[{string.Join(",", seg)}] graphDegrees=[{string.Join(",", deg)}] loopNodeCount={(loop == null ? 0 : loop.Count)} loopNodes=[{string.Join(";", nodes)}] loopUvArea={loopUvArea} fanUvAreas=[{fan}] minAreaThreshold={minArea}";
    }

    private static string FormatNode(LoopNode n)
        => n.isOriginalVertex ? $"v{n.originalVertexId}" : $"x{n.activeCrossingIndex}";

    private static bool TryGetActiveNode(IReadOnlyDictionary<int, LoopNode> activeNodes, LocalCrossing crossing, out LoopNode node)
    {
        if (activeNodes != null)
        {
            foreach (var kv in activeNodes)
            {
                var c = kv.Value.crossing;
                if (c.edgeIndex == crossing.edgeIndex && Mathf.Abs(c.t - crossing.t) <= 1e-6f) { node = kv.Value; return true; }
            }
        }
        node = default;
        return false;
    }

    internal static bool TryBuildInsideBoundarySegments(
        TriangleContext triangle,
        List<EdgeInfo> edgeInfos,
        IReadOnlyCollection<LocalCrossing> activeCrossings,
        IReadOnlyDictionary<int, LoopNode> activeNodes,
        out List<LoopSegment> boundarySegments,
        out string failReason)
    {
        boundarySegments = new List<LoopSegment>();
        failReason = "none";
        if (edgeInfos == null) return true;

        for (int e = 0; e < edgeInfos.Count; e++)
        {
            var info = edgeInfos[e];
            var selected = new List<LocalCrossing>();
            if (info.crossings != null)
            {
                for (int i = 0; i < info.crossings.Count; i++)
                {
                    var c = info.crossings[i];
                    if (IsActiveCrossing(c, activeCrossings))
                    {
                        selected.Add(c);
                    }
                }
            }
            selected.Sort((x, y) => x.t.CompareTo(y.t));

            Vector2 startUv = GetVertexUv(triangle, info.edgeStart);
            Vector2 endUv = GetVertexUv(triangle, info.edgeEnd);
            bool currentInside = triangle.SampleInside != null && triangle.SampleInside(startUv);
            float prevT = 0f;
            LoopNode prevNode = MakeOriginalNode(info.edgeStart);

            for (int i = 0; i < selected.Count; i++)
            {
                var c = selected[i];
                if (currentInside != c.isBeforeInside)
                {
                    failReason = $"isBeforeInside_mismatch:e{info.edgeIndex}:idx{i}";
                    boundarySegments.Clear();
                    return false;
                }
                LoopNode curNode = MakeCrossingNode(c);
                if (activeNodes != null)
                {
                    foreach (var kv in activeNodes)
                    {
                        if (Mathf.Abs(kv.Value.crossing.t - c.t) <= 1e-6f && kv.Value.crossing.edgeIndex == c.edgeIndex)
                        {
                            curNode = kv.Value;
                            break;
                        }
                    }
                }
                if (currentInside)
                {
                    float segT = Mathf.Clamp01(c.t - prevT);
                    if (segT > 1e-6f) boundarySegments.Add(new LoopSegment(prevNode, curNode));
                }
                currentInside = !currentInside;
                prevT = c.t;
                prevNode = curNode;
            }

            if (currentInside)
            {
                float segT = Mathf.Clamp01(1f - prevT);
                if (segT > 1e-6f) boundarySegments.Add(new LoopSegment(prevNode, MakeOriginalNode(info.edgeEnd)));
            }
        }

        return true;
    }

    private static bool IsActiveCrossing(LocalCrossing candidate, IReadOnlyCollection<LocalCrossing> activeCrossings)
    {
        if (activeCrossings == null || activeCrossings.Count == 0) return false;
        foreach (var a in activeCrossings)
        {
            if (a.edgeIndex != candidate.edgeIndex) continue;
            if (a.edgeStart != candidate.edgeStart || a.edgeEnd != candidate.edgeEnd) continue;
            if (Mathf.Abs(a.t - candidate.t) > 1e-6f) continue;
            return true;
        }
        return false;
    }

    private static TriangleProcessResult MakeWholeByVertexMajority(TriangleContext triangle)
        => WholeByVertexMajority(triangle)
            ? new TriangleProcessResult(TriangleRoute.WholeKeep, true)
            : new TriangleProcessResult(TriangleRoute.WholeTrim, false);

    private static TriangleProcessResult MakeWholeBySevenPointMajority(TriangleContext triangle)
        => WholeBySevenPointMajority(triangle)
            ? new TriangleProcessResult(TriangleRoute.WholeKeep, true)
            : new TriangleProcessResult(TriangleRoute.WholeTrim, false);

    internal static bool WholeByVertexMajority(TriangleContext triangle)
    {
        int inside = 0;
        if (triangle.SampleInside(triangle.uv0)) inside++;
        if (triangle.SampleInside(triangle.uv1)) inside++;
        if (triangle.SampleInside(triangle.uv2)) inside++;
        return inside >= 2;
    }

    internal static bool WholeBySevenPointMajority(TriangleContext triangle)
    {
        int inside = 0;
        if (triangle.SampleInside(triangle.uv0)) inside++;
        if (triangle.SampleInside(triangle.uv1)) inside++;
        if (triangle.SampleInside(triangle.uv2)) inside++;
        if (inside >= 4) return true;

        Vector2 m01 = (triangle.uv0 + triangle.uv1) * 0.5f;
        Vector2 m12 = (triangle.uv1 + triangle.uv2) * 0.5f;
        Vector2 m20 = (triangle.uv2 + triangle.uv0) * 0.5f;
        Vector2 centroid = (triangle.uv0 + triangle.uv1 + triangle.uv2) / 3f;

        if (triangle.SampleInside(m01)) inside++;
        if (inside >= 4) return true;
        if (triangle.SampleInside(m12)) inside++;
        if (inside >= 4) return true;
        if (triangle.SampleInside(m20)) inside++;
        if (inside >= 4) return true;
        if (triangle.SampleInside(centroid)) inside++;

        return inside >= 4;
    }

    internal static TriangleProcessResult ProcessTwoOddEdgesAsOneLine(TriangleContext triangle, List<EdgeInfo> edgeInfos)
    {
        const float epsilon = 1e-6f;
        if (!TryPickRepresentativeCrossing(edgeInfos, EdgeParityClass.OddEdge, 0, triangle, out LocalCrossing a)
            || !TryPickRepresentativeCrossing(edgeInfos, EdgeParityClass.OddEdge, 1, triangle, out LocalCrossing b))
        {
            return MakeWholeBySevenPointMajority(triangle);
        }
        if (!IsOddRepresentativeConsistent(triangle, a) || !IsOddRepresentativeConsistent(triangle, b))
        {
            return MakeWholeBySevenPointMajority(triangle);
        }

        Vector2 aUv = GetLocalCrossingUv(triangle, a);
        Vector2 bUv = GetLocalCrossingUv(triangle, b);
        if ((aUv - bUv).sqrMagnitude <= epsilon * epsilon)
        {
            return MakeWholeBySevenPointMajority(triangle);
        }

        var active = new List<LocalCrossing> { a, b };
        var activeNodes = BuildActiveNodeMap(active);
        if (!TryBuildInsideBoundarySegments(triangle, edgeInfos, active, activeNodes, out var boundarySegments, out _))
            return MakeWholeBySevenPointMajority(triangle);
        if (!TryExtractInsideLoopsSingleChord(triangle, boundarySegments, activeNodes, new Chord(a, b), out var loops, out var extractFailReason, out var extractDebug))
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][OneLineLoopDebug] stage=extract failReason={extractFailReason} {extractDebug}");
            return MakeWholeBySevenPointMajority(triangle);
        }
        var polygons = ConvertLoopsToPolygonRefs(loops);
        if (!ValidateInsidePolygons(triangle, polygons, epsilon, out var polyFailReason, out var areas))
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][OneLineLoopDebug] stage=validate_inside_polygons failReason={polyFailReason} areas=[{string.Join(",", areas)}] minAreaThreshold=1e-10");
            return MakeWholeBySevenPointMajority(triangle);
        }
        return new TriangleProcessResult(TriangleRoute.TwoOddEdgesAsOneLine, a, b, default, default, true, false, polygons);
    }

    internal static bool TryExtractInsideLoopsSingleChord(
        TriangleContext triangle,
        IReadOnlyList<LoopSegment> boundarySegments,
        IReadOnlyDictionary<int, LoopNode> activeNodes,
        Chord chord,
        out LoopNode[][] loops,
        out string failReason,
        out string debugInfo)
    {
        loops = Array.Empty<LoopNode[]>();
        failReason = "none";
        debugInfo = string.Empty;
        const float epsilon = 1e-6f;
        const float minArea = 1e-10f;
        var graph = new LoopGraph();
        if (boundarySegments != null) for (int i = 0; i < boundarySegments.Count; i++) AddSegmentIfValid(triangle, graph, boundarySegments[i].a, boundarySegments[i].b, epsilon);
        if (!TryGetActiveNode(activeNodes, chord.a, out var ca) || !TryGetActiveNode(activeNodes, chord.b, out var cb) || !AddSegmentIfValid(triangle, graph, ca, cb, epsilon))
        { failReason = "invalid_chord"; debugInfo = $"chord={FormatCrossing(chord.a)}->{FormatCrossing(chord.b)}"; return false; }
        var adj = BuildAdjacency(graph);
        foreach (var kv in adj) if (kv.Value.Count != 2) { failReason = "open_path_or_degree_invalid"; debugInfo = BuildOneLineDebugDump(triangle, boundarySegments, adj, null, 0f, null, minArea); return false; }
        if (adj.Count == 0) { failReason = "empty_graph"; debugInfo = BuildOneLineDebugDump(triangle, boundarySegments, adj, null, 0f, null, minArea); return false; }
        var start = new List<LoopNode>(adj.Keys)[0];
        if (!TraceCycle(adj, start, out var loop, out failReason)) { debugInfo = BuildOneLineDebugDump(triangle, boundarySegments, adj, loop, 0f, null, minArea); return false; }
        if (!ValidateExtractedLoop(triangle, loop, epsilon, minArea, out failReason, out var loopUvArea, out var fanAreas))
        { debugInfo = BuildOneLineDebugDump(triangle, boundarySegments, adj, loop, loopUvArea, fanAreas, minArea); return false; }
        loops = new[] { loop.ToArray() };
        debugInfo = BuildOneLineDebugDump(triangle, boundarySegments, adj, loop, loopUvArea, fanAreas, minArea);
        return true;
    }

    internal static TriangleProcessResult ProcessTwoOddEdgesAndOneEvenEdge(TriangleContext triangle, List<EdgeInfo> edgeInfos)
    {
        int gt2 = 0;
        for (int i = 0; i < edgeInfos.Count; i++) if (edgeInfos[i].crossings.Count > 2) gt2++;
        if (gt2 == 3) return MakeWholeBySevenPointMajority(triangle);

        if (!TryPickRepresentativePair(edgeInfos, EdgeParityClass.EvenEdge, 0, out LocalCrossing a0, out LocalCrossing a1))
            return MakeWholeBySevenPointMajority(triangle);
        if (!TryPickRepresentativeCrossing(edgeInfos, EdgeParityClass.OddEdge, 0, triangle, out LocalCrossing s0))
            return MakeWholeBySevenPointMajority(triangle);
        if (!TryPickRepresentativeCrossing(edgeInfos, EdgeParityClass.OddEdge, 1, triangle, out LocalCrossing s1))
            return MakeWholeBySevenPointMajority(triangle);
        // Odd-edge representatives must be consistent with endpoint inside/outside states.
        // Even-edge representatives may come from edges whose endpoints are same-side, so skip that check for a0/a1.
        if (!IsOddRepresentativeConsistent(triangle, s0) || !IsOddRepresentativeConsistent(triangle, s1))
            return MakeWholeBySevenPointMajority(triangle);

        const float epsilon = 1e-6f;
        Vector2 a0Uv = GetLocalCrossingUv(triangle, a0);
        Vector2 a1Uv = GetLocalCrossingUv(triangle, a1);
        Vector2 s0Uv = GetLocalCrossingUv(triangle, s0);
        Vector2 s1Uv = GetLocalCrossingUv(triangle, s1);
        if ((a0Uv - s0Uv).sqrMagnitude <= epsilon * epsilon
            || (a1Uv - s1Uv).sqrMagnitude <= epsilon * epsilon
            || (a0Uv - s1Uv).sqrMagnitude <= epsilon * epsilon
            || (a1Uv - s0Uv).sqrMagnitude <= epsilon * epsilon)
            return MakeWholeBySevenPointMajority(triangle);

        var active = new List<LocalCrossing> { a0, a1, s0, s1 };
        var activeNodes = BuildActiveNodeMap(active);
        if (!TrySelectUniqueChordPairing(triangle, active, out var chord0, out var chord1, out _))
            return MakeWholeBySevenPointMajority(triangle);
        if (!TryBuildInsideBoundarySegments(triangle, edgeInfos, active, activeNodes, out var boundarySegments, out _))
            return MakeWholeBySevenPointMajority(triangle);
        if (!TryExtractInsideLoops(triangle, boundarySegments, activeNodes, chord0, chord1, out var loops, out _))
            return MakeWholeBySevenPointMajority(triangle);
        var polygons = ConvertLoopsToPolygonRefs(loops);
        if (!ValidateInsidePolygons(triangle, polygons, epsilon)) return MakeWholeBySevenPointMajority(triangle);
        bool middleInside = !a0.isBeforeInside && a1.isBeforeInside;
        bool direct = true;
        return new TriangleProcessResult(TriangleRoute.TwoOddEdgesAndOneEvenEdge, a0, a1, s0, s1, direct, middleInside, polygons);
    }

    internal static TriangleProcessResult ProcessTwoEvenEdges(TriangleContext triangle, List<EdgeInfo> edgeInfos)
    {
        if (!TryPickRepresentativePair(edgeInfos, EdgeParityClass.EvenEdge, 0, out LocalCrossing a0, out LocalCrossing a1))
        {
            Debug.Log("[NDMF VRoid Mesh Trimmer][TwoEvenFallback] reason=pick_even_pair_a_failed");
            return MakeWholeBySevenPointMajority(triangle);
        }
        if (!TryPickRepresentativePair(edgeInfos, EdgeParityClass.EvenEdge, 1, out LocalCrossing b0, out LocalCrossing b1))
        {
            Debug.Log("[NDMF VRoid Mesh Trimmer][TwoEvenFallback] reason=pick_even_pair_b_failed");
            return MakeWholeBySevenPointMajority(triangle);
        }
        // Even-edge route: endpoints can be same-side by definition, so odd consistency check does not apply.

        bool middleA = !a0.isBeforeInside && a1.isBeforeInside;
        bool middleB = !b0.isBeforeInside && b1.isBeforeInside;
        if (middleA != middleB)
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][TwoEvenFallback] reason=middle_mismatch middleA={middleA} middleB={middleB}");
            return MakeWholeBySevenPointMajority(triangle);
        }

        var active = new List<LocalCrossing> { a0, a1, b0, b1 };
        var activeNodes = BuildActiveNodeMap(active);
        if (!TrySelectUniqueChordPairing(triangle, active, out var chord0, out var chord1, out var pairingFailReason))
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][TwoEvenFallback] reason=pairing_failed detail={pairingFailReason} active={FormatCrossing(active[0])}|{FormatCrossing(active[1])}|{FormatCrossing(active[2])}|{FormatCrossing(active[3])}");
            return MakeWholeBySevenPointMajority(triangle);
        }

        const float epsilon = 1e-6f;
        if (!TryBuildInsideBoundarySegments(triangle, edgeInfos, active, activeNodes, out var boundarySegments, out var boundaryFailReason))
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][TwoEvenFallback] reason=boundary_failed detail={boundaryFailReason} boundaryCount={boundarySegments?.Count ?? 0}");
            return MakeWholeBySevenPointMajority(triangle);
        }
        if (!TryExtractInsideLoops(triangle, boundarySegments, activeNodes, chord0, chord1, out var loops, out var loopFailReason))
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][TwoEvenFallback] reason=extract_failed detail={loopFailReason} boundaryCount={boundarySegments.Count} chord0={FormatCrossing(chord0.a)}->{FormatCrossing(chord0.b)} chord1={FormatCrossing(chord1.a)}->{FormatCrossing(chord1.b)} loopCount={(loops == null ? 0 : loops.Length)}");
            return MakeWholeBySevenPointMajority(triangle);
        }
        var polygons = ConvertLoopsToPolygonRefs(loops);
        if (!ValidateInsidePolygons(triangle, polygons, epsilon, out var polyFailReason, out var areas))
        {
            Debug.Log($"[NDMF VRoid Mesh Trimmer][TwoEvenFallback] reason=validate_failed detail={polyFailReason} areas=[{string.Join(",", areas)}]");
            return MakeWholeBySevenPointMajority(triangle);
        }
        bool direct = true;
        return new TriangleProcessResult(TriangleRoute.TwoEvenEdges, a0, a1, b0, b1, direct, middleA, polygons);
    }

    private static string FormatCrossing(LocalCrossing c) => $"e{c.edgeIndex}:t={c.t:F6}:before={(c.isBeforeInside ? 1 : 0)}";
    private static Dictionary<int, LoopNode> BuildActiveNodeMap(IReadOnlyList<LocalCrossing> active)
    {
        var map = new Dictionary<int, LoopNode>();
        for (int i = 0; i < active.Count; i++) map[i] = MakeCrossingNode(active[i], i);
        return map;
    }

    private static TriangleProcessResult.PolygonVertexRef[][] ConvertLoopsToPolygonRefs(LoopNode[][] loops)
    {
        if (loops == null || loops.Length == 0) return Array.Empty<TriangleProcessResult.PolygonVertexRef[]>();
        var polys = new TriangleProcessResult.PolygonVertexRef[loops.Length][];
        for (int i = 0; i < loops.Length; i++)
        {
            var src = loops[i];
            if (src == null)
            {
                polys[i] = Array.Empty<TriangleProcessResult.PolygonVertexRef>();
                continue;
            }
            var dst = new TriangleProcessResult.PolygonVertexRef[src.Length];
            for (int k = 0; k < src.Length; k++)
            {
                dst[k] = src[k].isOriginalVertex
                    ? new TriangleProcessResult.PolygonVertexRef(src[k].originalVertexId)
                    : new TriangleProcessResult.PolygonVertexRef(src[k].crossing);
            }
            polys[i] = dst;
        }
        return polys;
    }

    private static bool TryPickRepresentativeCrossing(List<EdgeInfo> edgeInfos, EdgeParityClass parityClass, int parityOrdinal, TriangleContext triangle, out LocalCrossing crossing)
    {
        int seen = 0;
        for (int i = 0; i < edgeInfos.Count; i++)
        {
            var info = edgeInfos[i];
            if (info.parityClass != parityClass) continue;
            if (seen == parityOrdinal)
            {
                if (info.crossings.Count == 0)
                {
                    crossing = default;
                    return false;
                }

                if (parityClass == EdgeParityClass.OddEdge)
                {
                    if (!PickOddEdgeRepresentative(triangle, info, out crossing))
                    {
                        return false;
                    }
                    return true;
                }

                crossing = info.crossings[0];
                return true;
            }
            seen++;
        }

        crossing = default;
        return false;
    }

    private static bool PickOddEdgeRepresentative(TriangleContext triangle, EdgeInfo edgeInfo, out LocalCrossing crossing)
    {
        bool startInside = SampleVertexInside(triangle, edgeInfo.edgeStart);
        bool endInside = SampleVertexInside(triangle, edgeInfo.edgeEnd);
        if (startInside == endInside)
        {
            crossing = default;
            return false;
        }

        crossing = startInside
            ? edgeInfo.crossings[edgeInfo.crossings.Count - 1]
            : edgeInfo.crossings[0];
        return true;
    }

    internal static bool PickEvenEdgeMinMax(EdgeInfo edgeInfo, out LocalCrossing minCrossing, out LocalCrossing maxCrossing, float epsilon = 1e-6f)
    {
        minCrossing = default;
        maxCrossing = default;
        if (edgeInfo.crossings == null || edgeInfo.crossings.Count < 2)
        {
            return false;
        }

        minCrossing = edgeInfo.crossings[0];
        maxCrossing = edgeInfo.crossings[edgeInfo.crossings.Count - 1];
        return Mathf.Abs(maxCrossing.t - minCrossing.t) > epsilon;
    }

    private static bool SampleVertexInside(TriangleContext triangle, int vertexId)
    {
        return triangle.SampleInside(GetVertexUv(triangle, vertexId));
    }

    private static Vector2 GetVertexUv(TriangleContext triangle, int vertexId)
    {
        if (vertexId == triangle.v0) return triangle.uv0;
        if (vertexId == triangle.v1) return triangle.uv1;
        return triangle.uv2;
    }

    internal static Vector2 GetLocalCrossingUv(TriangleContext triangle, LocalCrossing crossing)
    {
        Vector2 s = GetVertexUv(triangle, crossing.edgeStart);
        Vector2 e = GetVertexUv(triangle, crossing.edgeEnd);
        return Vector2.LerpUnclamped(s, e, crossing.t);
    }

    private static bool IsOddRepresentativeConsistent(TriangleContext triangle, LocalCrossing crossing)
    {
        bool startInside = SampleVertexInside(triangle, crossing.edgeStart);
        bool endInside = SampleVertexInside(triangle, crossing.edgeEnd);
        if (startInside == endInside) return false;
        int expectedInside = startInside ? crossing.edgeStart : crossing.edgeEnd;
        return GetInsideEndpointForCrossing(crossing) == expectedInside;
    }

    private static bool SegmentsProperlyIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float epsilon)
    {
        float o1 = Orient(a, b, c);
        float o2 = Orient(a, b, d);
        float o3 = Orient(c, d, a);
        float o4 = Orient(c, d, b);
        bool abStraddles = (o1 > epsilon && o2 < -epsilon) || (o1 < -epsilon && o2 > epsilon);
        bool cdStraddles = (o3 > epsilon && o4 < -epsilon) || (o3 < -epsilon && o4 > epsilon);
        return abStraddles && cdStraddles;
    }

    private static float Orient(Vector2 a, Vector2 b, Vector2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    private static bool TrySelectUniquePairing(TriangleContext triangle, LocalCrossing[] c, out int p0a, out int p0b, out int p1a, out int p1b)
    {
        p0a = p0b = p1a = p1b = -1;
        var pairings = new (int a, int b, int c0, int d)[] { (0, 1, 2, 3), (0, 2, 1, 3), (0, 3, 1, 2) };
        int validCount = 0;
        const float eps = 1e-6f;
        for (int i = 0; i < pairings.Length; i++)
        {
            var p = pairings[i];
            if (c[p.a].edgeIndex == c[p.b].edgeIndex || c[p.c0].edgeIndex == c[p.d].edgeIndex) continue;
            Vector2 a0 = GetLocalCrossingUv(triangle, c[p.a]);
            Vector2 a1 = GetLocalCrossingUv(triangle, c[p.b]);
            Vector2 b0 = GetLocalCrossingUv(triangle, c[p.c0]);
            Vector2 b1 = GetLocalCrossingUv(triangle, c[p.d]);
            if (SegmentsProperlyIntersect(a0, a1, b0, b1, eps)) continue;
            validCount++;
            p0a = p.a; p0b = p.b; p1a = p.c0; p1b = p.d;
        }
        return validCount == 1;
    }

    private static TriangleProcessResult.PolygonVertexRef[][] BuildInsidePolygonsForTwoOddOneEven(TriangleContext triangle, LocalCrossing a0, LocalCrossing a1, LocalCrossing s0, LocalCrossing s1, bool direct, bool middleInside)
    {
        var c0 = direct ? s0 : s1;
        var c1 = direct ? s1 : s0;
        if (middleInside)
        {
            return new[]
            {
                new[]
                {
                    new TriangleProcessResult.PolygonVertexRef(a0),
                    new TriangleProcessResult.PolygonVertexRef(c0),
                    new TriangleProcessResult.PolygonVertexRef(c1),
                    new TriangleProcessResult.PolygonVertexRef(a1)
                }
            };
        }

        return new[]
        {
            new[] { new TriangleProcessResult.PolygonVertexRef(a0), new TriangleProcessResult.PolygonVertexRef(c0), new TriangleProcessResult.PolygonVertexRef(GetInsideEndpointForCrossing(c0)) },
            new[] { new TriangleProcessResult.PolygonVertexRef(a1), new TriangleProcessResult.PolygonVertexRef(c1), new TriangleProcessResult.PolygonVertexRef(GetInsideEndpointForCrossing(c1)) }
        };
    }

    private static TriangleProcessResult.PolygonVertexRef[][] BuildInsidePolygonsForTwoEven(LocalCrossing a0, LocalCrossing a1, LocalCrossing b0, LocalCrossing b1, bool direct, bool middleInside)
    {
        var p0 = direct ? b0 : b1;
        var p1 = direct ? b1 : b0;
        if (middleInside)
        {
            return new[]
            {
                new[]
                {
                    new TriangleProcessResult.PolygonVertexRef(a0),
                    new TriangleProcessResult.PolygonVertexRef(p0),
                    new TriangleProcessResult.PolygonVertexRef(p1),
                    new TriangleProcessResult.PolygonVertexRef(a1)
                }
            };
        }

        return new[]
        {
            new[] { new TriangleProcessResult.PolygonVertexRef(a0), new TriangleProcessResult.PolygonVertexRef(p0), new TriangleProcessResult.PolygonVertexRef(GetInsideEndpointForCrossing(a0)) },
            new[] { new TriangleProcessResult.PolygonVertexRef(a1), new TriangleProcessResult.PolygonVertexRef(p1), new TriangleProcessResult.PolygonVertexRef(GetInsideEndpointForCrossing(a1)) }
        };
    }

    private static int GetInsideEndpointForCrossing(LocalCrossing c) => c.isBeforeInside ? c.edgeStart : c.edgeEnd;

    private static bool ValidateInsidePolygons(TriangleContext triangle, TriangleProcessResult.PolygonVertexRef[][] polygons, float epsilon)
    {
        return ValidateInsidePolygons(triangle, polygons, epsilon, out _, out _);
    }

    private static bool ValidateInsidePolygons(TriangleContext triangle, TriangleProcessResult.PolygonVertexRef[][] polygons, float epsilon, out string failReason, out List<string> areas)
    {
        failReason = "none";
        areas = new List<string>();
        if (polygons == null || polygons.Length == 0) { failReason = "polygon_empty"; return false; }
        for (int i = 0; i < polygons.Length; i++)
        {
            var poly = polygons[i];
            if (poly == null || poly.Length < 3) { failReason = "vertex_count_lt3"; return false; }
            var uv = new List<Vector2>(poly.Length);
            for (int k = 0; k < poly.Length; k++)
            {
                Vector2 p = poly[k].isOriginalVertex ? GetVertexUv(triangle, poly[k].originalVertexId) : GetLocalCrossingUv(triangle, poly[k].crossing);
                uv.Add(p);
            }
            SimplifyPolygonUvs(uv, epsilon, out _, out _);
            if (uv.Count < 3) { failReason = "vertex_count_lt3_after_simplify"; return false; }

            for (int a = 0; a < uv.Count; a++)
            {
                for (int b = a + 1; b < uv.Count; b++)
                {
                    if ((uv[a] - uv[b]).sqrMagnitude <= epsilon * epsilon) { failReason = "duplicate_uv"; return false; }
                }
            }

            float area = Mathf.Abs(SignedArea(uv));
            areas.Add(area.ToString("F8"));
            if (area <= epsilon) { failReason = "area_too_small"; return false; }
            if (PolygonHasSelfIntersection(uv, epsilon)) { failReason = "self_intersection"; return false; }
            for (int t = 1; t + 1 < uv.Count; t++)
            {
                float ta = Mathf.Abs((uv[t].x - uv[0].x) * (uv[t + 1].y - uv[0].y) - (uv[t + 1].x - uv[0].x) * (uv[t].y - uv[0].y)) * 0.5f;
                if (ta <= epsilon) { failReason = "fan_triangle_invalid"; return false; }
            }
        }
        return true;
    }

    private static void SimplifyPolygonUvs(List<Vector2> uv, float epsilon, out int removedAdjacent, out int removedCollinear)
    {
        removedAdjacent = 0;
        removedCollinear = 0;
        if (uv == null) return;
        bool changed = true;
        while (changed && uv.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < uv.Count; i++)
            {
                int j = (i + 1) % uv.Count;
                if ((uv[i] - uv[j]).sqrMagnitude <= epsilon * epsilon)
                {
                    uv.RemoveAt(j);
                    removedAdjacent++;
                    changed = true;
                    break;
                }
            }
        }
        changed = true;
        while (changed && uv.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < uv.Count; i++)
            {
                int prev = (i - 1 + uv.Count) % uv.Count;
                int next = (i + 1) % uv.Count;
                Vector2 a = uv[prev];
                Vector2 b = uv[i];
                Vector2 c = uv[next];
                float area2 = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));
                if (area2 <= epsilon)
                {
                    uv.RemoveAt(i);
                    removedCollinear++;
                    changed = true;
                    break;
                }
            }
        }
    }

    private static float SignedArea(List<Vector2> poly)
    {
        float a = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i];
            Vector2 q = poly[(i + 1) % poly.Count];
            a += p.x * q.y - q.x * p.y;
        }
        return a * 0.5f;
    }

    private static bool PolygonHasSelfIntersection(List<Vector2> poly, float epsilon)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[(i + 1) % poly.Count];
            for (int j = i + 1; j < poly.Count; j++)
            {
                if (Mathf.Abs(i - j) <= 1) continue;
                if (i == 0 && j == poly.Count - 1) continue;
                Vector2 c = poly[j];
                Vector2 d = poly[(j + 1) % poly.Count];
                if (SegmentsProperlyIntersect(a, b, c, d, epsilon)) return true;
            }
        }
        return false;
    }


    private static bool TryPickRepresentativePair(List<EdgeInfo> edgeInfos, EdgeParityClass parityClass, int parityOrdinal, out LocalCrossing first, out LocalCrossing second)
    {
        first = default;
        second = default;

        int seen = 0;
        for (int i = 0; i < edgeInfos.Count; i++)
        {
            var info = edgeInfos[i];
            if (info.parityClass != parityClass) continue;
            if (seen == parityOrdinal)
            {
                return PickEvenEdgeMinMax(info, out first, out second);
            }
            seen++;
        }

        return false;
    }

    private static EdgeParityClass ClassifyEdge(int crossingCount)
    {
        if (crossingCount == 0) return EdgeParityClass.ZeroEdge;
        if ((crossingCount & 1) == 1) return EdgeParityClass.OddEdge;
        return EdgeParityClass.EvenEdge;
    }

    private static void NormalizeCrossingsInPlace(List<LocalCrossing> crossings)
    {
        if (crossings == null || crossings.Count == 0) return;
        const float endpointEps = 1e-4f;
        const float pairEps = 1e-3f;
        crossings.RemoveAll(c => c.t <= endpointEps || c.t >= 1f - endpointEps);
        for (int i = crossings.Count - 2; i >= 0; i--)
        {
            if (Mathf.Abs(crossings[i + 1].t - crossings[i].t) < pairEps)
            {
                crossings.RemoveAt(i + 1);
                crossings.RemoveAt(i);
                i--;
            }
        }
    }

    private static void AppendEdgeLocalCrossings(TriangleContext triangle, int edgeIndex, int edgeStart, int edgeEnd, List<LocalCrossing> dst)
    {
        if (triangle.sharedCrossings == null) return;

        var key = new EdgeKey(edgeStart, edgeEnd);
        if (!triangle.sharedCrossings.TryGetValue(key, out var sharedList) || sharedList == null) return;

        bool sameDirectionAsCanonical = key.a == edgeStart && key.b == edgeEnd;
        for (int i = 0; i < sharedList.Count; i++)
        {
            EdgeCrossing shared = sharedList[i];
            dst.Add(new LocalCrossing
            {
                edgeIndex = edgeIndex,
                edgeStart = edgeStart,
                edgeEnd = edgeEnd,
                t = sameDirectionAsCanonical ? shared.t : 1f - shared.t,
                isBeforeInside = sameDirectionAsCanonical ? shared.isBeforeInside : !shared.isBeforeInside
            });
        }
    }
}
