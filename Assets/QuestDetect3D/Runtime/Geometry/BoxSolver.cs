// Metric 3D box from 2D box keypoints + real camera intrinsics + a known support plane.
//
// The network only predicts where the 8 box corners (and centre) project in the image. Objectron's own
// EPnP "lift" is up to scale and assumes a phone camera, so it is not used for geometry here. Instead:
//   1. rays through the 4 bottom-corner keypoints are intersected with the support plane (y = y0)
//      → metric footprint, yaw and handedness;
//   2. vertical lines through those footprint corners are matched with the top-corner rays → height;
//   3. Levenberg–Marquardt with a Huber loss refines (x, z, yaw, width, height, depth, y0) by minimising the
//      reprojection error of all keypoints through the actual PCA camera model.
// Output: gravity-aligned box + reprojection statistics used by the confidence model.
using UnityEngine;

namespace QuestDetect3D
{
    public struct SupportEstimate
    {
        public bool Valid;
        public float Y;
        public float Sigma;
        public int Hits;
        public int Rays;
        public bool UsedFloorModel;

        public override string ToString() =>
            Valid ? $"y={Y:F3} sigma={Sigma * 100f:F1}cm hits={Hits}/{Rays}{(UsedFloorModel ? " floor" : "")}" : "invalid";
    }

    public struct BoxFitResult
    {
        public bool Valid;
        public string FailReason;
        public UprightBox Box;
        public float RmsPx;
        public float MaxPx;
        public float InlierFraction;
        public int UsedKeypoints;
        public int Iterations;
        public int SideHandedness;
        public float InitialRmsPx;
        /// <summary>Diagonal (px) of the keypoints' 2D bounding box: the object's size on screen.</summary>
        public float DiagonalPx;
        /// <summary>RMS reprojection error relative to the on-screen object size (scale-free).</summary>
        public float NormalizedRms;
        public Vector3[] KeypointsWorld;
    }

    public static class BoxSolver
    {
        private const int ParamCount = 7; // bx, bz, yaw, width, height, depth, y0
        // Robust thresholds scale with the object's on-screen size (Objectron keypoint noise is ~3–8 % of the box).
        private const float HuberFraction = 0.04f;
        private const float InlierFraction = 0.06f;
        private const float MinHuberPx = 8f;
        private const float BehindPenaltyPx = 300f;
        private const float MinDim = 0.02f;
        private const float SupportPriorPx = 6f;
        private const int MaxIterations = 25;

