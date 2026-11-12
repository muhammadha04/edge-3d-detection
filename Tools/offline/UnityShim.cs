// Minimal, faithful subset of UnityEngine math types so the pure pipeline code (geometry, tracking, decoding)
// can be compiled with mono and tested without Unity or a headset. Conventions match Unity:
// left-handed, Quaternion.Euler applies Z, then X, then Y; Ray normalises its direction.
using System;

namespace UnityEngine
{
    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;
        public const float Infinity = float.PositiveInfinity;
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Abs(float f) => Math.Abs(f);
        public static int Abs(int f) => Math.Abs(f);
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
        public static int Clamp(int v, int a, int b) => v < a ? a : v > b ? b : v;
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Exp(float f) => (float)Math.Exp(f);
        public static float Log(float f) => (float)Math.Log(f);
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Acos(float f) => (float)Math.Acos(f);
        public static float Pow(float a, float b) => (float)Math.Pow(a, b);
        public static float Round(float f) => (float)Math.Round(f);
        public static int RoundToInt(float f) => (int)Math.Round(f);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
        public static float Sign(float f) => f >= 0f ? 1f : -1f;
        public static float Repeat(float t, float length) => Clamp(t - (float)Math.Floor(t / length) * length, 0f, length);
        public static float DeltaAngle(float current, float target)
        {
            var delta = Repeat(target - current, 360f);
            if (delta > 180f) delta -= 360f;
            return delta;
        }
        public static float MoveTowards(float current, float target, float maxDelta) =>
            Math.Abs(target - current) <= maxDelta ? target : current + Math.Sign(target - current) * maxDelta;
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public float sqrMagnitude => x * x + y * y;
        public float magnitude => Mathf.Sqrt(sqrMagnitude);
        public Vector2 normalized { get { var m = magnitude; return m > 1e-5f ? this / m : zero; } }
        public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);
        public static Vector2 operator /(Vector2 a, Vector2 b) => new Vector2(a.x / b.x, a.y / b.y);
        public override string ToString() => $"({x:F2}, {y:F2})";
    }

    public struct Vector2Int
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
        public static Vector2Int zero => new Vector2Int(0, 0);
        public static bool operator ==(Vector2Int a, Vector2Int b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2Int a, Vector2Int b) => !(a == b);
        public override bool Equals(object o) => o is Vector2Int v && v == this;
        public override int GetHashCode() => x * 73856093 ^ y;
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 down => new Vector3(0, -1, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);
        public static Vector3 right => new Vector3(1, 0, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => Mathf.Sqrt(sqrMagnitude);
        public Vector3 normalized { get { var m = magnitude; return m > 1e-5f ? this / m : zero; } }
        public void Normalize() { this = normalized; }
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * Mathf.Clamp01(t);
        public static Vector3 ProjectOnPlane(Vector3 v, Vector3 n) { var m = n.sqrMagnitude; return m < 1e-12f ? v : v - n * (Dot(v, n) / m); }
        public static float Angle(Vector3 a, Vector3 b)
        {
            var d = Mathf.Sqrt(a.sqrMagnitude * b.sqrMagnitude);
            if (d < 1e-15f) return 0f;
            return Mathf.Acos(Mathf.Clamp(Dot(a, b) / d, -1f, 1f)) * Mathf.Rad2Deg;
        }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 9.99999944E-11f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object o) => o is Vector3 v && v == this;
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() << 2 ^ z.GetHashCode() >> 2;
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);

        public static Quaternion AngleAxis(float angleDeg, Vector3 axis)
        {
            axis = axis.normalized;
            var h = angleDeg * Mathf.Deg2Rad * 0.5f;
            var s = Mathf.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }

        // Unity: rotates z around Z, then x around X, then y around Y (extrinsic) → q = qy * qx * qz.
        public static Quaternion Euler(float x, float y, float z) =>
            AngleAxis(y, Vector3.up) * AngleAxis(x, Vector3.right) * AngleAxis(z, Vector3.forward);

        public static Quaternion Inverse(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
            a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            var u = new Vector3(q.x, q.y, q.z);
            var t = 2f * Vector3.Cross(u, v);
            return v + q.w * t + Vector3.Cross(u, t);
        }

        public static float Angle(Quaternion a, Quaternion b)
        {
            var d = Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            return d > 0.999999f ? 0f : (float)(Math.Acos(Math.Min(d, 1f)) * 2f * Mathf.Rad2Deg);
        }

        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            var f = forward.normalized;
            var r = Vector3.Cross(up, f).normalized;
            var u = Vector3.Cross(f, r);
            // Rotation matrix columns r, u, f → quaternion.
            float m00 = r.x, m01 = u.x, m02 = f.x, m10 = r.y, m11 = u.y, m12 = f.y, m20 = r.z, m21 = u.z, m22 = f.z;
            var tr = m00 + m11 + m22;
            Quaternion q;
            if (tr > 0f)
            {
                var s = Mathf.Sqrt(tr + 1f) * 2f;
                q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                var s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                var s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                var s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }

            return q;
        }

        public static Quaternion LookRotation(Vector3 forward) => LookRotation(forward, Vector3.up);
    }

    public struct Ray
    {
        public Vector3 origin;
        private Vector3 m_dir;
        public Ray(Vector3 origin, Vector3 direction) { this.origin = origin; m_dir = direction.normalized; }
        public Vector3 direction { get => m_dir; set => m_dir = value.normalized; }
        public Vector3 GetPoint(float d) => origin + m_dir * d;
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
        public static Rect MinMaxRect(float xmin, float ymin, float xmax, float ymax) => new Rect(xmin, ymin, xmax - xmin, ymax - ymin);
        public float xMin => Math.Min(x, x + width);
        public float yMin => Math.Min(y, y + height);
        public float xMax => Math.Max(x, x + width);
        public float yMax => Math.Max(y, y + height);
        public Vector2 center => new Vector2(x + width * 0.5f, y + height * 0.5f);
    }

    public static class Random
    {
        public static System.Random Source = new System.Random(7);
        public static float Range(float a, float b) => a + (float)Source.NextDouble() * (b - a);
        public static int Range(int a, int b) => Source.Next(a, b);
        public static float value => (float)Source.NextDouble();
    }

    public static class Debug
    {
        public static void Log(object o) => Console.WriteLine(o);
        public static void LogWarning(object o) => Console.WriteLine("W " + o);
        public static void LogError(object o) => Console.WriteLine("E " + o);
    }
}

