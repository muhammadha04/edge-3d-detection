// Box editor operations on an UprightBox (pure maths, unit-tested). All edits keep the box upright.
// Faces: 0 = +X (right), 1 = -X (left), 2 = +Z (front), 3 = -Z (back), 4 = +Y (top), 5 = -Y (bottom).
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public static class BoxEdit
    {
        public const float MinSize = 0.01f;
        public static readonly string[] FaceNames = { "+x", "-x", "+z", "-z", "+y", "-y" };

        /// <summary>Move one face outward by <paramref name="delta"/> metres (negative = inward); the opposite face stays.</summary>
        public static UprightBox PushFace(UprightBox b, int face, float delta)
        {
            var s = b.Size;
            switch (face)
            {
                case 0:
                case 1:
                {
                    var newW = Mathf.Max(MinSize, s.x + delta);
                    var applied = newW - s.x;
                    s.x = newW;
                    b.BottomCenter += b.Right * (0.5f * applied * (face == 0 ? 1f : -1f));
                    break;
                }
                case 2:
                case 3:
                {
                    var newD = Mathf.Max(MinSize, s.z + delta);
                    var applied = newD - s.z;
                    s.z = newD;
                    b.BottomCenter += b.Forward * (0.5f * applied * (face == 2 ? 1f : -1f));
                    break;
                }
                case 4:
                    s.y = Mathf.Max(MinSize, s.y + delta);
                    break;
                case 5:
                {
                    var newH = Mathf.Max(MinSize, s.y + delta);
                    var applied = newH - s.y;
                    s.y = newH;
                    b.BottomCenter += Vector3.down * applied;
                    break;
                }
            }

            b.Size = s;
            return b;
        }

        /// <summary>Outward unit normal of a face (world/room frame of the box).</summary>
        public static Vector3 FaceNormal(UprightBox b, int face)
        {
            switch (face)
            {
                case 0: return b.Right;
                case 1: return -b.Right;
                case 2: return b.Forward;
                case 3: return -b.Forward;
                case 4: return Vector3.up;
                default: return Vector3.down;
            }
        }

        public static Vector3 FaceCenter(UprightBox b, int face)
        {
            var c = b.Center;
            switch (face)
            {
                case 0: return c + b.Right * (0.5f * b.Size.x);
                case 1: return c - b.Right * (0.5f * b.Size.x);
                case 2: return c + b.Forward * (0.5f * b.Size.z);
                case 3: return c - b.Forward * (0.5f * b.Size.z);
                case 4: return b.TopCenter;
                default: return b.BottomCenter;
            }
        }

        public static UprightBox Move(UprightBox b, Vector3 delta)
        {
            b.BottomCenter += delta;
            return b;
        }

        /// <summary>Rotate about the box's vertical centre axis.</summary>
        public static UprightBox Rotate(UprightBox b, float deltaYaw)
        {
            b.Yaw = RoomFrame.WrapPi(b.Yaw + deltaYaw);
            return b;
        }

        /// <summary>Symmetric resize of width/depth about the centre; height grows upward (bottom stays).</summary>
        public static UprightBox Resize(UprightBox b, float dWidth, float dHeight, float dDepth)
        {
            b.Size = new Vector3(Mathf.Max(MinSize, b.Size.x + dWidth), Mathf.Max(MinSize, b.Size.y + dHeight),
                Mathf.Max(MinSize, b.Size.z + dDepth));
            return b;
        }

        /// <summary>Turn the front arrow by quarter turns. The box itself does not change: width and depth swap.</summary>
        public static UprightBox TurnFront(UprightBox b, int quarters)
        {
            quarters = ((quarters % 4) + 4) % 4;
            b.Yaw = RoomFrame.WrapPi(b.Yaw + quarters * 0.5f * Mathf.PI);
            if ((quarters & 1) == 1)
            {
                b.Size = new Vector3(b.Size.z, b.Size.y, b.Size.x);
            }

            return b;
        }

        /// <summary>Bottom onto the floor, top kept where it is.</summary>
        public static UprightBox SnapToFloor(UprightBox b, float floorY)
        {
            var top = b.BottomCenter.y + b.Size.y;
            b.Size = new Vector3(b.Size.x, Mathf.Max(MinSize, top - floorY), b.Size.z);
            b.BottomCenter = new Vector3(b.BottomCenter.x, floorY, b.BottomCenter.z);
            return b;
        }

        /// <summary>Point in box-local coordinates: x along Right, y above bottom, z along Forward.</summary>
        public static Vector3 ToLocal(UprightBox b, Vector3 p)
        {
            var d = p - b.BottomCenter;
            return new Vector3(Vector3.Dot(d, b.Right), d.y, Vector3.Dot(d, b.Forward));
        }

        public static bool Contains(UprightBox b, Vector3 p, float margin = 0f)
        {
            var l = ToLocal(b, p);
            return Mathf.Abs(l.x) <= 0.5f * b.Size.x + margin && Mathf.Abs(l.z) <= 0.5f * b.Size.z + margin &&
                   l.y >= -margin && l.y <= b.Size.y + margin;
        }

        /// <summary>Euclidean distance from the point to the box (0 when inside).</summary>
        public static float OutsideDistance(UprightBox b, Vector3 p)
        {
            var l = ToLocal(b, p);
            var dx = Mathf.Max(0f, Mathf.Abs(l.x) - 0.5f * b.Size.x);
            var dz = Mathf.Max(0f, Mathf.Abs(l.z) - 0.5f * b.Size.z);
            var dy = l.y < 0f ? -l.y : Mathf.Max(0f, l.y - b.Size.y);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>First face hit by a ray from outside (slab test in box-local coordinates).</summary>
        public static bool RayFace(UprightBox b, Vector3 origin, Vector3 direction, out int face, out float distance)
        {
            face = -1;
            distance = 0f;
            var o = ToLocal(b, origin);
            var d = new Vector3(Vector3.Dot(direction, b.Right), direction.y, Vector3.Dot(direction, b.Forward));
            var min = new Vector3(-0.5f * b.Size.x, 0f, -0.5f * b.Size.z);
            var max = new Vector3(0.5f * b.Size.x, b.Size.y, 0.5f * b.Size.z);
            var tEnter = float.MinValue;
            var tExit = float.MaxValue;
            var enterFace = -1;
            for (var axis = 0; axis < 3; axis++)
            {
                var oa = axis == 0 ? o.x : axis == 1 ? o.y : o.z;
                var da = axis == 0 ? d.x : axis == 1 ? d.y : d.z;
                var lo = axis == 0 ? min.x : axis == 1 ? min.y : min.z;
                var hi = axis == 0 ? max.x : axis == 1 ? max.y : max.z;
                if (Mathf.Abs(da) < 1e-9f)
                {
                    if (oa < lo || oa > hi)
                    {
                        return false;
                    }

                    continue;
                }

                var t1 = (lo - oa) / da;
                var t2 = (hi - oa) / da;
                // Entering through the "lo" plane means the negative face of this axis.
                var f1 = axis == 0 ? 1 : axis == 1 ? 5 : 3;
                var f2 = axis == 0 ? 0 : axis == 1 ? 4 : 2;
                if (t1 > t2)
                {
                    (t1, t2) = (t2, t1);
                    (f1, f2) = (f2, f1);
                }

                if (t1 > tEnter)
                {
                    tEnter = t1;
                    enterFace = f1;
                }

                tExit = Mathf.Min(tExit, t2);
                if (tEnter > tExit)
                {
                    return false;
                }
            }

            if (tEnter < 0f || enterFace < 0)
            {
                return false; // origin inside the box (or box behind the ray)
            }

            face = enterFace;
            distance = tEnter;
            return true;
        }
    }
}
