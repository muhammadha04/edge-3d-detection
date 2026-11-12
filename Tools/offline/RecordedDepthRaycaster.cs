// IDepthRaycaster over a RECORDED Quest depth map (depth/NNNNNN.png + depth.jsonl), so the real DepthBoxFitter and
// tracker can be replayed on the PC (Tools/offline/run_tests.sh replay). Same conventions as the recorder:
//   world → depth pixel:  clip = M · [p, 1];  uv = (clip.xy / clip.w + 1) / 2;  col = uv.x W − 0.5, row = uv.y H − 0.5
//   linear depth of p:    zb0 / (clip.z / clip.w + zb1)       (metres, view-space z of the depth camera)
// A ray is marched until it passes behind the recorded surface, then refined by bisection; the normal comes from
// neighbouring depth pixels reprojected to the world.
using System;
using QuestDetect3D;
using UnityEngine;

public sealed class RecordedDepthRaycaster : IDepthRaycaster
{
    private ushort[] m_mm;
    private int m_w;
    private int m_h;
    private readonly double[] m_m = new double[16];
    private double[] m_inv = new double[16];
    private double m_zb0;
    private double m_zb1;

    public bool IsAvailable => m_mm != null;
    public int Rays { get; private set; }

    /// <summary>Uses one recorded depth map (uint16 mm, row 0 first) with its reprojection matrix and z-buffer parameters.</summary>
    public void SetFrame(ushort[] mm, int width, int height, double[] reprojection, double zb0, double zb1)
    {
        m_mm = mm;
        m_w = width;
        m_h = height;
        Array.Copy(reprojection, m_m, 16);
        m_inv = Invert(m_m) ?? throw new ArgumentException("singular reprojection matrix");
        m_zb0 = zb0;
        m_zb1 = zb1;
    }

    /// <summary>No depth for the current frame (every raycast misses).</summary>
    public void Clear() => m_mm = null;

    /// <summary>Recorded depth (m) at the pixel the point projects to, and the point's own linear depth.</summary>
    private bool Sample(Vector3 p, out double measured, out double own, out int col, out int row)
    {
        measured = own = 0;
        col = row = -1;
        var x = m_m[0] * p.x + m_m[1] * p.y + m_m[2] * p.z + m_m[3];
        var y = m_m[4] * p.x + m_m[5] * p.y + m_m[6] * p.z + m_m[7];
        var z = m_m[8] * p.x + m_m[9] * p.y + m_m[10] * p.z + m_m[11];
        var w = m_m[12] * p.x + m_m[13] * p.y + m_m[14] * p.z + m_m[15];
        if (w <= 1e-6)
        {
            return false;
        }

        col = (int)Math.Round((x / w + 1.0) * 0.5 * m_w - 0.5);
        row = (int)Math.Round((y / w + 1.0) * 0.5 * m_h - 0.5);
        if (col < 0 || row < 0 || col >= m_w || row >= m_h)
        {
            return false;
        }

        var v = m_mm[row * m_w + col];
        if (v == 0)
        {
            return false;
        }

        measured = v / 1000.0;
        own = m_zb0 / (z / w + m_zb1);
        return true;
    }

    /// <summary>Depth pixel → world point (false when the pixel has no depth).</summary>
    public Vector3 Unproject(int col, int row, out bool ok)
    {
        ok = false;
        if (col < 0 || row < 0 || col >= m_w || row >= m_h || m_mm[row * m_w + col] == 0)
        {
            return default;
        }

        var d = m_mm[row * m_w + col] / 1000.0;
        var nx = 2.0 * (col + 0.5) / m_w - 1.0;
        var ny = 2.0 * (row + 0.5) / m_h - 1.0;
        var nz = m_zb0 / d - m_zb1;
        var hx = m_inv[0] * nx + m_inv[1] * ny + m_inv[2] * nz + m_inv[3];
        var hy = m_inv[4] * nx + m_inv[5] * ny + m_inv[6] * nz + m_inv[7];
        var hz = m_inv[8] * nx + m_inv[9] * ny + m_inv[10] * nz + m_inv[11];
        var hw = m_inv[12] * nx + m_inv[13] * ny + m_inv[14] * nz + m_inv[15];
        ok = Math.Abs(hw) > 1e-12;
        return ok ? new Vector3((float)(hx / hw), (float)(hy / hw), (float)(hz / hw)) : default;
    }

