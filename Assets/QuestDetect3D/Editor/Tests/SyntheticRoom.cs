#if UNITY_EDITOR || QD3D_OFFLINE
// Analytic test room for debugging the 3D pipeline without a headset.
// Furniture is built from oriented boxes (legs, seat, backrest, table top...), the room has a floor, walls and
// a ceiling. Depth queries imitate the Quest Depth API: coarse angular resolution (~320 px across the view),
// distance-dependent noise σ(z) = a + b z², random dropouts and "flying pixels" at a few percent.
// The same class feeds the offline mono harness (Tools/offline) and the in-Unity SelfTest.
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D.Tests
{
    public struct Part
    {
        public Vector3 Center;
        public Vector3 Size;

        public Part(Vector3 center, Vector3 size)
        {
            Center = center;
            Size = size;
        }
    }

    public sealed class SyntheticObject
    {
        public string Name;
        public ObjectCategory Category;
        /// <summary>Ground-truth upright box (bounding box of all parts).</summary>
        public UprightBox Truth;
        /// <summary>Parts in the object's local frame (bottom-centre origin, +Z = front).</summary>
        public readonly List<Part> Parts = new List<Part>();
        /// <summary>Local side of the backrest for chairs: 0 = -Z (canonical), see BackrestEstimator.</summary>
        public int BackrestQuarter = 0;
    }

    public sealed class SyntheticRoom : IDepthRaycaster
    {
        public float FloorY = -0.06f;
        public float HalfSize = 3.5f;
        public float CeilingY = 2.6f;
        public float NoiseA = 0.006f;
        public float NoiseB = 0.004f;
        public float Dropout = 0.04f;
        public float FlyingPixels = 0.03f;
        /// <summary>Angular quantisation of depth samples (rad). Quest depth ≈ 320 px over ~100° → ~0.0055 rad.</summary>
        public float AngularStep = 0.0055f;
        public bool Noise = true;

        public readonly List<SyntheticObject> Objects = new List<SyntheticObject>();
        private readonly System.Random m_rng;

        public SyntheticRoom(int seed = 1)
        {
            m_rng = new System.Random(seed);
        }

        public bool IsAvailable => true;

        // ------------------------------------------------------------ furniture factories

        public SyntheticObject AddChair(string name, Vector3 bottomCenter, float yawDeg, float w = 0.58f, float d = 0.60f,
            float h = 0.95f, float seat = 0.46f)
        {
            var o = new SyntheticObject { Name = name, Category = ObjectCategory.Chair };
            const float leg = 0.035f;
            foreach (var sx in new[] { -1f, 1f })
            {
                foreach (var sz in new[] { -1f, 1f })
                {
                    o.Parts.Add(new Part(new Vector3(sx * (w * 0.5f - 0.04f), (seat - 0.05f) * 0.5f, sz * (d * 0.5f - 0.04f)),
                        new Vector3(leg, seat - 0.05f, leg)));
                }
            }

            o.Parts.Add(new Part(new Vector3(0f, seat - 0.025f, 0.02f), new Vector3(w, 0.05f, d - 0.04f))); // seat
            o.Parts.Add(new Part(new Vector3(0f, (seat + h) * 0.5f, -d * 0.5f + 0.03f), new Vector3(w, h - seat, 0.05f))); // backrest
            o.Truth = new UprightBox(new Vector3(bottomCenter.x, FloorY, bottomCenter.z), yawDeg * Mathf.Deg2Rad,
                new Vector3(w, h, d));
            Objects.Add(o);
            return o;
        }

        public SyntheticObject AddTable(string name, Vector3 bottomCenter, float yawDeg, float w = 1.20f, float d = 0.80f,
            float h = 0.75f)
        {
            var o = new SyntheticObject { Name = name, Category = ObjectCategory.Table };
            foreach (var sx in new[] { -1f, 1f })
            {
                foreach (var sz in new[] { -1f, 1f })
                {
                    o.Parts.Add(new Part(new Vector3(sx * (w * 0.5f - 0.05f), (h - 0.04f) * 0.5f, sz * (d * 0.5f - 0.05f)),
                        new Vector3(0.05f, h - 0.04f, 0.05f)));
                }
            }

            o.Parts.Add(new Part(new Vector3(0f, h - 0.02f, 0f), new Vector3(w, 0.04f, d)));
            o.Truth = new UprightBox(new Vector3(bottomCenter.x, FloorY, bottomCenter.z), yawDeg * Mathf.Deg2Rad,
                new Vector3(w, h, d));
            Objects.Add(o);
            return o;
        }

        public SyntheticObject AddSofa(string name, Vector3 bottomCenter, float yawDeg, float w = 2.0f, float d = 0.9f,
            float h = 0.85f)
        {
            var o = new SyntheticObject { Name = name, Category = ObjectCategory.Sofa };
            o.Parts.Add(new Part(new Vector3(0f, 0.22f, 0.05f), new Vector3(w, 0.44f, d - 0.1f))); // base + seat
            o.Parts.Add(new Part(new Vector3(0f, h * 0.5f, -d * 0.5f + 0.1f), new Vector3(w, h, 0.2f))); // back
            o.Parts.Add(new Part(new Vector3(-w * 0.5f + 0.1f, 0.32f, 0f), new Vector3(0.2f, 0.64f, d))); // arms
            o.Parts.Add(new Part(new Vector3(w * 0.5f - 0.1f, 0.32f, 0f), new Vector3(0.2f, 0.64f, d)));
            o.Truth = new UprightBox(new Vector3(bottomCenter.x, FloorY, bottomCenter.z), yawDeg * Mathf.Deg2Rad,
                new Vector3(w, h, d));
            Objects.Add(o);
            return o;
        }

        // ------------------------------------------------------------ ray casting

        public bool Raycast(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            var dir = ray.direction.normalized;
            if (AngularStep > 0f)
            {
                // Quantise the direction like a low-resolution depth map (blocky edges, mixed pixels).
                var yaw = Mathf.Atan2(dir.x, dir.z);
                var pitch = Mathf.Atan2(dir.y, Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z));
                yaw = Mathf.Round(yaw / AngularStep) * AngularStep;
                pitch = Mathf.Round(pitch / AngularStep) * AngularStep;
                dir = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
            }

            point = default;
            normal = Vector3.up;
            if (Noise && m_rng.NextDouble() < Dropout)
            {
                return false;
            }

            if (!TraceExact(ray.origin, dir, out var t, out normal) || t > maxDistance)
            {
                return false;
            }

            if (Noise)
            {
                var sigma = NoiseA + NoiseB * t * t;
                t += (float)Gaussian() * sigma;
                if (m_rng.NextDouble() < FlyingPixels)
                {
                    t *= 1f + (float)m_rng.NextDouble() * 0.35f;
                }
            }

            point = ray.origin + dir * t;
            if (Noise)
            {
                // Depth-derived normals are noisy (~8°).
                normal = (normal + 0.14f * new Vector3((float)Gaussian(), (float)Gaussian(), (float)Gaussian())).normalized;
            }

            return true;
        }

        public bool TraceExact(Vector3 origin, Vector3 dir, out float bestT, out Vector3 bestN)
        {
            bestT = float.MaxValue;
            bestN = Vector3.up;
            // Floor / ceiling / walls.
            PlaneHit(origin, dir, new Vector3(0, 1, 0), FloorY, ref bestT, ref bestN);
            PlaneHit(origin, dir, new Vector3(0, -1, 0), -CeilingY, ref bestT, ref bestN);
            PlaneHit(origin, dir, new Vector3(1, 0, 0), -HalfSize, ref bestT, ref bestN);
            PlaneHit(origin, dir, new Vector3(-1, 0, 0), -HalfSize, ref bestT, ref bestN);
            PlaneHit(origin, dir, new Vector3(0, 0, 1), -HalfSize, ref bestT, ref bestN);
            PlaneHit(origin, dir, new Vector3(0, 0, -1), -HalfSize, ref bestT, ref bestN);
            foreach (var o in Objects)
            {
                var rot = o.Truth.Rotation;
                var inv = Quaternion.Inverse(rot);
                var lo = inv * (origin - o.Truth.BottomCenter);
                var ld = inv * dir;
                foreach (var part in o.Parts)
                {
                    if (BoxHit(lo - part.Center, ld, part.Size * 0.5f, out var t, out var ln) && t < bestT)
                    {
                        bestT = t;
                        bestN = rot * ln;
                    }
                }
            }

            return bestT < float.MaxValue;
        }

        private static void PlaneHit(Vector3 o, Vector3 d, Vector3 n, float offset, ref float bestT, ref Vector3 bestN)
        {
            // Plane: dot(n, p) = offset, facing direction n.
            var denom = Vector3.Dot(n, d);
            if (denom >= -1e-6f)
            {
                return;
            }

            var t = (offset - Vector3.Dot(n, o)) / denom;
            if (t > 1e-4f && t < bestT)
            {
                bestT = t;
                bestN = n;
            }
        }

        private static bool BoxHit(Vector3 o, Vector3 d, Vector3 half, out float t, out Vector3 n)
        {
            t = 0f;
            n = Vector3.up;
            var tmin = float.MinValue;
            var tmax = float.MaxValue;
            var axisN = Vector3.zero;
            float[] oa = { o.x, o.y, o.z };
            float[] da = { d.x, d.y, d.z };
            float[] ha = { half.x, half.y, half.z };
            for (var i = 0; i < 3; i++)
            {
                if (Mathf.Abs(da[i]) < 1e-8f)
                {
                    if (oa[i] < -ha[i] || oa[i] > ha[i])
                    {
                        return false;
                    }

                    continue;
                }

                var t1 = (-ha[i] - oa[i]) / da[i];
                var t2 = (ha[i] - oa[i]) / da[i];
                var sign = -1f;
                if (t1 > t2)
                {
                    var tmp = t1;
                    t1 = t2;
                    t2 = tmp;
                    sign = 1f;
                }

                if (t1 > tmin)
                {
                    tmin = t1;
                    axisN = i == 0 ? new Vector3(sign, 0, 0) : i == 1 ? new Vector3(0, sign, 0) : new Vector3(0, 0, sign);
                }

                tmax = Mathf.Min(tmax, t2);
                if (tmin > tmax)
                {
                    return false;
                }
            }

            if (tmin < 1e-4f)
            {
                return false;
            }

            t = tmin;
            n = axisN;
            return true;
        }

        private double Gaussian()
        {
            var u1 = 1.0 - m_rng.NextDouble();
            var u2 = m_rng.NextDouble();
            return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
        }

        // ------------------------------------------------------------ "perfect detector" with YOLO-like noise

        /// <summary>Tight 2D box of an object's visible parts, or false when it is (mostly) out of view.</summary>
        public bool ProjectRect(SyntheticObject o, in CameraModel cam, out Rect viewport)
        {
            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;
            var rot = o.Truth.Rotation;
            var visibleCorners = 0;
            foreach (var part in o.Parts)
            {
                var c = part.Center;
                var s = part.Size;
                for (var k = 0; k < 8; k++)
                {
                    var local = c + new Vector3(((k & 1) != 0 ? 0.5f : -0.5f) * s.x, ((k & 2) != 0 ? 0.5f : -0.5f) * s.y,
                        ((k & 4) != 0 ? 0.5f : -0.5f) * s.z);
                    if (!cam.WorldToViewport(o.Truth.BottomCenter + rot * local, out var vp))
                    {
                        continue;
                    }

                    visibleCorners++;
                    minX = Mathf.Min(minX, vp.x);
                    minY = Mathf.Min(minY, vp.y);
                    maxX = Mathf.Max(maxX, vp.x);
                    maxY = Mathf.Max(maxY, vp.y);
                }
            }

            viewport = Rect.MinMaxRect(Mathf.Clamp01(minX), Mathf.Clamp01(minY), Mathf.Clamp01(maxX), Mathf.Clamp01(maxY));
            var onScreen = viewport.width * viewport.height;
            var full = (maxX - minX) * (maxY - minY);
            return visibleCorners > 0 && full > 0f && onScreen / full > 0.6f && viewport.width > 0.03f && viewport.height > 0.03f;
        }

        /// <summary>Is the object's centre actually visible (not hidden behind another object)?</summary>
        public bool CentreVisible(SyntheticObject o, Vector3 eye)
        {
            var target = o.Truth.Center;
            var dir = target - eye;
            var dist = dir.magnitude;
            if (!TraceExact(eye, dir / dist, out var t, out _))
            {
                return false;
            }

            return t > dist - 0.6f * Mathf.Max(o.Truth.Size.x, o.Truth.Size.z);
        }

        public Rect Jitter(Rect r, float amount)
        {
            var w = r.width;
            var h = r.height;
            return Rect.MinMaxRect(Mathf.Clamp01(r.xMin + J(amount) * w), Mathf.Clamp01(r.yMin + J(amount) * h),
                Mathf.Clamp01(r.xMax + J(amount) * w), Mathf.Clamp01(r.yMax + J(amount) * h));
        }

        private float J(float amount)
        {
            return (float)((m_rng.NextDouble() * 2.0 - 1.0) * amount);
        }

        public double NextDouble() => m_rng.NextDouble();

        /// <summary>Quest 3 left PCA camera at 1280×960 (intrinsics measured on device, see app log pca_ready).</summary>
        public static CameraModel QuestCamera(Vector3 position, Quaternion rotation) =>
            CameraModel.FromIntrinsics(position, rotation, new Vector2(865.7f, 865.7f), new Vector2(644.7f, 640.0f),
                new Vector2Int(1280, 1280), new Vector2Int(1280, 960));
    }
}
#endif
