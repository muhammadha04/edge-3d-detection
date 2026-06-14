// Draws Objectron multi_box_rects directly on the camera-feed RawImage (inference image space).

using System.Collections.Generic;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.CoordinateSystem;
using UnityEngine;
using UnityEngine.UI;

namespace QuestObjectron
{
    public class Objectron2DFeedOverlay : MonoBehaviour
    {
        private const int MaxBoxes = 5;
        private const float BoxPersistSeconds = 0.35f;

        [SerializeField] private RectTransform m_feedRect;
        [SerializeField] private UnityEngine.Color m_boxColor = new(0.1f, 1f, 0.25f, 0.95f);
        [SerializeField] private float m_borderThickness = 5f;

        private RotationAngle m_imageRotation = RotationAngle.Rotation0;
        private bool m_isMirrored;
        private RectTransform m_overlayRect;
        private readonly List<OrientedBoxUi> m_activeBoxes = new();
        private readonly List<OrientedBoxUi> m_pool = new();
        private List<NormalizedRect> m_pendingRects;
        private readonly object m_pendingLock = new();
        private bool m_hasPending;
        private float m_lastDrawTime;
        private int m_lastLoggedCount = -1;

        public bool HasDrawnThisSession { get; private set; }

        public void Bind(RectTransform feedRect, RotationAngle imageRotation, bool isMirrored)
        {
            m_feedRect = feedRect;
            m_imageRotation = imageRotation;
            m_isMirrored = isMirrored;
            EnsureOverlayLayer();
        }

        public void SetMirrorAndRotation(RotationAngle imageRotation, bool isMirrored)
        {
            m_imageRotation = imageRotation;
            m_isMirrored = isMirrored;
        }

        public void Enqueue(IList<NormalizedRect> rects)
        {
            if (rects == null || rects.Count == 0)
            {
                return;
            }

            lock (m_pendingLock)
            {
                m_pendingRects ??= new List<NormalizedRect>();
                m_pendingRects.Clear();
                foreach (var rect in rects)
                {
                    m_pendingRects.Add(rect);
                }

                m_hasPending = true;
            }
        }

        public void Clear()
        {
            lock (m_pendingLock)
            {
                m_hasPending = false;
                m_pendingRects?.Clear();
            }

            ReturnActiveToPool();
        }

        private void LateUpdate()
        {
            List<NormalizedRect> rects = null;
            lock (m_pendingLock)
            {
                if (m_hasPending)
                {
                    rects = m_pendingRects != null ? new List<NormalizedRect>(m_pendingRects) : null;
                    m_hasPending = false;
                }
            }

            if (rects != null)
            {
                Draw(rects);
            }
            else if (Time.realtimeSinceStartup - m_lastDrawTime > BoxPersistSeconds)
            {
                ReturnActiveToPool();
            }
        }

        private void Draw(IList<NormalizedRect> rects)
        {
            if (m_feedRect == null)
            {
                return;
            }

            EnsureOverlayLayer();
            if (m_overlayRect == null)
            {
                return;
            }

            m_lastDrawTime = Time.realtimeSinceStartup;
            ReturnActiveToPool();

            var boxCount = Mathf.Min(rects.Count, MaxBoxes);
            var localRect = m_overlayRect.rect;

            for (var i = 0; i < boxCount; i++)
            {
                var ui = GetBoxFromPool();
                m_activeBoxes.Add(ui);
                var vertices = localRect.GetRectVertices(rects[i], m_imageRotation, m_isMirrored);
                PlaceOrientedBox(ui, vertices);
            }

            if (boxCount > 0)
            {
                HasDrawnThisSession = true;
            }

            if (boxCount != m_lastLoggedCount)
            {
                m_lastLoggedCount = boxCount;
                QuestObjectronLogger.Viz($"2d_feed_overlay boxes={boxCount} rot={m_imageRotation} mirror={m_isMirrored}");
            }
        }

        private void EnsureOverlayLayer()
        {
            if (m_feedRect == null)
            {
                return;
            }

            if (m_overlayRect != null)
            {
                return;
            }

            var layerGo = new GameObject("Objectron2DFeedOverlay");
            layerGo.transform.SetParent(m_feedRect, false);
            m_overlayRect = layerGo.AddComponent<RectTransform>();
            m_overlayRect.anchorMin = Vector2.zero;
            m_overlayRect.anchorMax = Vector2.one;
            m_overlayRect.offsetMin = Vector2.zero;
            m_overlayRect.offsetMax = Vector2.zero;
            m_overlayRect.pivot = new Vector2(0.5f, 0.5f);
        }

        private static void PlaceOrientedBox(OrientedBoxUi ui, Vector3[] vertices)
        {
            if (vertices == null || vertices.Length < 4)
            {
                ui.Root.gameObject.SetActive(false);
                return;
            }

            ui.Root.gameObject.SetActive(true);
            for (var i = 0; i < 4; i++)
            {
                var a = new Vector2(vertices[i].x, vertices[i].y);
                var b = new Vector2(vertices[(i + 1) % 4].x, vertices[(i + 1) % 4].y);
                PlaceEdge(ui.Edges[i].rectTransform, a, b, ui.Border);
            }
        }

        private static void PlaceEdge(RectTransform edge, Vector2 a, Vector2 b, float thickness)
        {
            var delta = b - a;
            var length = delta.magnitude;
            if (length < 0.5f)
            {
                edge.gameObject.SetActive(false);
                return;
            }

            var angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            edge.gameObject.SetActive(true);
            edge.anchoredPosition = (a + b) * 0.5f;
            edge.sizeDelta = new Vector2(length, thickness);
            edge.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        private OrientedBoxUi GetBoxFromPool()
        {
            if (m_pool.Count > 0)
            {
                var ui = m_pool[m_pool.Count - 1];
                m_pool.RemoveAt(m_pool.Count - 1);
                return ui;
            }

            return CreateBoxUi();
        }

        private OrientedBoxUi CreateBoxUi()
        {
            var root = new GameObject("DetectionBox2D");
            root.transform.SetParent(m_overlayRect, false);
            var rootRt = root.AddComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.anchoredPosition = Vector2.zero;
            rootRt.sizeDelta = Vector2.zero;

            var edges = new Image[4];
            for (var i = 0; i < 4; i++)
            {
                var edgeGo = new GameObject($"Edge{i}");
                edgeGo.transform.SetParent(rootRt, false);
                var edgeRt = edgeGo.AddComponent<RectTransform>();
                edgeRt.pivot = new Vector2(0.5f, 0.5f);
                var img = edgeGo.AddComponent<Image>();
                img.color = m_boxColor;
                img.raycastTarget = false;
                edges[i] = img;
            }

            return new OrientedBoxUi
            {
                Root = rootRt,
                Edges = edges,
                Border = m_borderThickness
            };
        }

        private void ReturnActiveToPool()
        {
            foreach (var box in m_activeBoxes)
            {
                box.Root.gameObject.SetActive(false);
                m_pool.Add(box);
            }

            m_activeBoxes.Clear();
        }

        private sealed class OrientedBoxUi
        {
            public RectTransform Root;
            public Image[] Edges;
            public float Border;
        }
    }
}
