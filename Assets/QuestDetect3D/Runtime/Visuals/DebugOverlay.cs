// Debug view (B button / scan mode) — makes every box explainable:
//   Objectron: where the network put each keypoint (magenta, on its camera ray) versus where the fitted metric
//              box puts it (cyan). Long yellow links = reprojection error.
//   YOLO+depth: the 2D detection outline at the object's distance (magenta) and the depth points the box was
//              fitted to (cyan = inside the box, yellow = outside / rejected by the fit).
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

        public void AddDepthFit(in CameraModel cam, Rect viewport, in DepthFitResult fit, List<Vector3> points)
        {
            if (!Enabled || !fit.Valid)
            {
                return;
            }

            // 2D detection outline, drawn on the plane through the box centre facing the camera.
            var c00 = cam.ViewportToRay(new Vector2(viewport.xMin, viewport.yMin));
            var c10 = cam.ViewportToRay(new Vector2(viewport.xMax, viewport.yMin));
            var c11 = cam.ViewportToRay(new Vector2(viewport.xMax, viewport.yMax));
            var c01 = cam.ViewportToRay(new Vector2(viewport.xMin, viewport.yMax));
            var plane = new Plane(-cam.Forward, fit.Box.Center);
            var magenta = new Color(1f, 0.2f, 0.9f, 1f);
            if (TryHit(plane, c00, out var p00) && TryHit(plane, c10, out var p10) && TryHit(plane, c11, out var p11) &&
                TryHit(plane, c01, out var p01))
            {
                Segment(p00, p10, magenta);
                Segment(p10, p11, magenta);
                Segment(p11, p01, magenta);
                Segment(p01, p00, magenta);
            }

            if (points == null)
            {
                return;
            }

            var inv = Quaternion.Inverse(fit.Box.Rotation);
            var step = Mathf.Max(1, points.Count / 40);
            for (var i = 0; i < points.Count; i += step)
            {
                var l = inv * (points[i] - fit.Box.BottomCenter);
                var inside = Mathf.Abs(l.x) <= 0.5f * fit.Box.Size.x + 0.03f && Mathf.Abs(l.z) <= 0.5f * fit.Box.Size.z + 0.03f &&
                             l.y >= -0.03f && l.y <= fit.Box.Size.y + 0.03f;
                Cross(points[i], 0.008f, inside ? new Color(0.2f, 0.9f, 1f, 1f) : new Color(1f, 0.9f, 0.2f, 0.9f));
            }
        }

        private static bool TryHit(Plane plane, Ray ray, out Vector3 point)
        {
            if (plane.Raycast(ray, out var t) && t > 0f)
            {
                point = ray.GetPoint(t);
                return true;
            }

            point = default;
            return false;
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
