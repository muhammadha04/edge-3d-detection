// Debug view (B button): shows where the network put each keypoint (magenta, on its camera ray)
// versus where the fitted metric box puts it (cyan). Long yellow links = reprojection error.
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class DebugOverlay : MonoBehaviour
    {
        private const float Width = 0.004f;
        private readonly List<LineRenderer> m_pool = new List<LineRenderer>();
        private Material m_material;
        private int m_used;

        public bool Enabled { get; set; }

        public static DebugOverlay Create(Material material)
        {
            var go = new GameObject("DebugOverlay");
            var overlay = go.AddComponent<DebugOverlay>();
            overlay.m_material = material;
            return overlay;
        }

        public void Begin()
        {
            m_used = 0;
        }

        public void AddFit(KeypointDetection det, in CameraModel cam, in BoxFitResult fit)
        {
            if (!Enabled || !fit.Valid || fit.KeypointsWorld == null)
            {
                return;
            }

            for (var i = 0; i < KeypointDetection.KeypointCount; i++)
            {
                if (!det.Has[i])
                {
                    continue;
                }

                var fitted = fit.KeypointsWorld[i];
                var ray = cam.ViewportToRay(det.Viewport[i]);
                var observed = ray.GetPoint(Vector3.Distance(cam.Position, fitted));
                Cross(observed, 0.012f, new Color(1f, 0.2f, 0.9f, 1f));
                Cross(fitted, 0.008f, new Color(0.2f, 0.9f, 1f, 1f));
                Segment(observed, fitted, new Color(1f, 0.9f, 0.2f, 0.9f));
            }
        }

        public void End()
        {
            for (var i = m_used; i < m_pool.Count; i++)
            {
                if (m_pool[i].enabled)
                {
                    m_pool[i].enabled = false;
                }
            }
        }

        public void Clear()
        {
            Begin();
            End();
        }

        private void Cross(Vector3 p, float r, Color color)
        {
            Segment(p - Vector3.right * r, p + Vector3.right * r, color);
            Segment(p - Vector3.up * r, p + Vector3.up * r, color);
        }

        private void Segment(Vector3 a, Vector3 b, Color color)
        {
            LineRenderer lr;
            if (m_used < m_pool.Count)
            {
                lr = m_pool[m_used];
            }
            else
            {
                var go = new GameObject("dbg");
                go.transform.SetParent(transform, false);
                lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = m_material;
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.startWidth = Width;
                lr.endWidth = Width;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                m_pool.Add(lr);
            }

            m_used++;
            lr.enabled = true;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            lr.startColor = color;
            lr.endColor = color;
        }
    }
}