        public static BoxFitResult Fit(KeypointDetection det, in CameraModel cam, SupportEstimate support,
            in CategoryInfo info)
        {
            var result = new BoxFitResult { Valid = false };
            if (!cam.IsValid)
            {
                result.FailReason = "camera_model_invalid";
                return result;
            }

            if (!support.Valid)
            {
                result.FailReason = "no_support_plane";
                return result;
            }

            if (det.ValidKeypointCount < 6)
            {
                result.FailReason = "too_few_keypoints";
                return result;
            }

            if (!TryInitialize(det, cam, support, info, out var theta, out var chi, out var reason))
            {
                result.FailReason = reason;
                return result;
            }

            var layout = det.Layout;
            layout.SideHandedness = chi;
            var y0Meas = support.Y;
            var sigmaY = Mathf.Max(0.01f, support.Sigma);

            var keypointCount = 0;
            for (var i = 0; i < KeypointDetection.KeypointCount; i++)
            {
                if (det.Has[i])
                {
                    keypointCount++;
                }
            }

            var residualCount = keypointCount * 2 + 1;
            var r = new float[residualCount];
            var rTrial = new float[residualCount];
            var jac = new float[residualCount, ParamCount];
            var robustWeights = new float[KeypointDetection.KeypointCount];
            for (var i = 0; i < robustWeights.Length; i++)
            {
                robustWeights[i] = 1f;
            }

            var diagonalPx = KeypointDiagonalPx(det, cam);
            var huberPx = Mathf.Max(MinHuberPx, HuberFraction * diagonalPx);
            var inlierPx = Mathf.Max(1.5f * MinHuberPx, InlierFraction * diagonalPx);
            var errors = new float[KeypointDetection.KeypointCount];
            ComputeErrors(det, layout, cam, theta, errors);
            result.InitialRmsPx = Rms(det, errors);

            var lambda = 1e-3f;
            var thetaTrial = new float[ParamCount];
            var steps = new[] { 1e-3f, 1e-3f, 1e-3f, 1e-3f, 1e-3f, 1e-3f, 1e-3f };
            var jtj = new float[ParamCount, ParamCount];
            var jtr = new float[ParamCount];
            var delta = new float[ParamCount];
            var iterations = 0;

            for (var iter = 0; iter < MaxIterations; iter++)
            {
                iterations = iter + 1;
                // IRLS Huber weights from the current solution.
                ComputeErrors(det, layout, cam, theta, errors);
                for (var i = 0; i < KeypointDetection.KeypointCount; i++)
                {
                    var e = errors[i];
                    robustWeights[i] = e <= huberPx ? 1f : huberPx / Mathf.Max(e, 1e-3f);
                }

                var cost = Residuals(det, layout, cam, theta, robustWeights, y0Meas, sigmaY, r);

                // Forward-difference Jacobian.
                for (var p = 0; p < ParamCount; p++)
                {
                    System.Array.Copy(theta, thetaTrial, ParamCount);
                    thetaTrial[p] += steps[p];
                    Residuals(det, layout, cam, thetaTrial, robustWeights, y0Meas, sigmaY, rTrial);
                    for (var k = 0; k < residualCount; k++)
                    {
                        jac[k, p] = (rTrial[k] - r[k]) / steps[p];
                    }
                }

                for (var a = 0; a < ParamCount; a++)
                {
                    jtr[a] = 0f;
                    for (var b = 0; b < ParamCount; b++)
                    {
                        jtj[a, b] = 0f;
                    }
                }

                for (var k = 0; k < residualCount; k++)
                {
                    for (var a = 0; a < ParamCount; a++)
                    {
                        var ja = jac[k, a];
                        if (ja == 0f)
                        {
                            continue;
                        }

                        jtr[a] += ja * r[k];
                        for (var b = a; b < ParamCount; b++)
                        {
                            jtj[a, b] += ja * jac[k, b];
                        }
                    }
                }

                for (var a = 0; a < ParamCount; a++)
                {
                    for (var b = 0; b < a; b++)
                    {
                        jtj[a, b] = jtj[b, a];
                    }
                }

                var improved = false;
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    if (!SolveDamped(jtj, jtr, lambda, delta))
                    {
                        lambda *= 10f;
                        continue;
                    }

                    for (var p = 0; p < ParamCount; p++)
                    {
                        thetaTrial[p] = theta[p] - delta[p];
                    }

                    ClampParams(thetaTrial, y0Meas, sigmaY);
                    var trialCost = Residuals(det, layout, cam, thetaTrial, robustWeights, y0Meas, sigmaY, rTrial);
                    if (trialCost < cost)
                    {
                        System.Array.Copy(thetaTrial, theta, ParamCount);
                        lambda = Mathf.Max(lambda / 3f, 1e-7f);
                        improved = true;
                        var maxStep = 0f;
                        for (var p = 0; p < ParamCount; p++)
                        {
                            maxStep = Mathf.Max(maxStep, Mathf.Abs(delta[p]));
                        }

                        if (maxStep < 1e-5f)
                        {
                            iter = MaxIterations;
                        }

                        break;
                    }

                    lambda *= 4f;
                }

                if (!improved)
                {
                    break;
                }
            }

            ComputeErrors(det, layout, cam, theta, errors);
            var used = 0;
            var inliers = 0;
            var max = 0f;
            for (var i = 0; i < KeypointDetection.KeypointCount; i++)
            {
                if (!det.Has[i])
                {
                    continue;
                }

                used++;
                if (errors[i] <= inlierPx)
                {
                    inliers++;
                }

                max = Mathf.Max(max, errors[i]);
            }

