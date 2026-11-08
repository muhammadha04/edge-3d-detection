// Estimates the height of the horizontal surface an object stands on, using the Depth API
// (MRUK EnvironmentRaycastManager) along rays through the object's bottom keypoints, and a
// continuously-updated room floor model for floor-standing categories.
using System.Collections.Generic;
using Meta.XR;
using UnityEngine;

namespace QuestDetect3D
{
    /// <summary>Running estimate of the floor height from downward depth raycasts around the user.</summary>
    public sealed class FloorEstimator
    {
        private const int MaxSamples = 90;
        private const int MinSamples = 10;
        private readonly List<float> m_samples = new List<float>(MaxSamples);
        private readonly List<float> m_sorted = new List<float>(MaxSamples);
        private int m_next;
        private int m_tick;

        public bool HasFloor { get; private set; }
        public float FloorY { get; private set; }
        public float Sigma { get; private set; } = 0.05f;
        public int SampleCount => m_samples.Count;

        public void Reset()
        {
            m_samples.Clear();
            m_next = 0;
            HasFloor = false;
        }

        /// <summary>Call once per frame; casts a couple of rays below the head.</summary>
        public void Update(EnvironmentRaycastManager raycast, Transform head)
        {
            if (raycast == null || head == null || !EnvironmentRaycastManager.IsSupported)
            {
                return;
            }

            m_tick++;
            if (m_tick % 3 != 0)
            {
                return;
            }

            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
            {
                flat = Vector3.ProjectOnPlane(head.up, Vector3.up);
            }

            flat.Normalize();
            // Rotate through a small fan of directions: 45–80° below the horizon, ±35° around heading.
            var phase = m_tick / 3 % 6;
            var yawOffset = (phase % 3 - 1) * 35f;
            var pitch = phase < 3 ? 50f : 75f;
            var dir = Quaternion.AngleAxis(yawOffset, Vector3.up) * flat;
            dir = Quaternion.AngleAxis(pitch, Vector3.Cross(Vector3.up, dir)) * dir;
            if (!raycast.Raycast(new Ray(head.position, dir), out var hit, 6f))
            {
                return;
            }

            if (hit.normal.y < 0.9f || hit.point.y > head.position.y - 0.6f)
            {
                return;
            }

            AddSample(hit.point.y);
        }

        private void AddSample(float y)
        {
            if (m_samples.Count < MaxSamples)
            {
                m_samples.Add(y);
            }
            else
            {
                m_samples[m_next] = y;
                m_next = (m_next + 1) % MaxSamples;
            }

            if (m_samples.Count < MinSamples)
            {
                return;
            }

            m_sorted.Clear();
            m_sorted.AddRange(m_samples);
            m_sorted.Sort();
            // Floor = densest low cluster: samples within [p10 - 3 cm, p10 + 6 cm].
            var low = m_sorted[Mathf.Clamp(m_sorted.Count / 10, 0, m_sorted.Count - 1)];
            var cluster = new List<float>();
            foreach (var s in m_sorted)
            {
                if (s >= low - 0.03f && s <= low + 0.06f)
                {
                    cluster.Add(s);
                }
            }

            if (cluster.Count < MinSamples / 2)
            {
                return;
            }

            var median = cluster[cluster.Count / 2];
            var mad = 0f;
            foreach (var s in cluster)
            {
                mad += Mathf.Abs(s - median);
            }

            mad /= cluster.Count;
            FloorY = median;
            Sigma = Mathf.Clamp(1.25f * mad + 0.005f, 0.008f, 0.05f);
            HasFloor = true;
        }
    }

    public static class SupportPlaneEstimator
    {
        private static readonly List<float> s_hits = new List<float>(16);

        public static SupportEstimate Estimate(
            KeypointDetection det,
            in CameraModel cam,
            EnvironmentRaycastManager raycast,
            FloorEstimator floor,
            in CategoryInfo info)
        {
            var local = EstimateLocal(det, cam, raycast);
            var useFloor = info.Support == SupportSurface.Floor && floor != null && floor.HasFloor;
            if (!useFloor)
            {
                return local;
            }

            var floorEstimate = new SupportEstimate
            {
                Valid = true,
                Y = floor.FloorY,
                Sigma = floor.Sigma,
                Hits = local.Hits,
                Rays = local.Rays,
                UsedFloorModel = true,
            };

            if (!local.Valid)
            {
                return floorEstimate;
            }

            // Local hits near the floor model agree → fuse; otherwise trust the floor model (local rays often
            // hit the object itself, e.g. chair legs or the seat).
            if (Mathf.Abs(local.Y - floor.FloorY) < 0.08f)
            {
                var wl = 1f / (local.Sigma * local.Sigma);
                var wf = 1f / (floor.Sigma * floor.Sigma);
                floorEstimate.Y = (local.Y * wl + floor.FloorY * wf) / (wl + wf);
                floorEstimate.Sigma = Mathf.Sqrt(1f / (wl + wf));
            }

            return floorEstimate;
        }

        private static SupportEstimate EstimateLocal(KeypointDetection det, in CameraModel cam,
            EnvironmentRaycastManager raycast)
        {
            var estimate = new SupportEstimate { Valid = false };
            if (raycast == null || !EnvironmentRaycastManager.IsSupported)
            {
                return estimate;
            }

            s_hits.Clear();
            var rays = 0;
            var centroid = Vector2.zero;
            var centroidN = 0;
            for (var i = 1; i <= 8; i++)
            {
                if (!det.Has[i] || det.Layout.IsTop(i))
                {
                    continue;
                }

                centroid += det.Viewport[i];
                centroidN++;
                rays++;
                if (raycast.Raycast(cam.ViewportToRay(det.Viewport[i]), out var hit, 8f))
                {
                    s_hits.Add(hit.point.y);
                }
            }

            if (centroidN > 0)
            {
                centroid /= centroidN;
                // A few rays just around the bottom-face centroid: usually the surface right under the object.
                for (var k = 0; k < 4; k++)
                {
                    var offset = new Vector2(k % 2 == 0 ? -0.015f : 0.015f, k < 2 ? -0.01f : -0.03f);
                    rays++;
                    if (raycast.Raycast(cam.ViewportToRay(centroid + offset), out var hit, 8f) && hit.normal.y > 0.7f)
                    {
                        s_hits.Add(hit.point.y);
                    }
                }
            }

            estimate.Rays = rays;
            estimate.Hits = s_hits.Count;
            if (s_hits.Count < 3)
            {
                return estimate;
            }

            s_hits.Sort();
            // The support surface is the lowest consistent cluster: hits on the object itself are higher.
            var low = s_hits[Mathf.Min(1, s_hits.Count - 1)];
            var sum = 0f;
            var n = 0;
            foreach (var y in s_hits)
            {
                if (y <= low + 0.04f)
                {
                    sum += y;
                    n++;
                }
            }

            var mean = sum / n;
            var dev = 0f;
            foreach (var y in s_hits)
            {
                if (y <= low + 0.04f)
                {
                    dev += (y - mean) * (y - mean);
                }
            }

            estimate.Valid = n >= 2;
            estimate.Y = mean;
            estimate.Sigma = Mathf.Clamp(Mathf.Sqrt(dev / n) + 0.012f, 0.012f, 0.06f);
            return estimate;
        }
    }
}
