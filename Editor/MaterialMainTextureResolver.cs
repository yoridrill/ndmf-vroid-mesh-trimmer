using UnityEngine;

public static class MaterialMainTextureResolver
{
    private static readonly string[] PropertyPriority =
    {
        "_MainTex",
        "_BaseMap",
        "_BaseColorMap",
        "_MainTexture"
    };

    public static bool TryGetMainTexture(Material material, out Texture2D texture2D, out string propertyName)
    {
        texture2D = null;
        propertyName = null;

        if (material == null)
        {
            return false;
        }

        foreach (string prop in PropertyPriority)
        {
            if (!material.HasProperty(prop))
            {
                continue;
            }

            propertyName = prop;
            Texture tex = material.GetTexture(prop);
            texture2D = tex as Texture2D;
            return texture2D != null;
        }

        return false;
    }
}
