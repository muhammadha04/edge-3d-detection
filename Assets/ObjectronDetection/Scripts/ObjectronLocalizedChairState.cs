// Runtime-only chair track state (not Unity-serialized).

using Mediapipe;
using UnityEngine;

namespace QuestObjectron
{
    internal sealed class ObjectronLocalizedChairState
    {
        public int ObjectId;
        public PlacementMethod Method;
        public Vector3[] Corners;
        public ObjectAnnotation Annotation;
        public ObjectronPlacementDebugReport? DebugReport;
        public float SizeFitScore = float.MaxValue;
        public Vector3 DetectedExtentsSortedM;
    }
}
