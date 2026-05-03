using System;
using System.Collections.Generic;
using UnityEngine;

public readonly struct EdgeKey : IEquatable<EdgeKey>
{
    public readonly int a;
    public readonly int b;

    public EdgeKey(int vertexIndex0, int vertexIndex1)
    {
        a = Math.Min(vertexIndex0, vertexIndex1);
        b = Math.Max(vertexIndex0, vertexIndex1);
    }

    public bool Equals(EdgeKey other)
    {
        return a == other.a && b == other.b;
    }

    public override bool Equals(object obj)
    {
        return obj is EdgeKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (a * 397) ^ b;
        }
    }
}

public struct EdgeCrossing
{
    public float tCanonical;
    public int vertexIndex;
    public Vector2 uv;
    public MeshTrimProcessor.VertexData vertexData;
    public MeshTrimProcessor.VertexSource weightedSource;
}

public class EdgeCrossingCache
{
    private readonly Dictionary<EdgeKey, List<EdgeCrossing>> _cache = new Dictionary<EdgeKey, List<EdgeCrossing>>();

    public int CreatedCount { get; private set; }
    public int HitCount { get; private set; }
    public int ReusedCount { get; private set; }

    public List<EdgeCrossing> GetOrCreateEdgeCrossings(EdgeKey edgeKey)
    {
        if (_cache.TryGetValue(edgeKey, out var crossings))
        {
            HitCount++;
            if (crossings.Count > 0)
            {
                ReusedCount++;
            }
            return crossings;
        }

        crossings = new List<EdgeCrossing>(1);
        _cache[edgeKey] = crossings;
        CreatedCount++;
        return crossings;
    }
}