            var box = ToBox(theta);
            result.Valid = true;
            result.Box = box;
            result.RmsPx = Rms(det, errors);
            result.DiagonalPx = diagonalPx;
            result.NormalizedRms = result.RmsPx / Mathf.Max(1f, diagonalPx);
            result.MaxPx = max;
            result.UsedKeypoints = used;
            result.InlierFraction = used > 0 ? inliers / (float)used : 0f;
            result.Iterations = iterations;
            result.SideHandedness = chi;
            result.KeypointsWorld = new Vector3[KeypointDetection.KeypointCount];
            for (var i = 0; i < KeypointDetection.KeypointCount; i++)
            {
                result.KeypointsWorld[i] = KeypointWorld(box, layout, i);
            }

            if (box.Size.x > 4f || box.Size.y > 4f || box.Size.z > 4f)
            {
                result.Valid = false;
                result.FailReason = "implausible_size";
            }

            return result;
        }

        // ---------------------------------------------------------------- model

        public static Vector3 KeypointWorld(in UprightBox box, in BoxLayout layout, int index)
        {
            if (index == 0)
            {
                return box.Center;
            }

            var chi = layout.SideHandedness == 0 ? -1 : layout.SideHandedness;
            return box.Corner(chi * layout.SideSign(index), layout.IsTop(index) ? 1 : 0, layout.FrontSign(index));
        }

        private static UprightBox ToBox(float[] t)
        {
            return new UprightBox(new Vector3(t[0], t[6], t[1]), t[2], new Vector3(t[3], t[4], t[5]));
        }

        private static float Residuals(KeypointDetection det, in BoxLayout layout, in CameraModel cam, float[] theta,
            float[] robustWeights, float y0Meas, float sigmaY, float[] r)
        {
            var box = ToBox(theta);
            var k = 0;
            var cost = 0f;
            for (var i = 0; i < KeypointDetection.KeypointCount; i++)
            {
                if (!det.Has[i])
                {
                    continue;
                }

                var w = Mathf.Sqrt(Mathf.Max(0f, det.Weight[i] * robustWeights[i]));
                var world = KeypointWorld(box, layout, i);
                float rx, ry;
                if (cam.WorldToViewport(world, out var vp))
                {
                    rx = (vp.x - det.Viewport[i].x) * cam.PixelsPerViewportX;
                    ry = (vp.y - det.Viewport[i].y) * cam.PixelsPerViewportY;
                }
                else
                {
                    rx = BehindPenaltyPx;
                    ry = BehindPenaltyPx;
                }

                r[k++] = rx * w;
                r[k++] = ry * w;
                cost += (rx * rx + ry * ry) * w * w;
            }

            var prior = (theta[6] - y0Meas) / sigmaY * SupportPriorPx;
            r[k] = prior;
            cost += prior * prior;
            return cost;
        }

        private static void ComputeErrors(KeypointDetection det, in BoxLayout layout, in CameraModel cam, float[] theta,
            float[] errors)
        {
            var box = ToBox(theta);
            for (var i = 0; i < KeypointDetection.KeypointCount; i++)
            {
                if (!det.Has[i])
                {
                    errors[i] = 0f;
                    continue;
                }

                if (cam.WorldToViewport(KeypointWorld(box, layout, i), out var vp))
                {
                    var dx = (vp.x - det.Viewport[i].x) * cam.PixelsPerViewportX;
                    var dy = (vp.y - det.Viewport[i].y) * cam.PixelsPerViewportY;
                    errors[i] = Mathf.Sqrt(dx * dx + dy * dy);
                }
                else
                {
                    errors[i] = BehindPenaltyPx;
                }
            }
        }

