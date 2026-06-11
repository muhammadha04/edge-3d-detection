// Objectron 9-point box geometry (0=center, 1-8=corners).

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronBoxGeometry
    {
        private static readonly int[][] s_faces =
        {
            new[] { 1, 2, 3, 4 },
            new[] { 5, 6, 7, 8 },
            new[] { 1, 2, 5, 6 },
            new[] { 3, 4, 7, 8 },
            new[] { 1, 3, 5, 7 },
            new[] { 2, 4, 6, 8 },
        };

        /// <summary>Four corner indices (1-8) on the face whose outward normal is most downward in world space.</summary>
        public static bool TryGetTableCornerIndices(Vector3[] corners, out int[] tableCornerIndices)
        {
            tableCornerIndices = null;
            if (corners == null || corners.Length < 9)
            {
                return false;
            }

            var bestFace = -1;
            var bestUpDot = float.MaxValue;
            for (var f = 0; f < s_faces.Length; f++)
            {
                var face = s_faces[f];
                var center = Vector3.zero;
                for (var i = 0; i < 4; i++)
                {
                    center += corners[face[i]];
                }

                center *= 0.25f;
                var outward = (center - corners[0]).normalized;
                var upDot = Vector3.Dot(outward, Vector3.up);
                if (upDot < bestUpDot)
                {
                    bestUpDot = upDot;
                    bestFace = f;
                }
            }

            if (bestFace < 0)
            {
                return false;
            }

            tableCornerIndices = (int[])s_faces[bestFace].Clone();
            return true;
        }

        public static Vector3 GetFaceOutwardNormal(Vector3[] corners, int[] faceCornerIndicesOneBased)
        {
            if (corners == null || faceCornerIndicesOneBased == null || faceCornerIndicesOneBased.Length < 4)
            {
                return Vector3.down;
            }

            var center = Vector3.zero;
            for (var i = 0; i < 4; i++)
            {
                center += corners[faceCornerIndicesOneBased[i]];
            }

            center *= 0.25f;
            return (center - corners[0]).normalized;
        }

        public static Vector3 GetBoxAxisWorld(Vector3[] corners, int cornerAOneBased, int cornerBOneBased)
        {
            return (corners[cornerBOneBased] - corners[cornerAOneBased]).normalized;
        }
    }
}