    /// <summary>IDepthRaycaster: first recorded surface along the ray (march 0.5–1 % of the distance, then 10 bisection steps).</summary>
    public bool Raycast(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
    {
        point = default;
        normal = Vector3.up;
        Rays++;
        if (m_mm == null)
        {
            return false;
        }

        var o = ray.origin;
        var d = ray.direction.normalized;
        var prevT = 0.05f;
        var t = prevT;
        var hadSample = false;
        while (t <= maxDistance)
        {
            var p = o + d * t;
            if (Sample(p, out var measured, out var own, out _, out _))
            {
                if (own >= measured)
                {
                    if (!hadSample)
                    {
                        return false; // already behind the surface at the first sample (inside an object)
                    }

                    // Bisection between prevT (in front) and t (behind).
                    float lo = prevT, hi = t;
                    for (var i = 0; i < 10; i++)
                    {
                        var mid = 0.5f * (lo + hi);
                        if (Sample(o + d * mid, out var mm, out var ow, out _, out _) && ow >= mm)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid;
                        }
                    }

                    point = o + d * hi;
                    Sample(point, out _, out _, out var c, out var r);
                    normal = NormalAt(c, r, d);
                    return true;
                }

                hadSample = true;
            }

            prevT = t;
            t += Mathf.Max(0.005f, 0.01f * t);
        }

        return false;
    }

    /// <summary>Surface normal from neighbouring depth pixels, facing the ray.</summary>
    private Vector3 NormalAt(int col, int row, Vector3 rayDir)
    {
        var p0 = Unproject(col, row, out var ok0);
        var px = Unproject(col + 1, row, out var okx);
        if (!okx)
        {
            px = Unproject(col - 1, row, out okx);
            px = p0 - (px - p0);
        }

        var py = Unproject(col, row + 1, out var oky);
        if (!oky)
        {
            py = Unproject(col, row - 1, out oky);
            py = p0 - (py - p0);
        }

        if (!ok0 || !okx || !oky)
        {
            return -rayDir;
        }

        var n = Vector3.Cross(px - p0, py - p0).normalized;
        if (n.sqrMagnitude < 0.5f)
        {
            return -rayDir;
        }

        return Vector3.Dot(n, rayDir) > 0f ? -n : n;
    }

    /// <summary>4×4 matrix inverse (Gauss-Jordan), null when singular.</summary>
    private static double[] Invert(double[] m)
    {
        var a = new double[16];
        var inv = new double[16];
        Array.Copy(m, a, 16);
        for (var i = 0; i < 4; i++)
        {
            inv[i * 4 + i] = 1;
        }

        for (var c = 0; c < 4; c++)
        {
            var p = c;
            for (var r = c + 1; r < 4; r++)
            {
                if (Math.Abs(a[r * 4 + c]) > Math.Abs(a[p * 4 + c]))
                {
                    p = r;
                }
            }

            if (Math.Abs(a[p * 4 + c]) < 1e-15)
            {
                return null;
            }

            for (var k = 0; k < 4; k++)
            {
                var t1 = a[c * 4 + k];
                a[c * 4 + k] = a[p * 4 + k];
                a[p * 4 + k] = t1;
                var t2 = inv[c * 4 + k];
                inv[c * 4 + k] = inv[p * 4 + k];
                inv[p * 4 + k] = t2;
            }

            var f = a[c * 4 + c];
            for (var k = 0; k < 4; k++)
            {
                a[c * 4 + k] /= f;
                inv[c * 4 + k] /= f;
            }

            for (var r = 0; r < 4; r++)
            {
                if (r == c)
                {
                    continue;
                }

                var g = a[r * 4 + c];
                for (var k = 0; k < 4; k++)
                {
                    a[r * 4 + k] -= g * a[c * 4 + k];
                    inv[r * 4 + k] -= g * inv[c * 4 + k];
                }
            }
        }

        return inv;
    }
}
