// Accepted ground truth from k blind repetitions (pure maths, unit-tested; mirrored by repeatability.py).
//   yaw:    every repetition is first aligned to the first one modulo the object's symmetry period
//           (360° none, 180° 2-fold, 90° 4-fold); an odd number of quarter turns swaps width and depth.
//   robust mean = per-coordinate MEDIAN of the aligned repetitions (bottom-centre x/y/z, yaw, w/h/d).
//   gt_sigma: sample standard deviations (n−1) around that mean:
//     centre_cm = sqrt(Σ|c_i − c|² / (n−1)) of the box CENTRE (3D), in cm
//     yaw_deg   = sqrt(Σ Δyaw_i² / (n−1)), in degrees
//     size_pct  = mean over w/h/d of (std_dim / mean_dim) × 100
// With one repetition the sigmas are 0 (unknown), and n = 1 says so.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public struct RobustBox
    {
        public UprightBox Box;
        public float CentreCm;
        public float YawDeg;
        public float SizePct;
        public int N;
    }

    public static class Repeatability
    {
        /// <summary>Symmetry string of the catalog ("none", "2", "4") → yaw period in radians.</summary>
        public static float PeriodFor(string symmetry)
        {
            switch (symmetry)
            {
                case "2": return Mathf.PI;
                case "4": return 0.5f * Mathf.PI;
                default: return 2f * Mathf.PI;
            }
        }

        /// <summary>Box re-expressed with its yaw as close as possible to <paramref name="referenceYaw"/>
        /// (same physical box when the object is symmetric with this period).</summary>
        public static UprightBox AlignYaw(UprightBox box, float referenceYaw, float period)
        {
            var quarter = 0.5f * Mathf.PI;
            var d = RoomFrame.WrapPi(box.Yaw - referenceYaw);
            var k = Mathf.RoundToInt(d / period);
            if (k == 0)
            {
                return box;
            }

            var removed = k * period;
            var quarters = Mathf.RoundToInt(removed / quarter);
            var aligned = box;
            aligned.Yaw = RoomFrame.WrapPi(box.Yaw - removed);
            if ((Math.Abs(quarters) & 1) == 1)
            {
                aligned.Size = new Vector3(box.Size.z, box.Size.y, box.Size.x);
            }

            return aligned;
        }

        /// <summary>Robust mean (per-coordinate median after yaw alignment) and gt_sigma of k repetitions. Use k ≥ 3 blind repetitions.</summary>
        public static RobustBox Compute(IReadOnlyList<UprightBox> boxes, float period)
        {
            var result = new RobustBox { N = boxes.Count };
            if (boxes.Count == 0)
            {
                return result;
            }

            var reference = boxes[0].Yaw;
            var aligned = new List<UprightBox>(boxes.Count);
            var yaws = new List<float>(boxes.Count);
            foreach (var b in boxes)
            {
                var a = AlignYaw(b, reference, period);
                aligned.Add(a);
                yaws.Add(reference + RoomFrame.WrapPi(a.Yaw - reference)); // unwrapped around the reference
            }

            var mean = new UprightBox(
                new Vector3(Median(aligned, b => b.BottomCenter.x), Median(aligned, b => b.BottomCenter.y),
                    Median(aligned, b => b.BottomCenter.z)),
                RoomFrame.WrapPi(MedianOf(yaws)),
                new Vector3(Median(aligned, b => b.Size.x), Median(aligned, b => b.Size.y), Median(aligned, b => b.Size.z)));
            result.Box = mean;
            if (boxes.Count < 2)
            {
                return result;
            }

            var n1 = boxes.Count - 1f;
            double centre = 0, yaw = 0;
            double[] sizeSq = { 0, 0, 0 };
            double[] sizeSum = { 0, 0, 0 };
            var meanYawUnwrapped = reference + RoomFrame.WrapPi(mean.Yaw - reference);
            for (var i = 0; i < aligned.Count; i++)
            {
                var a = aligned[i];
                centre += (a.Center - mean.Center).sqrMagnitude;
                var dy = yaws[i] - meanYawUnwrapped;
                yaw += dy * dy;
                for (var k = 0; k < 3; k++)
                {
                    var s = k == 0 ? a.Size.x : k == 1 ? a.Size.y : a.Size.z;
                    sizeSum[k] += s;
                }
            }

            for (var i = 0; i < aligned.Count; i++)
            {
                var a = aligned[i];
                for (var k = 0; k < 3; k++)
                {
                    var s = k == 0 ? a.Size.x : k == 1 ? a.Size.y : a.Size.z;
                    var m = sizeSum[k] / aligned.Count;
                    sizeSq[k] += (s - m) * (s - m);
                }
            }

            result.CentreCm = (float)(Math.Sqrt(centre / n1) * 100.0);
            result.YawDeg = (float)(Math.Sqrt(yaw / n1) * Mathf.Rad2Deg);
            double pct = 0;
            for (var k = 0; k < 3; k++)
            {
                var m = sizeSum[k] / aligned.Count;
                pct += m > 1e-6 ? Math.Sqrt(sizeSq[k] / n1) / m * 100.0 : 0.0;
            }

            result.SizePct = (float)(pct / 3.0);
            return result;
        }

        /// <summary>Median of one coordinate over boxes.</summary>
        private static float Median(List<UprightBox> boxes, Func<UprightBox, float> f)
        {
            var values = new List<float>(boxes.Count);
            foreach (var b in boxes)
            {
                values.Add(f(b));
            }

            return MedianOf(values);
        }

        /// <summary>Median of a list (mean of the two middle values for even counts).</summary>
        public static float MedianOf(List<float> values)
        {
            var v = new List<float>(values);
            v.Sort();
            var n = v.Count;
            if (n == 0)
            {
                return 0f;
            }

            return (n & 1) == 1 ? v[n / 2] : 0.5f * (v[n / 2 - 1] + v[n / 2]);
        }
    }
}