namespace QuestDetect3D
{
    /// <summary>Offline stand-in for the on-device logger (Runtime/Core/Log.cs writes logcat + file).</summary>
    public static class Log
    {
        public static bool Verbose;
        public static void Info(string m) { if (Verbose) System.Console.WriteLine("I " + m); }
        public static void Warn(string m) => System.Console.WriteLine("W " + m);
        public static void Error(string m) => System.Console.WriteLine("E " + m);
        public static void Flush() { }
    }
}

namespace UnityEngine
{
    /// <summary>Minimal Transform (pose holder) for code paths that take the head transform.</summary>
    public class Transform
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 forward => rotation * Vector3.forward;
        public Vector3 up => rotation * Vector3.up;
        public Vector3 right => rotation * Vector3.right;
    }
}

namespace Meta.XR
{
    using UnityEngine;

    public struct EnvironmentRaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
    }

    /// <summary>Offline stand-in for MRUK's EnvironmentRaycastManager, backed by any IDepthRaycaster.</summary>
    public class EnvironmentRaycastManager
    {
        public static bool IsSupported = true;
        public QuestDetect3D.IDepthRaycaster Source;

        public bool Raycast(Ray ray, out EnvironmentRaycastHit hit, float maxDistance)
        {
            hit = default;
            if (Source == null || !Source.Raycast(ray, maxDistance, out var p, out var n))
            {
                return false;
            }

            hit.point = p;
            hit.normal = n;
            return true;
        }
    }
}
