using System;
using System.Collections.Generic;

public class EdgeIntersectionCache
{
    private readonly Dictionary<EdgeKey, int> _cache = new Dictionary<EdgeKey, int>();

    public bool TryGet(int insideVertexIndex, int outsideVertexIndex, out int interpolatedVertexIndex)
    {
        return _cache.TryGetValue(new EdgeKey(insideVertexIndex, outsideVertexIndex), out interpolatedVertexIndex);
    }

    public void Set(int insideVertexIndex, int outsideVertexIndex, int interpolatedVertexIndex)
    {
        _cache[new EdgeKey(insideVertexIndex, outsideVertexIndex)] = interpolatedVertexIndex;
    }

    private readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        private readonly int _inside;
        private readonly int _outside;

        public EdgeKey(int inside, int outside)
        {
            _inside = inside;
            _outside = outside;
        }

        public bool Equals(EdgeKey other)
        {
            return _inside == other._inside && _outside == other._outside;
        }

        public override bool Equals(object obj)
        {
            return obj is EdgeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (_inside * 397) ^ _outside;
            }
        }
    }
}
