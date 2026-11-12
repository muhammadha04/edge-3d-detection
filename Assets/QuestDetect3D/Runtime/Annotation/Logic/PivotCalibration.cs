// Controller-tip pivot calibration without markers (pure maths, unit-tested).
// The operator keeps the controller tip on one fixed point (a pen mark) and rotates the controller around it.
// For every pose i (rotation R_i, position p_i of the tracked controller anchor):  R_i t + p_i = P
// with t = tip offset in the controller frame and P = the fixed point in the world. Stacking all poses gives
// [R_i  −I] [t; P] = −p_i, solved by linear least squares (6×6 normal equations, double precision).
// One outlier pass drops poses whose residual exceeds 3× the median (a slipped tip), then re-solves.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public struct PivotResult
    {
        public bool Ok;
        public string Error;
        public Vector3 TipOffset;
        public Vector3 Pivot;
        public float RmsMm;
        public float MaxMm;
        /// <summary>Largest rotation between any pose and the first one: small spread = poorly conditioned.</summary>
        public float SpreadDeg;
        public int Used;
        public int Total;

        public override string ToString() =>
            Ok ? $"tip=({TipOffset.x * 1000f:F1},{TipOffset.y * 1000f:F1},{TipOffset.z * 1000f:F1})mm rms={RmsMm:F2}mm max={MaxMm:F2}mm spread={SpreadDeg:F0}° used={Used}/{Total}"
                : $"failed: {Error} (poses={Total}, spread={SpreadDeg:F0}°)";
    }

    public static class PivotCalibration
    {
        public const int MinPoses = 30;
        public const float MinSpreadDeg = 30f;
        public const float TargetRmsMm = 3f;

        public static PivotResult Solve(IReadOnlyList<Quaternion> rotations, IReadOnlyList<Vector3> positions)
        {
            var result = new PivotResult { Total = rotations.Count };
            if (rotations.Count != positions.Count || rotations.Count < MinPoses)
            {
                result.Error = $"need ≥ {MinPoses} poses";
                return result;
            }

            for (var i = 1; i < rotations.Count; i++)
            {
                result.SpreadDeg = Mathf.Max(result.SpreadDeg, Quaternion.Angle(rotations[0], rotations[i]));
            }

            if (result.SpreadDeg < MinSpreadDeg)
            {
                result.Error = $"rotate the controller more (spread {result.SpreadDeg:F0}° < {MinSpreadDeg:F0}°)";
                return result;
            }

            var use = new bool[rotations.Count];
            for (var i = 0; i < use.Length; i++)
            {
                use[i] = true;
            }

            if (!SolveSubset(rotations, positions, use, out var t, out var p))
            {
                result.Error = "singular system";
                return result;
            }

            // Outlier pass.
            var residuals = Residuals(rotations, positions, t, p);
            var sorted = new List<double>(residuals);
            sorted.Sort();
            var median = sorted[sorted.Count / 2];
            var threshold = Math.Max(3.0 * median, 0.001);
            var kept = 0;
            for (var i = 0; i < use.Length; i++)
            {
                use[i] = residuals[i] <= threshold;
                kept += use[i] ? 1 : 0;
            }

            if (kept >= MinPoses && kept < use.Length)
            {
                if (!SolveSubset(rotations, positions, use, out t, out p))
                {
                    result.Error = "singular system after outlier removal";
                    return result;
                }

                residuals = Residuals(rotations, positions, t, p);
            }
            else
            {
                for (var i = 0; i < use.Length; i++)
                {
                    use[i] = true;
                }

                kept = use.Length;
            }

            double sum = 0, max = 0;
            for (var i = 0; i < use.Length; i++)
            {
                if (!use[i])
                {
                    continue;
                }

                sum += residuals[i] * residuals[i];
                max = Math.Max(max, residuals[i]);
            }

            result.Ok = true;
            result.TipOffset = new Vector3((float)t[0], (float)t[1], (float)t[2]);
            result.Pivot = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            result.RmsMm = (float)(Math.Sqrt(sum / kept) * 1000.0);
            result.MaxMm = (float)(max * 1000.0);
            result.Used = kept;
            return result;
        }

        /// <summary>Rotation matrix (row-major) of a unit quaternion, same convention as Quaternion * Vector3.</summary>
        public static double[] Matrix(Quaternion q)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w;
            var n = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n > 0)
            {
                x /= n;
                y /= n;
                z /= n;
                w /= n;
            }

            return new[]
            {
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
            };
        }

        private static bool SolveSubset(IReadOnlyList<Quaternion> rotations, IReadOnlyList<Vector3> positions, bool[] use,
            out double[] t, out double[] p)
        {
            var ata = new double[36];
            var atb = new double[6];
            for (var i = 0; i < rotations.Count; i++)
            {
                if (!use[i])
                {
                    continue;
                }

                var r = Matrix(rotations[i]);
                double[] b = { -positions[i].x, -positions[i].y, -positions[i].z };
                // Row k of A_i: [r[k,0], r[k,1], r[k,2], -δ(k,0), -δ(k,1), -δ(k,2)]
                for (var k = 0; k < 3; k++)
                {
                    var row = new double[6];
                    row[0] = r[3 * k];
                    row[1] = r[3 * k + 1];
                    row[2] = r[3 * k + 2];
                    row[3 + k] = -1.0;
                    for (var a = 0; a < 6; a++)
                    {
                        atb[a] += row[a] * b[k];
                        for (var c = 0; c < 6; c++)
                        {
                            ata[6 * a + c] += row[a] * row[c];
                        }
                    }
                }
            }

            var x = SolveLinear(ata, atb, 6);
            t = null;
            p = null;
            if (x == null)
            {
                return false;
            }

            t = new[] { x[0], x[1], x[2] };
            p = new[] { x[3], x[4], x[5] };
            return true;
        }

        private static double[] Residuals(IReadOnlyList<Quaternion> rotations, IReadOnlyList<Vector3> positions, double[] t,
            double[] p)
        {
            var res = new double[rotations.Count];
            for (var i = 0; i < res.Length; i++)
            {
                var r = Matrix(rotations[i]);
                var ex = r[0] * t[0] + r[1] * t[1] + r[2] * t[2] + positions[i].x - p[0];
                var ey = r[3] * t[0] + r[4] * t[1] + r[5] * t[2] + positions[i].y - p[1];
                var ez = r[6] * t[0] + r[7] * t[1] + r[8] * t[2] + positions[i].z - p[2];
                res[i] = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            }

            return res;
        }

        /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
        public static double[] SolveLinear(double[] a, double[] b, int n)
        {
            var m = (double[])a.Clone();
            var v = (double[])b.Clone();
            for (var col = 0; col < n; col++)
            {
                var pivot = col;
                for (var row = col + 1; row < n; row++)
                {
                    if (Math.Abs(m[row * n + col]) > Math.Abs(m[pivot * n + col]))
                    {
                        pivot = row;
                    }
                }

                if (Math.Abs(m[pivot * n + col]) < 1e-12)
                {
                    return null;
                }

                if (pivot != col)
                {
                    for (var k = 0; k < n; k++)
                    {
                        var tmp = m[col * n + k];
                        m[col * n + k] = m[pivot * n + k];
                        m[pivot * n + k] = tmp;
                    }

                    var tv = v[col];
                    v[col] = v[pivot];
                    v[pivot] = tv;
                }

                for (var row = col + 1; row < n; row++)
                {
                    var f = m[row * n + col] / m[col * n + col];
                    for (var k = col; k < n; k++)
                    {
                        m[row * n + k] -= f * m[col * n + k];
                    }

                    v[row] -= f * v[col];
                }
            }

            var x = new double[n];
            for (var row = n - 1; row >= 0; row--)
            {
                var s = v[row];
                for (var k = row + 1; k < n; k++)
                {
                    s -= m[row * n + k] * x[k];
                }

                x[row] = s / m[row * n + row];
            }

            return x;
        }
    }
}
