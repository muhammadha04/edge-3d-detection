// Dedupe chair detections by MediaPipe object id and oriented box overlap.

using System.Collections.Generic;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronChairDedup
    {
        private const float CenterMatchRadiusM = 0.55f;
        private const float CenterOverlapRadiusM = 0.38f;
        private const float MinContainedCornerDistanceM = 0.12f;

        public static bool AreSameChair(
            int objectIdA,
            Vector3[] cornersA,
            int objectIdB,
            Vector3[] cornersB)
        {
            if (objectIdA > 0 && objectIdA == objectIdB)
            {
                return true;
            }

            if (cornersA == null || cornersB == null || cornersA.Length < 9 || cornersB.Length < 9)
            {
                return false;
            }

            var centerA = cornersA[0];
            var centerB = cornersB[0];
            var centerDist = Vector3.Distance(centerA, centerB);
            if (centerDist < CenterOverlapRadiusM)
            {
                return true;
            }

            if (centerDist < CenterMatchRadiusM
                && (BoxesOverlapSignificantly(cornersA, cornersB)
                    || HasCornerInsideOtherBox(cornersA, cornersB)))
            {
                return true;
            }

            return false;
        }

        public static int FindDuplicateIndex(
            IReadOnlyList<ObjectronLocalizedChairState> localizedChairs,
            int objectId,
            Vector3[] candidateCorners)
        {
            if (localizedChairs == null || candidateCorners == null)
            {
                return -1;
            }

            for (var i = 0; i < localizedChairs.Count; i++)
            {
                var chair = localizedChairs[i];
                if (chair.Corners == null)
                {
                    continue;
                }

                if (AreSameChair(objectId, candidateCorners, chair.ObjectId, chair.Corners))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool BoxesOverlapSignificantly(Vector3[] a, Vector3[] b)
        {
            if (!ObjectronOrientedBoxFitter.TryFitTransform(a, out var centerA, out var rotA, out var sizeA)
                || !ObjectronOrientedBoxFitter.TryFitTransform(b, out var centerB, out var rotB, out var sizeB))
            {
                return false;
            }

            var halfA = sizeA * 0.5f;
            var halfB = sizeB * 0.5f;
            var invRotA = Quaternion.Inverse(rotA);
            var localB = invRotA * (centerB - centerA);
            var extentB = ProjectHalfExtents(rotA, rotB, halfB);

            return Mathf.Abs(localB.x) < halfA.x + extentB.x
                   && Mathf.Abs(localB.y) < halfA.y + extentB.y
                   && Mathf.Abs(localB.z) < halfA.z + extentB.z;
        }

        private static bool HasCornerInsideOtherBox(Vector3[] a, Vector3[] b)
        {
            return AnyCornerNearBox(a, b) || AnyCornerNearBox(b, a);
        }

        private static bool AnyCornerNearBox(Vector3[] corners, Vector3[] box)
        {
            if (!ObjectronOrientedBoxFitter.TryFitTransform(box, out var center, out var rotation, out var size))
            {
                return false;
            }

            var invRot = Quaternion.Inverse(rotation);
            var half = size * 0.5f + Vector3.one * MinContainedCornerDistanceM;
            for (var i = 1; i <= 8; i++)
            {
                var local = invRot * (corners[i] - center);
                if (Mathf.Abs(local.x) <= half.x
                    && Mathf.Abs(local.y) <= half.y
                    && Mathf.Abs(local.z) <= half.z)
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector3 ProjectHalfExtents(Quaternion rotA, Quaternion rotB, Vector3 halfB)
        {
            var axisX = rotB * Vector3.right;
            var axisY = rotB * Vector3.up;
            var axisZ = rotB * Vector3.forward;
            return new Vector3(
                Mathf.Abs(Vector3.Dot(axisX, rotA * Vector3.right)) * halfB.x
                + Mathf.Abs(Vector3.Dot(axisY, rotA * Vector3.right)) * halfB.y
                + Mathf.Abs(Vector3.Dot(axisZ, rotA * Vector3.right)) * halfB.z,
                Mathf.Abs(Vector3.Dot(axisX, rotA * Vector3.up)) * halfB.x
                + Mathf.Abs(Vector3.Dot(axisY, rotA * Vector3.up)) * halfB.y
                + Mathf.Abs(Vector3.Dot(axisZ, rotA * Vector3.up)) * halfB.z,
                Mathf.Abs(Vector3.Dot(axisX, rotA * Vector3.forward)) * halfB.x
                + Mathf.Abs(Vector3.Dot(axisY, rotA * Vector3.forward)) * halfB.y
                + Mathf.Abs(Vector3.Dot(axisZ, rotA * Vector3.forward)) * halfB.z);
        }
    }
}
