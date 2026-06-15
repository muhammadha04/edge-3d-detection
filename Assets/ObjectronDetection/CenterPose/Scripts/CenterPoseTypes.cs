using UnityEngine;

namespace QuestObjectron.CenterPose
{
    /// <summary>MediaPipe-style normalized 2D keypoint (origin top-left, 0–1).</summary>
    public readonly struct NormalizedKeypoint2D
    {
        public readonly int Id;
        public readonly float X;
        public readonly float Y;

        public NormalizedKeypoint2D(int id, float x, float y)
        {
            Id = id;
            X = x;
            Y = y;
        }
    }

    public readonly struct CenterPoseDetection
    {
        public readonly float Score;
        public readonly NormalizedKeypoint2D[] Keypoints;
        public readonly Vector3 ObjScale;
        public readonly int ImageWidth;
        public readonly int ImageHeight;

        public CenterPoseDetection(
            float score,
            NormalizedKeypoint2D[] keypoints,
            Vector3 objScale,
            int imageWidth,
            int imageHeight)
        {
            Score = score;
            Keypoints = keypoints;
            ObjScale = objScale;
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
        }
    }

    public readonly struct CenterPosePreprocessMeta
    {
        public readonly Vector2 Center;
        public readonly float Scale;
        public readonly int Width;
        public readonly int Height;

        public CenterPosePreprocessMeta(Vector2 center, float scale, int width, int height)
        {
            Center = center;
            Scale = scale;
            Width = width;
            Height = height;
        }
    }
}
