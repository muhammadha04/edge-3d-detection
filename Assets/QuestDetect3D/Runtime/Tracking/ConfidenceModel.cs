// Confidence = how much we trust that the box is at the right place with the right size and heading.
//
// Per observation (one frame), quality q ∈ [0,1] is the product of independent sanity terms:
//   reprojection  – fitted 3D box re-projects onto the network's 2D keypoints (RMS / on-screen size, inliers)
//   support       – the floor/table height under the object is well measured by the Depth API
//   gravity       – the network's own 3D axes agree that the object is upright
//   size          – metric size is plausible for the category
//   distance      – far objects get fewer pixels
//   model         – detector score when available
// Per tracked object, confidence C fuses many observations:
//   evidence (how many good views) × consistency (position / yaw / size agree across views)
//   × agreement (fraction of inlier views) × viewpoint diversity × presence (seen when expected)
//   × mean observation quality.
// These are heuristic scores, not calibrated probabilities. Calibrate them against measured
// ground truth before treating "90 %" as a 90 % hit rate.
using UnityEngine;

namespace QuestDetect3D
{
    public struct QualityBreakdown
    {
        public float Reprojection;
        public float Support;
        public float Gravity;
        public float Size;
        public float Distance;
        public float Model;
        public float Total;

        public override string ToString() =>
            $"q={Total:F2} [reproj={Reprojection:F2} support={Support:F2} gravity={Gravity:F2} size={Size:F2} " +
            $"dist={Distance:F2} model={Model:F2}]";
    }

    public struct ConfidenceBreakdown
    {
        public float Evidence;
        public float Quality;
        public float Consistency;
        public float Agreement;
        public float Views;
        public float Presence;
        public float Total;

        public override string ToString() =>
            $"C={Total:F2} [evidence={Evidence:F2} quality={Quality:F2} consistency={Consistency:F2} " +
            $"agreement={Agreement:F2} views={Views:F2} presence={Presence:F2}]";
    }

    public static class ConfidenceModel
    {
        public const float ConfirmThreshold = 0.55f;
        public const int ConfirmMinHits = 4;

        public static QualityBreakdown ObservationQuality(
            in BoxFitResult fit,
            in SupportEstimate support,
            KeypointDetection det,
            in CategoryInfo info,
            float distanceM)
        {
            var q = new QualityBreakdown();
            // Scale-free: RMS error as a fraction of the object's on-screen size (8 % ≈ typical Objectron noise).
            q.Reprojection = Gaussian(fit.NormalizedRms, 0.08f) * (0.5f + 0.5f * fit.InlierFraction);

            var sigma = Mathf.Max(0f, support.Sigma - 0.01f);
            q.Support = Gaussian(sigma, 0.035f);
            if (!support.UsedFloorModel && support.Rays > 0)
            {
                q.Support *= 0.6f + 0.4f * Mathf.Clamp01(support.Hits / (float)support.Rays);
            }

            q.Gravity = float.IsNaN(det.ModelVerticality)
                ? 0.85f
                : Mathf.Clamp01((det.ModelVerticality - 0.55f) / 0.35f);
            q.Gravity = Mathf.Max(0.2f, q.Gravity);

            q.Size = SizePlausibility(fit.Box.Size, info);

            if (distanceM < 0.45f)
            {
                q.Distance = 0.6f;
            }
            else if (distanceM <= 2.8f)
            {
                q.Distance = 1f;
            }
            else
            {
                q.Distance = Mathf.Exp(-(distanceM - 2.8f) / 2f);
            }

            q.Model = Mathf.Clamp(det.ModelScore, 0.3f, 1f);
            q.Total = Mathf.Clamp01(q.Reprojection * q.Support * q.Gravity * q.Size * q.Distance * q.Model);
            return q;
        }

        public static float SizePlausibility(Vector3 size, in CategoryInfo info)
        {
            var shortSide = Mathf.Min(size.x, size.z);
            var longSide = Mathf.Max(size.x, size.z);
            return RangeScore(shortSide, info.ShortSide) * RangeScore(longSide, info.LongSide) *
                   RangeScore(size.y, info.Height);
        }

        public static float RangeScore(float v, Vector2 range)
        {
            if (v >= range.x && v <= range.y)
            {
                return 1f;
            }

            var edge = v < range.x ? range.x : range.y;
            return Gaussian(v - edge, Mathf.Max(0.01f, 0.2f * edge));
        }

        public static float Gaussian(float x, float sigma)
        {
            var t = x / sigma;
            return Mathf.Exp(-0.5f * t * t);
        }

        public static ConfidenceBreakdown TrackConfidence(
            float evidenceSum,
            float meanQuality,
            float positionSigmaM,
            float yawSigmaDeg,
            float sizeSigmaRel,
            float inlierFraction,
            float viewSpreadDeg,
            float misses,
            float effectiveViews)
        {
            var c = new ConfidenceBreakdown();
            c.Evidence = 1f - Mathf.Exp(-evidenceSum / 1.2f);
            c.Quality = Mathf.Pow(Mathf.Clamp01(meanQuality), 0.35f);
            // Consistency of the FUSED estimate: per-view scatter shrinks with the number of independent views
            // (standard error), so a noisy-but-unbiased detector converges to high confidence with more views.
            var sqrtN = Mathf.Sqrt(Mathf.Max(1f, effectiveViews));
            c.Consistency = Gaussian(positionSigmaM / sqrtN, 0.035f) * Gaussian(yawSigmaDeg / sqrtN, 8f) *
                            Gaussian(sizeSigmaRel / sqrtN, 0.07f);
            c.Agreement = Mathf.Pow(Mathf.Clamp01(inlierFraction), 1.5f);
            c.Views = 0.85f + 0.15f * Mathf.Clamp01(viewSpreadDeg / 25f);
            c.Presence = 1f / (1f + 0.15f * Mathf.Max(0f, misses));
            c.Total = Mathf.Clamp(c.Evidence * c.Quality * c.Consistency * c.Agreement * c.Views * c.Presence, 0f, 0.99f);
            return c;
        }
    }
}
