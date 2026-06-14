// Composite detection quality for localize/refine gating (lower score = better).

using UnityEngine;

namespace QuestObjectron
{
    public readonly struct DetectionQualitySnapshot
    {
        public readonly float Score;
        public readonly float SizeFitScore;
        public readonly PlacementMethod Method;
        public readonly bool DepthRaycastHit;
        public readonly float StageAlignErrorPx;
        public readonly float CenterJumpM;

        public DetectionQualitySnapshot(
            float score,
            float sizeFitScore,
            PlacementMethod method,
            bool depthRaycastHit,
            float stageAlignErrorPx,
            float centerJumpM)
        {
            Score = score;
            SizeFitScore = sizeFitScore;
            Method = method;
            DepthRaycastHit = depthRaycastHit;
            StageAlignErrorPx = stageAlignErrorPx;
            CenterJumpM = centerJumpM;
        }
    }

    public static class ObjectronDetectionQuality
    {
        private const float DepthHitBonus = 0.12f;
        private const float DepthMissPenalty = 0.08f;
        private const float LargeJumpPenalty = 0.35f;
        private const float LargeJumpThresholdM = 0.3f;

        public static bool TryEvaluate(
            PlacementOutput output,
            float stageAlignErrorPx,
            float centerJumpM,
            out DetectionQualitySnapshot snapshot)
        {
            snapshot = default;
            if (output.Corners == null
                || output.Method == PlacementMethod.None
                || !ObjectronChairSizeFit.TryScore(output.Corners, out var sizeFit, out _))
            {
                return false;
            }

            var depthTerm = output.DebugReport?.DepthRaycastHit == true ? -DepthHitBonus : DepthMissPenalty;
            var alignTerm = Mathf.Clamp01(stageAlignErrorPx / 400f) * 0.15f;
            var jumpTerm = centerJumpM > LargeJumpThresholdM ? LargeJumpPenalty : 0f;
            var methodTerm = GetMethodPenalty(output.Method);
            var score = sizeFit + methodTerm + depthTerm + alignTerm + jumpTerm;

            snapshot = new DetectionQualitySnapshot(
                score,
                sizeFit,
                output.Method,
                output.DebugReport?.DepthRaycastHit ?? false,
                stageAlignErrorPx,
                centerJumpM);
            return true;
        }

        public static bool IsBetterThan(
            in DetectionQualitySnapshot candidate,
            in DetectionQualitySnapshot incumbent,
            float minRelativeImprovement = 0.05f)
        {
            return ObjectronChairSizeFit.IsBetterFit(candidate.Score, incumbent.Score, minRelativeImprovement);
        }

        private static float GetMethodPenalty(PlacementMethod method)
        {
            return method switch
            {
                PlacementMethod.ModelOrientedMaskBox => 0f,
                PlacementMethod.DepthRefinedBox => 0.02f,
                PlacementMethod.FloorSnappedBox => 0.03f,
                PlacementMethod.TableSnappedBox => 0.04f,
                PlacementMethod.MaskAlignedBox => 0.06f,
                PlacementMethod.Keypoint3D => 0.08f,
                PlacementMethod.Keypoint2DRaycast => 0.1f,
                PlacementMethod.TranslationBox => 0.12f,
                PlacementMethod.TwoStageLifted3D => 0.14f,
                PlacementMethod.TwoStageCanonical => 0.16f,
                _ => 0.2f,
            };
        }
    }
}
