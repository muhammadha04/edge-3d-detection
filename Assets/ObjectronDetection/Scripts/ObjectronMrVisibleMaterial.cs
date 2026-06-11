// Materials that draw on top of Quest passthrough (ZTest Always).

using UnityEngine;
using UnityEngine.Rendering;

namespace QuestObjectron
{
    public static class ObjectronMrVisibleMaterial
    {
        public const int OverlayRenderQueue = 4500;

        public static Material CreateUnlit(Color color, bool transparent = false)
        {
            var shader = Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader);
            if (transparent)
            {
                mat.SetFloat("_Mode", 3f);
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = OverlayRenderQueue;
            }

            mat.color = color;
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }

            if (mat.HasProperty("_ZWrite"))
            {
                mat.SetInt("_ZWrite", 0);
            }

            if (mat.HasProperty("_ZTest"))
            {
                mat.SetInt("_ZTest", (int)CompareFunction.Always);
            }

            if (!transparent)
            {
                mat.renderQueue = OverlayRenderQueue;
            }

            return mat;
        }

        public static void ApplyToLineRenderer(LineRenderer lineRenderer, Color color)
        {
            if (lineRenderer == null)
            {
                return;
            }

            lineRenderer.sharedMaterial = CreateUnlit(color);
            lineRenderer.startColor = color;
            lineRenderer.endColor = color;
            lineRenderer.shadowCastingMode = ShadowCastingMode.Off;
            lineRenderer.receiveShadows = false;
        }

        public static void ApplyToRenderer(Renderer renderer, Color color, bool transparent = false)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.sharedMaterial = CreateUnlit(color, transparent);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }
}
