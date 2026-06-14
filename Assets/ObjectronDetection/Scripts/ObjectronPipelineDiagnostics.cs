// Compares raw MediaPipe stage-2 pose vs Quest world placement (for logcat debugging).

using Mediapipe;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronPipelineDiagnostics
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static bool Enabled { get; set; } = true;
#else
        public static bool Enabled { get; set; }
#endif

        public static void LogStageSummary(
            FrameAnnotation lifted,
            int stageOneRectCount,
            float inferenceMs)
        {
            if (!Enabled)
            {
                return;
            }

            var stage2Count = lifted?.Annotations?.Count ?? 0;
            QuestObjectronLogger.Detect(
                $"pipeline stage1_ssd_rects={stageOneRectCount} stage2_lifted={stage2Count} infer_ms={inferenceMs:F0} " +
                "(graph=ObjectronGpuSubgraph; stage2=BoxLandmark+EPnP → lifted_objects)");
        }

        public static void LogPlacementCompare(
            ObjectAnnotation annotation,
            PlacementOutput rawStage2,
            PlacementOutput worldRefined)
        {
            if (!Enabled || annotation == null)
            {
                return;
            }

            var modelScale = ReadScale(annotation);
            var modelT = ReadTranslation(annotation);
            var rawExtent = rawStage2.Corners != null && ObjectronBoxValidation.TryGetExtentMeters(rawStage2.Corners, out var re)
                ? re
                : 0f;
            var refinedExtent = worldRefined.Corners != null
                                && ObjectronBoxValidation.TryGetExtentMeters(worldRefined.Corners, out var fe)
                ? fe
                : 0f;

            var rawCenter = rawStage2.Corners?[0].ToString("F2") ?? "null";
            var refinedCenter = worldRefined.Corners?[0].ToString("F2") ?? "null";

            QuestObjectronLogger.Detect(
                $"pipeline_compare id={annotation.ObjectId} " +
                $"raw_method={rawStage2.Method} refined_method={worldRefined.Method} " +
                $"model_scale=({modelScale.x:F2},{modelScale.y:F2},{modelScale.z:F2}) " +
                $"model_t_cam=({modelT.x:F2},{modelT.y:F2},{modelT.z:F2}) " +
                $"raw_extent={rawExtent:F2}m refined_extent={refinedExtent:F2}m " +
                $"raw_center={rawCenter} refined_center={refinedCenter}");
        }

        private static Vector3 ReadScale(ObjectAnnotation ann)
        {
            if (ann?.Scale != null && ann.Scale.Count >= 3)
            {
                return new Vector3(ann.Scale[0], ann.Scale[1], ann.Scale[2]);
            }

            return Vector3.zero;
        }

        private static Vector3 ReadTranslation(ObjectAnnotation ann)
        {
            if (ann?.Translation != null && ann.Translation.Count >= 3)
            {
                return new Vector3(ann.Translation[0], ann.Translation[1], ann.Translation[2]);
            }

            return Vector3.zero;
        }
    }
}
