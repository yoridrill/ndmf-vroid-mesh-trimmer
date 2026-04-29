using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class MeshAttributeInterpolator
{
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Vector3.LerpUnclamped(a, b, t);
    public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => Vector4.LerpUnclamped(a, b, t);
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => Vector2.LerpUnclamped(a, b, t);
    public static Color Lerp(Color a, Color b, float t) => Color.LerpUnclamped(a, b, t);

    public static BoneWeight Lerp(BoneWeight a, BoneWeight b, float t)
    {
        Dictionary<int, float> weightByBone = new Dictionary<int, float>();

        AddWeight(weightByBone, a.boneIndex0, a.weight0 * (1f - t));
        AddWeight(weightByBone, a.boneIndex1, a.weight1 * (1f - t));
        AddWeight(weightByBone, a.boneIndex2, a.weight2 * (1f - t));
        AddWeight(weightByBone, a.boneIndex3, a.weight3 * (1f - t));

        AddWeight(weightByBone, b.boneIndex0, b.weight0 * t);
        AddWeight(weightByBone, b.boneIndex1, b.weight1 * t);
        AddWeight(weightByBone, b.boneIndex2, b.weight2 * t);
        AddWeight(weightByBone, b.boneIndex3, b.weight3 * t);

        var top = weightByBone
            .Where(kv => kv.Value > 0f)
            .OrderByDescending(kv => kv.Value)
            .Take(4)
            .ToList();

        while (top.Count < 4)
        {
            top.Add(new KeyValuePair<int, float>(0, 0f));
        }

        float sum = top[0].Value + top[1].Value + top[2].Value + top[3].Value;
        if (sum <= 0f)
        {
            return a;
        }

        BoneWeight result = new BoneWeight
        {
            boneIndex0 = top[0].Key,
            boneIndex1 = top[1].Key,
            boneIndex2 = top[2].Key,
            boneIndex3 = top[3].Key,
            weight0 = top[0].Value / sum,
            weight1 = top[1].Value / sum,
            weight2 = top[2].Value / sum,
            weight3 = top[3].Value / sum
        };

        return result;
    }

    private static void AddWeight(Dictionary<int, float> map, int boneIndex, float weight)
    {
        if (weight <= 0f)
        {
            return;
        }

        if (map.TryGetValue(boneIndex, out float existing))
        {
            map[boneIndex] = existing + weight;
        }
        else
        {
            map[boneIndex] = weight;
        }
    }
}
