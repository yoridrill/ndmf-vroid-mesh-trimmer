using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class EdgeCrossingTrimRouterTests
{
    private static EdgeCrossingTrimRouter.TriangleContext MakeContext(Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>> map)
    {
        return new EdgeCrossingTrimRouter.TriangleContext
        {
            v0 = 0,
            v1 = 1,
            v2 = 2,
            uv0 = new Vector2(0, 0),
            uv1 = new Vector2(1, 0),
            uv2 = new Vector2(0, 1),
            sharedCrossings = map,
            SampleInside = uv => uv.x + uv.y > 0.5f
        };
    }

    private static EdgeCrossingTrimRouter.EdgeCrossing C(int a, int b, float t, bool before)
        => new EdgeCrossingTrimRouter.EdgeCrossing { edge = new EdgeCrossingTrimRouter.EdgeKey(a, b), t = t, isBeforeInside = before };

    private static Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>> BuildCounts(int c01, int c12, int c20)
    {
        var map = new Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>>();
        AddEdge(map, 0, 1, c01);
        AddEdge(map, 1, 2, c12);
        AddEdge(map, 2, 0, c20);
        return map;
    }

    private static void AddEdge(Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>> map, int a, int b, int count)
    {
        if (count <= 0) return;
        var list = new List<EdgeCrossingTrimRouter.EdgeCrossing>(count);
        for (int i = 0; i < count; i++)
        {
            float t = (i + 1f) / (count + 1f);
            bool before = (i % 2) == 0;
            list.Add(C(a, b, t, before));
        }
        map[new EdgeCrossingTrimRouter.EdgeKey(a, b)] = list;
    }

    [TestCase(0,0,0,true)]
    [TestCase(2,0,0,true)]
    [TestCase(4,0,0,true)]
    [TestCase(1,1,0,false)]
    [TestCase(3,1,0,false)]
    [TestCase(3,3,0,false)]
    [TestCase(2,1,1,false)]
    [TestCase(3,2,1,false)]
    [TestCase(4,1,1,false)]
    [TestCase(4,3,1,false)]
    [TestCase(6,3,5,true)]
    [TestCase(2,2,0,false)]
    [TestCase(4,2,0,false)]
    [TestCase(4,4,0,false)]
    [TestCase(2,2,2,true)]
    [TestCase(1,0,0,true)]
    [TestCase(1,1,1,true)]
    [TestCase(3,3,3,true)]
    public void Classification_RoutesExpectedBuckets(int c01, int c12, int c20, bool expectWholeByMajority)
    {
        var route = EdgeCrossingTrimRouter.ProcessTriangle(MakeContext(BuildCounts(c01, c12, c20))).route;
        if (expectWholeByMajority)
        {
            Assert.IsTrue(route == EdgeCrossingTrimRouter.TriangleRoute.WholeKeep || route == EdgeCrossingTrimRouter.TriangleRoute.WholeTrim);
            return;
        }

        int odd = ((c01 % 2 == 1) ? 1 : 0) + ((c12 % 2 == 1) ? 1 : 0) + ((c20 % 2 == 1) ? 1 : 0);
        int even = ((c01 > 0 && c01 % 2 == 0) ? 1 : 0) + ((c12 > 0 && c12 % 2 == 0) ? 1 : 0) + ((c20 > 0 && c20 % 2 == 0) ? 1 : 0);
        if (odd == 2 && even == 0) Assert.AreEqual(EdgeCrossingTrimRouter.TriangleRoute.TwoOddEdgesAsOneLine, route);
        else if (odd == 2 && even == 1) Assert.AreEqual(EdgeCrossingTrimRouter.TriangleRoute.TwoOddEdgesAndOneEvenEdge, route);
        else if (odd == 0 && even == 2) Assert.AreEqual(EdgeCrossingTrimRouter.TriangleRoute.TwoEvenEdges, route);
    }

    [Test]
    public void OddRepresentative_SelectsOutsideNearest()
    {
        var map = new Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>>
        {
            [new EdgeCrossingTrimRouter.EdgeKey(0,1)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(0,1,0.1f,false), C(0,1,0.8f,true), C(0,1,0.9f,false) },
            [new EdgeCrossingTrimRouter.EdgeKey(1,2)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(1,2,0.3f,false) },
        };
        var r = EdgeCrossingTrimRouter.ProcessTriangle(MakeContext(map));
        Assert.AreEqual(EdgeCrossingTrimRouter.TriangleRoute.TwoOddEdgesAsOneLine, r.route);
        Assert.IsTrue(r.hasOneLineSplit);
    }

    [Test]
    public void OddRepresentative_InconsistentEndpoints_FallsBack()
    {
        // edge 0-1 vertices both inside with this sampler -> inconsistent odd edge.
        var map = BuildCounts(1, 1, 0);
        var ctx = MakeContext(map);
        ctx.SampleInside = _ => true;
        var result = EdgeCrossingTrimRouter.ProcessTriangle(ctx);
        Assert.IsTrue(result.route == EdgeCrossingTrimRouter.TriangleRoute.WholeKeep || result.route == EdgeCrossingTrimRouter.TriangleRoute.WholeTrim);
    }

    [Test]
    public void EvenRepresentative_MinMaxTooClose_FallsBack()
    {
        var map = new Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>>
        {
            [new EdgeCrossingTrimRouter.EdgeKey(0,1)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(0,1,0.5f,false), C(0,1,0.5000001f,true) },
            [new EdgeCrossingTrimRouter.EdgeKey(1,2)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(1,2,0.2f,false), C(1,2,0.8f,true) },
        };
        var result = EdgeCrossingTrimRouter.ProcessTriangle(MakeContext(map));
        Assert.IsTrue(result.route == EdgeCrossingTrimRouter.TriangleRoute.WholeKeep || result.route == EdgeCrossingTrimRouter.TriangleRoute.WholeTrim);
    }

    [Test]
    public void EvenPair_MiddleMismatch_FallsBack()
    {
        var map = new Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>>
        {
            [new EdgeCrossingTrimRouter.EdgeKey(0,1)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(0,1,0.2f,true), C(0,1,0.8f,false) },
            [new EdgeCrossingTrimRouter.EdgeKey(1,2)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(1,2,0.2f,false), C(1,2,0.8f,false) },
        };
        var r = EdgeCrossingTrimRouter.ProcessTriangle(MakeContext(map));
        Assert.IsTrue(r.route == EdgeCrossingTrimRouter.TriangleRoute.WholeKeep || r.route == EdgeCrossingTrimRouter.TriangleRoute.WholeTrim);
    }

    [Test]
    public void TwoOddOneEven_DirectOrCross_IsUnique()
    {
        var map = new Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>>
        {
            [new EdgeCrossingTrimRouter.EdgeKey(0,1)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(0,1,0.25f,false), C(0,1,0.75f,true) }, // even
            [new EdgeCrossingTrimRouter.EdgeKey(1,2)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(1,2,0.3f,false) }, // odd
            [new EdgeCrossingTrimRouter.EdgeKey(2,0)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(2,0,0.4f,true) },  // odd
        };
        var r = EdgeCrossingTrimRouter.ProcessTriangle(MakeContext(map));
        Assert.AreEqual(EdgeCrossingTrimRouter.TriangleRoute.TwoOddEdgesAndOneEvenEdge, r.route);
        Assert.IsTrue(r.hasTwoLineSplit);
    }

    [Test]
    public void TwoEvenEdges_ChordIntersection_FallsBack()
    {
        var map = new Dictionary<EdgeCrossingTrimRouter.EdgeKey, List<EdgeCrossingTrimRouter.EdgeCrossing>>
        {
            [new EdgeCrossingTrimRouter.EdgeKey(0,1)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(0,1,0.2f,false), C(0,1,0.8f,true) },
            [new EdgeCrossingTrimRouter.EdgeKey(1,2)] = new List<EdgeCrossingTrimRouter.EdgeCrossing>{ C(1,2,0.2f,true), C(1,2,0.8f,false) },
        };
        var r = EdgeCrossingTrimRouter.ProcessTriangle(MakeContext(map));
        Assert.IsTrue(r.route == EdgeCrossingTrimRouter.TriangleRoute.WholeKeep || r.route == EdgeCrossingTrimRouter.TriangleRoute.WholeTrim);
    }

    [Test]
    public void SplitPayload_CrossingsStayOnTriangleEdges()
    {
        var mapOneLine = BuildCounts(1, 1, 0);
        var one = EdgeCrossingTrimRouter.ProcessTriangle(MakeContext(mapOneLine));
        Assert.IsTrue(one.hasOneLineSplit);
        Assert.IsTrue(IsTriangleEdge(one.splitCrossingA.edgeStart, one.splitCrossingA.edgeEnd));
        Assert.IsTrue(IsTriangleEdge(one.splitCrossingB.edgeStart, one.splitCrossingB.edgeEnd));

        var mapTwoLine = BuildCounts(2, 1, 1);
        var two = EdgeCrossingTrimRouter.ProcessTriangle(MakeContext(mapTwoLine));
        Assert.IsTrue(two.hasTwoLineSplit);
        Assert.IsTrue(IsTriangleEdge(two.evenCrossingMin.edgeStart, two.evenCrossingMin.edgeEnd));
        Assert.IsTrue(IsTriangleEdge(two.evenCrossingMax.edgeStart, two.evenCrossingMax.edgeEnd));
        Assert.IsTrue(IsTriangleEdge(two.oddCrossing0.edgeStart, two.oddCrossing0.edgeEnd));
        Assert.IsTrue(IsTriangleEdge(two.oddCrossing1.edgeStart, two.oddCrossing1.edgeEnd));
    }

    private static bool IsTriangleEdge(int a, int b)
    {
        return (a == 0 && b == 1) || (a == 1 && b == 0)
            || (a == 1 && b == 2) || (a == 2 && b == 1)
            || (a == 2 && b == 0) || (a == 0 && b == 2);
    }
}
