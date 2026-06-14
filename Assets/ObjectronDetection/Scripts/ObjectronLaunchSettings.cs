// Start-menu tunables passed into Objectron detection scenes.

using Mediapipe.Unity.Objectron;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronLaunchSettings
    {
        public const int MinMaxObjects = 1;
        public const int MaxMaxObjects = 20;

        public static int MaxObjects { get; set; } = 3;
        public static float MinDetectionConfidence { get; set; } = 0.5f;
        /// <summary>0.99 (MediaPipe default) forces frequent slow stage-1 SSD re-runs on Quest; 0.55 tracks better.</summary>
        public static float MinTrackingConfidence { get; set; } = 0.55f;

        public static int ClampMaxObjects(int value) =>
            Mathf.Clamp(value, MinMaxObjects, MaxMaxObjects);

        public static void ApplyToGraph(ObjectronGraph graph)
        {
            if (graph == null)
            {
                return;
            }

            graph.maxNumObjects = ClampMaxObjects(MaxObjects);
            graph.minDetectionConfidence = MinDetectionConfidence;
            graph.minTrackingConfidence = MinTrackingConfidence;
        }
    }
}
