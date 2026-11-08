// Procedural meshes for crisp MR wireframes (thin prisms instead of LineRenderers).
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D
{
    public static class MeshUtil
    {
        public static void AddPrism(List<Vector3> v, List<Color> c, List<int> t, Vector3 a, Vector3 b, float halfThickness,
            Color color)
        {
            var dir = b - a;
            if (dir.sqrMagnitude < 1e-8f)
            {
                return;
            }

            dir.Normalize();
            var reference = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
            var u = Vector3.Cross(dir, reference).normalized * halfThickness;
            var w = Vector3.Cross(dir, u).normalized * halfThickness;
            var start = v.Count;
            var offsets = new[] { u + w, u - w, -u - w, -u + w };
            for (var i = 0; i < 4; i++)
            {
                v.Add(a + offsets[i]);
                v.Add(b + offsets[i]);
                c.Add(color);
                c.Add(color);
            }

            for (var i = 0; i < 4; i++)
            {
                var j = (i + 1) % 4;
                var a0 = start + i * 2;
                var b0 = a0 + 1;
                var a1 = start + j * 2;
                var b1 = a1 + 1;
                t.Add(a0);
                t.Add(b0);
                t.Add(b1);
                t.Add(a0);
                t.Add(b1);
                t.Add(a1);
            }
        }

        public static void AddQuad(List<Vector3> v, List<Color> c, List<int> t, Vector3 p0, Vector3 p1, Vector3 p2,
            Vector3 p3, Color color)
        {
            var s = v.Count;
            v.Add(p0);
            v.Add(p1);
            v.Add(p2);
            v.Add(p3);
            for (var i = 0; i < 4; i++)
            {
                c.Add(color);
            }

            t.Add(s);
            t.Add(s + 1);
            t.Add(s + 2);
            t.Add(s);
            t.Add(s + 2);
            t.Add(s + 3);
        }

        public static void Apply(Mesh mesh, List<Vector3> v, List<Color> c, List<int> t)
        {
            mesh.Clear();
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
        }

        /// <summary>Rectangle in the local XY plane centred at (cx, cy).</summary>
        public static Mesh CreateRect(float width, float height, float cx = 0f, float cy = 0f)
        {
            var mesh = new Mesh { name = "QD3D_Rect" };
            var v = new List<Vector3>
            {
                new Vector3(cx - width * 0.5f, cy - height * 0.5f, 0f),
                new Vector3(cx - width * 0.5f, cy + height * 0.5f, 0f),
                new Vector3(cx + width * 0.5f, cy + height * 0.5f, 0f),
                new Vector3(cx + width * 0.5f, cy - height * 0.5f, 0f),
            };
            var c = new List<Color> { Color.white, Color.white, Color.white, Color.white };
            var t = new List<int> { 0, 1, 2, 0, 2, 3 };
            Apply(mesh, v, c, t);
            return mesh;
        }

        public static TextMesh CreateText(Transform parent, string name, Font font, float lineHeight, Vector3 localPos,
            Color color, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            const int fontSize = 64;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            // TextMesh line height ≈ 0.1 × fontSize × characterSize × scale (metres).
            go.transform.localScale = Vector3.one * (lineHeight / (0.1f * fontSize));
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = fontSize;
            tm.characterSize = 1f;
            tm.anchor = anchor;
            tm.alignment = TextAlignment.Center;
            tm.richText = true;
            tm.color = color;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = font.material;
            return tm;
        }

        public static MeshRenderer CreateMeshObject(Transform parent, string name, Mesh mesh, Material material,
            Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        public static void SetColor(Renderer renderer, MaterialPropertyBlock block, Color color)
        {
            block.Clear();
            block.SetColor(ColorId, color);
            renderer.SetPropertyBlock(block);
        }

        public static readonly int ColorId = Shader.PropertyToID("_Color");

        public static Color ConfidenceColor(float c)
        {
            var red = new Color(1f, 0.32f, 0.28f);
            var amber = new Color(1f, 0.76f, 0.2f);
            var green = new Color(0.3f, 0.95f, 0.55f);
            return c < 0.5f ? Color.Lerp(red, amber, c / 0.5f) : Color.Lerp(amber, green, (c - 0.5f) / 0.4f);
        }
    }
}
