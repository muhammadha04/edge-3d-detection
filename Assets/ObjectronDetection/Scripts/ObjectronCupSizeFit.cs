// Compares Objectron box edge lengths to known physical cup dimensions.

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronCupSizeFit
    {
        /// <summary>Full extents in meters: X=11cm (handle+cylinder), Y=10cm, Z=8cm (handle on left).</summary>
        public static readonly Vector3 ReferenceExtentsM = new(0.11f, 0.10f, 0.08f);

        /// <summary>
        /// Lower is better. Rotation-invariant: sorts both detected and reference edge lengths
        /// before comparing relative squared error per axis.
        /// </summary>
        public static bool TryScore(Vector3[] corners, out float score, out Vector3 detectedSortedM)
        {
            score = float.MaxValue;
            detectedSortedM = default;
            if (!ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(corners, out var detected))
            {
                return false;
            }

            detectedSortedM = SortAscending(detected);
            var referenceSortedM = SortAscending(ReferenceExtentsM);
            score = RelativeSquaredError(detectedSortedM, referenceSortedM);
            return true;
        }

        /// <summary>True when candidate is meaningfully closer to reference dimensions than incumbent.</summary>
        public static bool IsBetterFit(float candidateScore, float incumbentScore, float minRelativeImprovement = 0.05f)
        {
            if (candidateScore >= incumbentScore)
            {
                return false;
            }

            if (float.IsPositiveInfinity(incumbentScore) || incumbentScore >= float.MaxValue * 0.5f)
            {
                return true;
            }

            return candidateScore <= incumbentScore * (1f - minRelativeImprovement);
        }

        public static string FormatExtentsCm(Vector3 sortedMeters) =>
            $"({sortedMeters.x * 100f:F0},{sortedMeters.y * 100f:F0},{sortedMeters.z * 100f:F0})cm";

        private static Vector3 SortAscending(Vector3 v)
        {
            var a = v.x;
            var b = v.y;
            var c = v.z;
            if (a > b)
            {
                (a, b) = (b, a);
            }

            if (b > c)
            {
                (b, c) = (c, b);
            }

            if (a > b)
            {
                (a, b) = (b, a);
            }

            return new Vector3(a, b, c);
        }

        private static float RelativeSquaredError(Vector3 detectedSortedM, Vector3 referenceSortedM)
        {
            var ex = SquaredRelativeError(detectedSortedM.x, referenceSortedM.x);
            var ey = SquaredRelativeError(detectedSortedM.y, referenceSortedM.y);
            var ez = SquaredRelativeError(detectedSortedM.z, referenceSortedM.z);
            return ex + ey + ez;
        }

        private static float SquaredRelativeError(float detectedM, float referenceM)
        {
            if (referenceM < 0.001f)
            {
                return detectedM * detectedM;
            }

            var relative = (detectedM - referenceM) / referenceM;
            return relative * relative;
        }
    }
}
