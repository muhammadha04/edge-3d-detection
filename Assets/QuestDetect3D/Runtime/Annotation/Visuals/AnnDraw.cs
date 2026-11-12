// Immediate-mode drawing for QD3D Annotate: everything (boxes, walls, points, plumb lines, axes, reticles) is
// re-emitted every frame into one dynamic mesh, plus a pool of world-space labels. Callers draw in WORLD space.
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class AnnDraw
    {
        private readonly List<Vector3> m_v = new List<Vector3>(8192);
        private readonly List<Color> m_c = new List<Color>(8192);
        private readonly List<int> m_t = new List<int>(16384);
        private readonly Mesh m_mesh;
        private readonly GameObject m_go;
        private readonly List<TextMesh> m_labels = new List<TextMesh>();
        private readonly Font m_font;
        private readonly Transform m_head;
        private int m_labelCount;

        public AnnDraw(Material material, Font font, Transform head)
        {
            m_font = font;
            m_head = head;
            m_mesh = new Mesh { name = "AnnDraw" };
            m_mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m_mesh.MarkDynamic();
            var renderer = MeshUtil.CreateMeshObject(null, "AnnDraw", m_mesh, material, Vector3.zero);
            m_go = renderer.gameObject;
        }

        public bool Visible
        {
            set
            {
                m_go.SetActive(value);
                if (!value)
                {
                    foreach (var l in m_labels)
                    {
                        l.gameObject.SetActive(false);
                    }
                }
            }
        }

        /// <summary>Starts a new frame of drawings (called by AnnotationMode).</summary>
        public void Begin()
        {
            m_v.Clear();
            m_c.Clear();
            m_t.Clear();
            m_labelCount = 0;
        }

        /// <summary>Uploads the mesh and hides unused labels (called by AnnotationMode).</summary>
        public void End()
        {
            MeshUtil.Apply(m_mesh, m_v, m_c, m_t);
            m_mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            for (var i = m_labelCount; i < m_labels.Count; i++)
            {
                if (m_labels[i].gameObject.activeSelf)
                {
                    m_labels[i].gameObject.SetActive(false);
                }
            }
        }

        /// <summary>Thick line (prism) from a to b.</summary>
        public void Line(Vector3 a, Vector3 b, Color color, float halfThickness = 0.0015f)
        {
            MeshUtil.AddPrism(m_v, m_c, m_t, a, b, halfThickness, color);
        }

        /// <summary>Filled quadrilateral (translucent walls).</summary>
        public void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color color)
        {
            MeshUtil.AddQuad(m_v, m_c, m_t, p0, p1, p2, p3, color);
        }

        /// <summary>Small 3D cross (three short bars): visible from any direction.</summary>
        public void Point(Vector3 p, Color color, float size = 0.008f)
        {
            var h = size * 0.5f;
            var t = size * 0.18f;
            Line(p - Vector3.right * h, p + Vector3.right * h, color, t);
            Line(p - Vector3.up * h, p + Vector3.up * h, color, t);
            Line(p - Vector3.forward * h, p + Vector3.forward * h, color, t);
        }

        /// <summary>Flat disc approximation on the floor (footprints, reticles).</summary>
        public void FloorRing(Vector3 centre, float radius, Color color, float halfThickness = 0.0015f)
        {
            const int n = 20;
            var prev = centre + new Vector3(radius, 0f, 0f);
            for (var i = 1; i <= n; i++)
            {
                var a = i * 2f * Mathf.PI / n;
                var p = centre + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Line(prev, p, color, halfThickness);
                prev = p;
            }
        }

        /// <summary>Edges, optional translucent walls and a highlighted face of an upright box.</summary>
        public void Box(UprightBox box, Color edge, float halfThickness, Color wall, bool walls, int highlightFace = -1)
        {
            var c = new Vector3[8];
            box.GetCorners(c);
            // Edges: bits 1 = +x, 2 = top, 4 = +z
            int[,] edges =
            {
                { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, // along x
                { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, // vertical
                { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }, // along z
            };
            for (var i = 0; i < 12; i++)
            {
                Line(c[edges[i, 0]], c[edges[i, 1]], edge, halfThickness);
            }

            if (walls || highlightFace >= 0)
            {
                // Faces in BoxEdit order: +x, -x, +z, -z, +y, -y
                int[][] faces =
                {
                    new[] { 1, 3, 7, 5 }, new[] { 0, 4, 6, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 2, 3, 1 },
                    new[] { 2, 6, 7, 3 }, new[] { 0, 1, 5, 4 },
                };
                for (var f = 0; f < 6; f++)
                {
                    if (f == 5 && highlightFace != 5)
                    {
                        continue; // never cover the floor footprint
                    }

                    if (!walls && f != highlightFace)
                    {
                        continue;
                    }

                    var color = f == highlightFace ? new Color(1f, 0.85f, 0.2f, 0.35f) : wall;
                    Quad(c[faces[f][0]], c[faces[f][1]], c[faces[f][2]], c[faces[f][3]], color);
                }
            }
        }

        /// <summary>Front arrow on the floor in front of the box (shows which face is "front").</summary>
        public void FrontArrow(UprightBox box, Color color)
        {
            var f = box.Forward;
            var r = box.Right;
            var start = box.BottomCenter + Vector3.up * 0.005f;
            var tip = start + f * (0.5f * box.Size.z + 0.25f);
            Line(start, tip, color, 0.004f);
            Line(tip, tip - f * 0.08f + r * 0.05f, color, 0.004f);
            Line(tip, tip - f * 0.08f - r * 0.05f, color, 0.004f);
        }

        /// <summary>Room axes: red = +X, green = +Y (up), blue = +Z.</summary>
        public void Axes(Vector3 origin, float yaw, float length = 0.3f)
        {
            var z = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            var x = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            Line(origin, origin + x * length, new Color(1f, 0.3f, 0.3f, 1f), 0.003f);
            Line(origin, origin + Vector3.up * length, new Color(0.3f, 1f, 0.4f, 1f), 0.003f);
            Line(origin, origin + z * length, new Color(0.35f, 0.6f, 1f, 1f), 0.003f);
        }

        /// <summary>Text facing the user (pooled TextMesh).</summary>
        public void Label(Vector3 position, string text, Color color, float lineHeight = 0.025f)
        {
            TextMesh label;
            if (m_labelCount < m_labels.Count)
            {
                label = m_labels[m_labelCount];
            }
            else
            {
                label = MeshUtil.CreateText(null, "AnnLabel", m_font, lineHeight, Vector3.zero, color);
                m_labels.Add(label);
            }

            m_labelCount++;
            if (!label.gameObject.activeSelf)
            {
                label.gameObject.SetActive(true);
            }

            label.text = text;
            label.color = color;
            var t = label.transform;
            t.position = position;
            if (m_head != null)
            {
                var away = position - m_head.position;
                away.y = 0f;
                if (away.sqrMagnitude > 1e-6f)
                {
                    t.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
                }
            }
        }
    }
}
