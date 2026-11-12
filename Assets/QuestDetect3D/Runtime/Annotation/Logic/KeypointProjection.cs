// The 9 box keypoints used by the dataset export, and their projection into a recorded PCA frame.
// Keypoint order (documented in Docs and mirrored by Tools/annotation/qd3d/geometry.py):
//   0 = box centre; 1..8 = corners in UprightBox.GetCorners order, corner index c = k − 1 with bits
//   1 = +X (right), 2 = top, 4 = +Z (front):
//   1 (-x,bottom,-z) 2 (+x,bottom,-z) 3 (-x,top,-z) 4 (+x,top,-z) 5 (-x,bottom,+z) 6 (+x,bottom,+z) 7 (-x,top,+z) 8 (+x,top,+z)
// Image pixels: origin top-left of the saved JPEG, u = viewport.x × W, v = (1 − viewport.y) × H (viewport origin
// bottom-left, see CameraModel). The C# and Python versions are checked against one shared fixture.
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public static class KeypointProjection
    {
        public const int Count = 9;

        /// <summary>The 9 keypoints (centre + 8 corners) of a box, in the box's frame.</summary>
        public static Vector3[] Keypoints3D(UprightBox box)
        {
            var corners = new Vector3[8];
            box.GetCorners(corners);
            var k = new Vector3[Count];
            k[0] = box.Center;
            for (var i = 0; i < 8; i++)
            {
                k[i + 1] = corners[i];
            }

            return k;
        }

        /// <summary>World point → JPEG pixel (top-left origin). False when behind the camera.</summary>
        public static bool ToPixel(in CameraModel cam, Vector3 world, out Vector2 pixel)
        {
            if (!cam.WorldToViewport(world, out var vp))
            {
                pixel = default;
                return false;
            }

            pixel = new Vector2(vp.x * cam.Resolution.x, (1f - vp.y) * cam.Resolution.y);
            return true;
        }
    }
}