        private static float KeypointDiagonalPx(KeypointDetection det, in CameraModel cam)
        {
            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;
            for (var i = 1; i < KeypointDetection.KeypointCount; i++)
            {
                if (!det.Has[i])
                {
                    continue;
                }

                var x = det.Viewport[i].x * cam.PixelsPerViewportX;
                var y = det.Viewport[i].y * cam.PixelsPerViewportY;
                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            if (maxX < minX)
            {
                return 1f;
            }

            var dx = maxX - minX;
            var dy = maxY - minY;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static float Rms(KeypointDetection det, float[] errors)
        {
            var sum = 0f;
            var n = 0;
            for (var i = 0; i < KeypointDetection.KeypointCount; i++)
            {
                if (det.Has[i])
                {
                    sum += errors[i] * errors[i];
                    n++;
                }
            }

            return n > 0 ? Mathf.Sqrt(sum / n) : float.MaxValue;
        }

        private static void ClampParams(float[] t, float y0Meas, float sigmaY)
        {
            t[3] = Mathf.Max(MinDim, t[3]);
            t[4] = Mathf.Max(MinDim, t[4]);
            t[5] = Mathf.Max(MinDim, t[5]);
            var maxDy = 3f * sigmaY + 0.01f;
            t[6] = Mathf.Clamp(t[6], y0Meas - maxDy, y0Meas + maxDy);
        }

        // ---------------------------------------------------------------- initialisation

        private static bool TryInitialize(KeypointDetection det, in CameraModel cam, SupportEstimate support,
            in CategoryInfo info, out float[] theta, out int chi, out string reason)
        {
            theta = null;
            chi = -1;
            reason = null;
            var layout = det.Layout;
            var y0 = support.Y;

            // Footprint corners keyed by (front sign, raw side sign).
            var have = new bool[2, 2];
            var pts = new Vector3[2, 2];
            var bottomCount = 0;
            for (var i = 1; i <= 8; i++)
            {
                if (!det.Has[i] || layout.IsTop(i))
                {
                    continue;
                }

                var ray = cam.ViewportToRay(det.Viewport[i]);
                if (!IntersectHorizontal(ray, y0, out var p))
                {
                    continue;
                }

                var fi = layout.FrontSign(i) > 0 ? 1 : 0;
                var si = layout.SideSign(i) > 0 ? 1 : 0;
                pts[fi, si] = p;
                have[fi, si] = true;
                bottomCount++;
            }

            if (bottomCount < 3)
            {
                reason = $"bottom_rays_miss_plane({bottomCount}/4)";
                return false;
            }

            if (bottomCount == 3)
            {
                for (var f = 0; f < 2; f++)
                {
                    for (var s = 0; s < 2; s++)
                    {
                        if (!have[f, s])
                        {
                            pts[f, s] = pts[1 - f, s] + pts[f, 1 - s] - pts[1 - f, 1 - s];
                            have[f, s] = true;
                        }
                    }
                }
            }

            var frontVec = 0.5f * ((pts[1, 1] - pts[0, 1]) + (pts[1, 0] - pts[0, 0]));
            var sideVec = 0.5f * ((pts[1, 1] - pts[1, 0]) + (pts[0, 1] - pts[0, 0]));
            frontVec.y = 0f;
            sideVec.y = 0f;
            var bottom = 0.25f * (pts[0, 0] + pts[0, 1] + pts[1, 0] + pts[1, 1]);

            float yaw;
            if (frontVec.magnitude > 0.02f)
            {
                yaw = Mathf.Atan2(frontVec.x, frontVec.z);
            }
            else if (det.ModelFrontWorld.sqrMagnitude > 1e-4f)
            {
                yaw = Mathf.Atan2(det.ModelFrontWorld.x, det.ModelFrontWorld.z);
            }
            else if (sideVec.magnitude > 0.02f)
            {
                // Front is perpendicular to side; sign resolved by the optimiser.
                yaw = Mathf.Atan2(sideVec.x, sideVec.z) - Mathf.PI * 0.5f;
            }
            else
            {
                reason = "degenerate_footprint";
                return false;
            }

            var right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            if (sideVec.magnitude > 0.02f)
            {
                chi = Vector3.Dot(sideVec, right) >= 0f ? 1 : -1;
            }
            else if (layout.SideHandedness != 0)
            {
                chi = layout.SideHandedness;
            }
            else if (det.ModelSideWorld.sqrMagnitude > 1e-4f)
            {
                chi = Vector3.Dot(det.ModelSideWorld, right) >= 0f ? 1 : -1;
            }
            else
            {
                chi = -1;
            }

            var depth = Mathf.Max(MinDim, frontVec.magnitude);
            var width = Mathf.Max(MinDim, sideVec.magnitude);

            // Height from top-corner rays vs vertical lines through the footprint corners.
            var heightSum = 0f;
            var heightN = 0;
            for (var i = 1; i <= 8; i++)
            {
                if (!det.Has[i] || !layout.IsTop(i))
                {
                    continue;
                }

                var fi = layout.FrontSign(i) > 0 ? 1 : 0;
                var si = layout.SideSign(i) > 0 ? 1 : 0;
                var ray = cam.ViewportToRay(det.Viewport[i]);
                if (ClosestParamOnVertical(pts[fi, si], ray, out var t) && t > 0.01f && t < 3f)
                {
                    heightSum += t;
                    heightN++;
                }
            }

            float height;
            if (heightN > 0)
            {
                height = heightSum / heightN;
            }
            else if (det.Has[0] && ClosestParamOnVertical(bottom, cam.ViewportToRay(det.Viewport[0]), out var tc) &&
                     tc > 0.01f)
            {
                height = 2f * tc;
            }
            else
            {
                height = info.TypicalHeight;
            }

            theta = new[] { bottom.x, bottom.z, yaw, width, Mathf.Max(MinDim, height), depth, y0 };
            return true;
        }

        public static bool IntersectHorizontal(Ray ray, float y, out Vector3 point)
        {
            point = default;
            var dy = ray.direction.y;
            if (dy > -1e-3f)
            {
                return false;
            }

            var t = (y - ray.origin.y) / dy;
            if (t < 0.05f || t > 10f)
            {
                return false;
            }

            point = ray.origin + ray.direction * t;
            return true;
        }

        /// <summary>Parameter t of the point on the vertical line (p + up·t) closest to the ray.</summary>
        public static bool ClosestParamOnVertical(Vector3 p, Ray ray, out float t)
        {
            var u = Vector3.up;
            var d = ray.direction.normalized;
            var w0 = p - ray.origin;
            var b = Vector3.Dot(u, d);
            var dd = Vector3.Dot(u, w0);
            var e = Vector3.Dot(d, w0);
            var denom = 1f - b * b;
            if (denom < 1e-4f)
            {
                t = 0f;
                return false;
            }

            t = (b * e - dd) / denom;
            return true;
        }

        // ---------------------------------------------------------------- linear algebra

        private static bool SolveDamped(float[,] jtj, float[] jtr, float lambda, float[] x)
        {
            const int n = ParamCount;
            var a = new double[n, n + 1];
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    a[i, j] = jtj[i, j];
                }

                a[i, i] += lambda * (jtj[i, i] + 1e-6);
                a[i, n] = jtr[i];
            }

            for (var col = 0; col < n; col++)
            {
                var pivot = col;
                var best = System.Math.Abs(a[col, col]);
                for (var row = col + 1; row < n; row++)
                {
                    var v = System.Math.Abs(a[row, col]);
                    if (v > best)
                    {
                        best = v;
                        pivot = row;
                    }
                }

                if (best < 1e-12)
                {
                    return false;
                }

                if (pivot != col)
                {
                    for (var j = col; j <= n; j++)
                    {
                        (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
                    }
                }

                for (var row = col + 1; row < n; row++)
                {
                    var f = a[row, col] / a[col, col];
                    if (f == 0)
                    {
                        continue;
                    }

                    for (var j = col; j <= n; j++)
                    {
                        a[row, j] -= f * a[col, j];
                    }
                }
            }

            for (var i = n - 1; i >= 0; i--)
            {
                var s = a[i, n];
                for (var j = i + 1; j < n; j++)
                {
                    s -= a[i, j] * x[j];
                }

                x[i] = (float)(s / a[i, i]);
                if (float.IsNaN(x[i]) || float.IsInfinity(x[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
