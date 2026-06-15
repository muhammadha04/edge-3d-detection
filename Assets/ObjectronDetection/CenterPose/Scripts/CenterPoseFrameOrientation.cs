using Mediapipe.Unity;
using UnityEngine;

namespace QuestObjectron.CenterPose
{
    /// <summary>Applies the same rotation / mirror as PassthroughImageSource before CenterPose inference.</summary>
    public static class CenterPoseFrameOrientation
    {
        public static Color32[] Orient(Color32[] src, int width, int height, RotationAngle rotation, bool flipHorizontal)
        {
            var rotated = Rotate(src, width, height, rotation, out var outW, out var outH);
            if (!flipHorizontal)
            {
                return rotated;
            }

            return FlipHorizontal(rotated, outW, outH);
        }

        private static Color32[] Rotate(Color32[] src, int width, int height, RotationAngle rotation, out int outW, out int outH)
        {
            switch (rotation)
            {
                case RotationAngle.Rotation90:
                    outW = height;
                    outH = width;
                    return Rotate90Cw(src, width, height);
                case RotationAngle.Rotation180:
                    outW = width;
                    outH = height;
                    return Rotate180(src, width, height);
                case RotationAngle.Rotation270:
                    outW = height;
                    outH = width;
                    return Rotate90Ccw(src, width, height);
                default:
                    outW = width;
                    outH = height;
                    return (Color32[])src.Clone();
            }
        }

        private static Color32[] FlipHorizontal(Color32[] src, int width, int height)
        {
            var dst = new Color32[src.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    dst[y * width + x] = src[y * width + (width - 1 - x)];
                }
            }

            return dst;
        }

        private static Color32[] Rotate90Cw(Color32[] src, int width, int height)
        {
            var dst = new Color32[src.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var nx = height - 1 - y;
                    var ny = x;
                    dst[ny * height + nx] = src[y * width + x];
                }
            }

            return dst;
        }

        private static Color32[] Rotate90Ccw(Color32[] src, int width, int height)
        {
            var dst = new Color32[src.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var nx = y;
                    var ny = width - 1 - x;
                    dst[ny * height + nx] = src[y * width + x];
                }
            }

            return dst;
        }

        private static Color32[] Rotate180(Color32[] src, int width, int height)
        {
            var dst = new Color32[src.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    dst[(height - 1 - y) * width + (width - 1 - x)] = src[y * width + x];
                }
            }

            return dst;
        }
    }
}
