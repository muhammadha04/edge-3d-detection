// Gravity-aligned oriented box: the canonical 3D output of the whole pipeline.
using UnityEngine;

namespace QuestDetect3D
{
    public struct UprightBox
    {
        /// <summary>Centre of the bottom face (on the support surface), world metres.</summary>
        public Vector3 BottomCenter;

        /// <summary>Heading of the box local +Z (object front) around world up, radians.</summary>
        public float Yaw;

        /// <summary>x = width (local X), y = height, z = depth (local Z), metres.</summary>
        public Vector3 Size;

        public UprightBox(Vector3 bottomCenter, float yaw, Vector3 size)
        {
            BottomCenter = bottomCenter;
            Yaw = yaw;
            Size = size;
        }

        public Vector3 Center => BottomCenter + Vector3.up * (Size.y * 0.5f);
        public Vector3 TopCenter => BottomCenter + Vector3.up * Size.y;
        public Quaternion Rotation => Quaternion.Euler(0f, Yaw * Mathf.Rad2Deg, 0f);
        public Vector3 Forward => new Vector3(Mathf.Sin(Yaw), 0f, Mathf.Cos(Yaw));
        public Vector3 Right => new Vector3(Mathf.Cos(Yaw), 0f, -Mathf.Sin(Yaw));

        /// <summary>Corner at local signs (sx, sz ∈ ±1) and level (0 bottom, 1 top).</summary>
        public Vector3 Corner(int sx, int top, int sz)
        {
            return BottomCenter
                   + Right * (0.5f * Size.x * sx)
                   + Vector3.up * (Size.y * top)
                   + Forward * (0.5f * Size.z * sz);
        }

        /// <summary>8 corners, index bits: 1 = +x, 2 = top, 4 = +z.</summary>
        public void GetCorners(Vector3[] corners)
        {
            for (var i = 0; i < 8; i++)
            {
                corners[i] = Corner((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : 0, (i & 4) != 0 ? 1 : -1);
            }
        }

        public override string ToString()
        {
            return $"bottom=({BottomCenter.x:F2},{BottomCenter.y:F2},{BottomCenter.z:F2}) " +
                   $"yaw={Yaw * Mathf.Rad2Deg:F0}° size=({Size.x * 100f:F0}x{Size.y * 100f:F0}x{Size.z * 100f:F0})cm";
        }
    }
}
