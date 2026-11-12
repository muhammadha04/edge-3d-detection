// Pure post-processing of the YOLOv9 graph used by Meta's MultiObjectDetection sample
// (yolov9sentis.sentis = YOLOv9-t + a box-corner / argmax tail, uint8 weights):
//   output 0: boxes   (N, 4)  x1, y1, x2, y2 in input pixels, origin TOP-LEFT (TextureConverter default)
//   output 1: classes (N)     argmax over the 80 COCO classes
//   output 2: scores  (N)     max class probability (already sigmoid)
// N = 8400 anchors for a 640×640 input. Verified offline against onnxruntime on real chair photos.
//
// We keep only classes that map to an ObjectCategory, then run greedy NMS within each CategoryGroup
// (a chair in front of a table must not suppress the table).
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class Detection2D
    {
        public int ClassId;
        public string Label;
        public ObjectCategory Category;
        public float Score;
        /// <summary>Box in viewport coordinates of the camera image (origin bottom-left, 0..1).</summary>
        public Rect Viewport;

        public override string ToString() =>
            $"{Label}({Category}) {Score:F2} vp=[{Viewport.xMin:F2},{Viewport.yMin:F2},{Viewport.xMax:F2},{Viewport.yMax:F2}]";
    }

    public static class YoloDecode
    {
        public const float DefaultScoreThreshold = 0.25f;
        public const float DefaultIouThreshold = 0.55f;
        public const int MaxDetections = 12;

        private static readonly List<int> s_candidates = new List<int>(256);

        /// <param name="boxes">N×4 row-major corners in input pixels (top-left origin).</param>
        public static void Decode(
            float[] boxes,
            int[] classIds,
            float[] scores,
            int count,
            Vector2Int inputSize,
            IReadOnlyList<string> labels,
            float scoreThreshold,
            float iouThreshold,
            List<Detection2D> output)
        {
            output.Clear();
            s_candidates.Clear();
            for (var i = 0; i < count; i++)
            {
                if (scores[i] < scoreThreshold)
                {
                    continue;
                }

                var cls = classIds[i];
                if (cls < 0 || labels == null || cls >= labels.Count || !Categories.TryFromCocoLabel(labels[cls], out _))
                {
                    continue;
                }

                s_candidates.Add(i);
            }

            s_candidates.Sort((a, b) => scores[b].CompareTo(scores[a]));
            var suppressed = new bool[s_candidates.Count];
            for (var a = 0; a < s_candidates.Count && output.Count < MaxDetections; a++)
            {
                if (suppressed[a])
                {
                    continue;
                }

                var i = s_candidates[a];
                Categories.TryFromCocoLabel(labels[classIds[i]], out var category);
                var group = Categories.Get(category).Group;
                var det = new Detection2D
                {
                    ClassId = classIds[i],
                    Label = labels[classIds[i]],
                    Category = category,
                    Score = scores[i],
                    Viewport = ToViewport(boxes, i, inputSize),
                };
                if (det.Viewport.width < 0.02f || det.Viewport.height < 0.02f)
                {
                    continue;
                }

                output.Add(det);
                for (var b = a + 1; b < s_candidates.Count; b++)
                {
                    if (suppressed[b])
                    {
                        continue;
                    }

                    var j = s_candidates[b];
                    Categories.TryFromCocoLabel(labels[classIds[j]], out var otherCategory);
                    if (Categories.Get(otherCategory).Group != group)
                    {
                        continue;
                    }

                    if (IoU(boxes, i, j) > iouThreshold)
                    {
                        suppressed[b] = true;
                    }
                }
            }
        }

        /// <summary>Input-pixel corners (top-left origin) → clamped viewport rect (bottom-left origin).</summary>
        public static Rect ToViewport(float[] boxes, int i, Vector2Int inputSize)
        {
            var x1 = Mathf.Clamp01(boxes[4 * i] / inputSize.x);
            var y1 = Mathf.Clamp01(boxes[4 * i + 1] / inputSize.y);
            var x2 = Mathf.Clamp01(boxes[4 * i + 2] / inputSize.x);
            var y2 = Mathf.Clamp01(boxes[4 * i + 3] / inputSize.y);
            // Top-left image rows → bottom-left viewport: v = 1 - y.
            return Rect.MinMaxRect(Mathf.Min(x1, x2), 1f - Mathf.Max(y1, y2), Mathf.Max(x1, x2), 1f - Mathf.Min(y1, y2));
        }

        public static float IoU(float[] boxes, int a, int b)
        {
            var ax1 = boxes[4 * a];
            var ay1 = boxes[4 * a + 1];
            var ax2 = boxes[4 * a + 2];
            var ay2 = boxes[4 * a + 3];
            var bx1 = boxes[4 * b];
            var by1 = boxes[4 * b + 1];
            var bx2 = boxes[4 * b + 2];
            var by2 = boxes[4 * b + 3];
            var iw = Mathf.Max(0f, Mathf.Min(ax2, bx2) - Mathf.Max(ax1, bx1));
            var ih = Mathf.Max(0f, Mathf.Min(ay2, by2) - Mathf.Max(ay1, by1));
            var inter = iw * ih;
            var union = (ax2 - ax1) * (ay2 - ay1) + (bx2 - bx1) * (by2 - by1) - inter;
            return union > 0f ? inter / union : 0f;
        }
    }
}
