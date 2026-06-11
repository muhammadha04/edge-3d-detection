// World-space wireframe boxes for Objectron detections.

using System.Collections.Generic;
using UnityEngine;

namespace QuestObjectron
{
    public class OrientedBBoxDrawer : MonoBehaviour
    {
        private static readonly int[][] s_edges =
        {
            new[] { 1, 2 }, new[] { 3, 4 }, new[] { 5, 6 }, new[] { 7, 8 },
            new[] { 1, 3 }, new[] { 2, 4 }, new[] { 5, 7 }, new[] { 6, 8 },
            new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 }, new[] { 4, 8 },
        };

        [SerializeField] private UnityEngine.Color m_lineColor = new UnityEngine.Color(0.2f, 1f, 0.4f, 1f);
        [SerializeField] private float m_lineWidth = 0.12f;
        [SerializeField] private int m_maxBoxes = 5;

        private readonly List<BoxVisual> m_pool = new();
        private int m_activeCount;
        private bool m_prewarmed;
        private int m_lastLoggedBoxCount = -1;
        private sealed class BoxVisual
        {
            public GameObject Root;
            public LineRenderer[] Edges = new LineRenderer[12];
        }

        public void Prewarm()
        {
            if (m_prewarmed)
            {
                return;
            }

            for (var i = 0; i < m_maxBoxes; i++)
            {
                var visual = GetOrCreate(i);
                visual.Root.SetActive(false);
            }

            m_prewarmed = true;
        }

        private void Awake()
        {
            Prewarm();
        }

        public void SetDetections(IReadOnlyList<Vector3[]> worldCornersPerObject)
        {
            if (!m_prewarmed)
            {
                Prewarm();
            }

            m_activeCount = 0;
            if (worldCornersPerObject == null)
            {
                HideAll();
                return;
            }

            for (var i = 0; i < worldCornersPerObject.Count && i < m_maxBoxes; i++)
            {
                var corners = worldCornersPerObject[i];
                if (corners == null || corners.Length < 9)
                {
                    continue;
                }

                if (!ObjectronBoxValidation.HasValidExtents(corners))
                {
                    continue;
                }

                var visual = GetOrCreate(i);
                visual.Root.SetActive(true);
                DrawBox(visual, corners);
                m_activeCount++;
            }

            for (var i = m_activeCount; i < m_pool.Count; i++)
            {
                if (m_pool[i].Root != null)
                {
                    m_pool[i].Root.SetActive(false);
                }
            }

            if (m_activeCount != m_lastLoggedBoxCount)
            {
                m_lastLoggedBoxCount = m_activeCount;
                QuestObjectronLogger.Viz($"boxes={m_activeCount}");
            }
        }

        private void DrawBox(BoxVisual visual, Vector3[] corners)
        {
            for (var e = 0; e < s_edges.Length; e++)
            {
                var a = corners[s_edges[e][0]];
                var b = corners[s_edges[e][1]];
                var lr = visual.Edges[e];
                lr.SetPosition(0, a);
                lr.SetPosition(1, b);
            }
        }

        private BoxVisual GetOrCreate(int index)
        {
            while (m_pool.Count <= index)
            {
                m_pool.Add(CreateVisual(m_pool.Count));
            }

            return m_pool[index];
        }

        private BoxVisual CreateVisual(int index)
        {
            var root = new GameObject($"ObjectronBBox_{index}");
            root.layer = 0;
            root.transform.SetParent(ObjectronQuestVisuals.VisualsRoot, false);

            var visual = new BoxVisual { Root = root };

            for (var e = 0; e < s_edges.Length; e++)
            {
                var edgeGo = new GameObject($"Edge_{e}");
                edgeGo.transform.SetParent(root.transform, false);
                var lr = edgeGo.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.startWidth = m_lineWidth;
                lr.endWidth = m_lineWidth;
                lr.alignment = LineAlignment.View;
                ObjectronMrVisibleMaterial.ApplyToLineRenderer(lr, m_lineColor);
                visual.Edges[e] = lr;
            }

            root.SetActive(false);
            return visual;
        }

        private void HideAll()
        {
            foreach (var v in m_pool)
            {
                if (v.Root != null)
                {
                    v.Root.SetActive(false);
                }
            }
        }
    }
}
