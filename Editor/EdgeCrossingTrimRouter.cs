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
        public int InsideVertex => isBeforeInside ? edgeStart : edgeEnd;
    }

    internal struct EdgeInfo
    {
        public int edgeIndex;
        public int edgeStart;
        public int edgeEnd;
        public List<LocalCrossing> crossings;
        public EdgeParityClass parityClass;
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

        if (odd == 2 && even == 0) return ProcessTwoOddEdgesAsOneLine(triangle, edgeInfos);
        if (odd == 2 && even == 1) return ProcessTwoOddEdgesAndOneEvenEdge(triangle, edgeInfos);
        if (odd == 0 && (even == 0 || even == 1)) return MakeWholeByVertexMajority(triangle);
        if (odd == 0 && even == 2) return ProcessTwoEvenEdges(triangle, edgeInfos);
        if ((odd == 0 && even == 3) || odd == 1 || odd == 3) return MakeWholeBySevenPointMajority(triangle);

        return MakeWholeBySevenPointMajority(triangle);
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

        Vector2 m01 = (triangle.uv0 + triangle.uv1) * 0.5f;
        Vector2 m12 = (triangle.uv1 + triangle.uv2) * 0.5f;
        Vector2 m20 = (triangle.uv2 + triangle.uv0) * 0.5f;
        Vector2 centroid = (triangle.uv0 + triangle.uv1 + triangle.uv2) / 3f;

        if (triangle.SampleInside(m01)) inside++;
        if (triangle.SampleInside(m12)) inside++;
        if (triangle.SampleInside(m20)) inside++;
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

        int[] keptInsideVertices = a.InsideVertex == b.InsideVertex
            ? new[] { a.InsideVertex }
            : new[] { a.InsideVertex, b.InsideVertex };

        return new TriangleProcessResult(a, b, keptInsideVertices);
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

        bool direct = a0.InsideVertex == s0.InsideVertex && a1.InsideVertex == s1.InsideVertex;
        bool cross = a0.InsideVertex == s1.InsideVertex && a1.InsideVertex == s0.InsideVertex;
        if (direct == cross) return MakeWholeBySevenPointMajority(triangle);

        Vector2 chord1A = a0Uv;
        Vector2 chord1B = direct ? s0Uv : s1Uv;
        Vector2 chord2A = a1Uv;
        Vector2 chord2B = direct ? s1Uv : s0Uv;
        if (SegmentsProperlyIntersect(chord1A, chord1B, chord2A, chord2B, epsilon))
            return MakeWholeBySevenPointMajority(triangle);

        bool middleInside = !a0.isBeforeInside && a1.isBeforeInside;
        var polygons = BuildInsidePolygonsForTwoOddOneEven(triangle, a0, a1, s0, s1, direct, middleInside);
        return polygons == null ? MakeWholeBySevenPointMajority(triangle) : new TriangleProcessResult(TriangleRoute.TwoOddEdgesAndOneEvenEdge, a0, a1, s0, s1, direct, middleInside, polygons);
    }

    internal static TriangleProcessResult ProcessTwoEvenEdges(TriangleContext triangle, List<EdgeInfo> edgeInfos)
    {
        if (!TryPickRepresentativePair(edgeInfos, EdgeParityClass.EvenEdge, 0, out LocalCrossing a0, out LocalCrossing a1))
            return MakeWholeBySevenPointMajority(triangle);
        if (!TryPickRepresentativePair(edgeInfos, EdgeParityClass.EvenEdge, 1, out LocalCrossing b0, out LocalCrossing b1))
            return MakeWholeBySevenPointMajority(triangle);
        // Even-edge route: endpoints can be same-side by definition, so odd consistency check does not apply.

        bool middleA = !a0.isBeforeInside && a1.isBeforeInside;
        bool middleB = !b0.isBeforeInside && b1.isBeforeInside;
        if (middleA != middleB) return MakeWholeBySevenPointMajority(triangle);

        bool direct = a0.InsideVertex == b0.InsideVertex && a1.InsideVertex == b1.InsideVertex;
        bool cross = a0.InsideVertex == b1.InsideVertex && a1.InsideVertex == b0.InsideVertex;
        if (direct == cross) return MakeWholeBySevenPointMajority(triangle);

        const float epsilon = 1e-6f;
        Vector2 a0Uv = GetLocalCrossingUv(triangle, a0);
        Vector2 a1Uv = GetLocalCrossingUv(triangle, a1);
        Vector2 b0Uv = GetLocalCrossingUv(triangle, b0);
        Vector2 b1Uv = GetLocalCrossingUv(triangle, b1);

        Vector2 chord1A = a0Uv;
        Vector2 chord1B = direct ? b0Uv : b1Uv;
        Vector2 chord2A = a1Uv;
        Vector2 chord2B = direct ? b1Uv : b0Uv;
        if ((chord1A - chord1B).sqrMagnitude <= epsilon * epsilon || (chord2A - chord2B).sqrMagnitude <= epsilon * epsilon)
            return MakeWholeBySevenPointMajority(triangle);

        var polygons = BuildInsidePolygonsForTwoEven(a0, a1, b0, b1, direct, middleA);
        return polygons == null ? MakeWholeBySevenPointMajority(triangle) : new TriangleProcessResult(TriangleRoute.TwoEvenEdges, a0, a1, b0, b1, direct, middleA, polygons);
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

    private static Vector2 GetLocalCrossingUv(TriangleContext triangle, LocalCrossing crossing)
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
        return crossing.InsideVertex == expectedInside;
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
            new[] { new TriangleProcessResult.PolygonVertexRef(a0), new TriangleProcessResult.PolygonVertexRef(c0), new TriangleProcessResult.PolygonVertexRef(c0.InsideVertex) },
            new[] { new TriangleProcessResult.PolygonVertexRef(a1), new TriangleProcessResult.PolygonVertexRef(c1), new TriangleProcessResult.PolygonVertexRef(c1.InsideVertex) }
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
            new[] { new TriangleProcessResult.PolygonVertexRef(a0), new TriangleProcessResult.PolygonVertexRef(p0), new TriangleProcessResult.PolygonVertexRef(a0.InsideVertex) },
            new[] { new TriangleProcessResult.PolygonVertexRef(a1), new TriangleProcessResult.PolygonVertexRef(p1), new TriangleProcessResult.PolygonVertexRef(a1.InsideVertex) }
        };
    }

    private static bool ValidateInsidePolygons(TriangleContext triangle, TriangleProcessResult.PolygonVertexRef[][] polygons, float epsilon)
    {
        if (polygons == null || polygons.Length == 0) return false;
        for (int i = 0; i < polygons.Length; i++)
        {
            var poly = polygons[i];
            if (poly == null || poly.Length < 3) return false;
            var uv = new List<Vector2>(poly.Length);
            for (int k = 0; k < poly.Length; k++)
            {
                Vector2 p = poly[k].isOriginalVertex ? GetVertexUv(triangle, poly[k].originalVertexId) : GetLocalCrossingUv(triangle, poly[k].crossing);
                uv.Add(p);
            }

            if (Mathf.Abs(SignedArea(uv)) <= epsilon) return false;
            if (PolygonHasSelfIntersection(uv, epsilon)) return false;
        }
        return true;
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
