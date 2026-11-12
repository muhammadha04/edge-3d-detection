// One physical object hypothesis, fused over many single-frame box fits.
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D
{
    public enum TrackState
    {
        Tentative,
        Confirmed,
    }

    public struct BoxObservation
    {
        public UprightBox Box;
        public float Quality;
        public QualityBreakdown Breakdown;
        public float Time;
        public Vector3 ViewDirection; // horizontal unit vector, camera → object
        public float RmsPx;
        public float SupportSigma;
        public int FrameId;
        /// <summary>Detected category of this frame (tracks vote over these).</summary>
        public ObjectCategory Category;
        /// <summary>Yaw is defined modulo this period (360° keypoint fits, 90° depth-box fits).</summary>
        public float YawPeriodDeg;
        /// <summary>Detector score in [0,1].</summary>
        public float Score;
        /// <summary>Single-frame position uncertainty (m) reported by the box fitter.</summary>
        public float SigmaPos;
    }

    public sealed class TrackedObject
    {
        private const int MaxObservations = 40;
        private const float TimeDecaySec = 30f;
        private const float YawInlierDeg = 35f;

        private static int s_nextId = 1;

        public readonly int Id;
        /// <summary>Current label: weighted vote over all observations in the track's group.</summary>
        public ObjectCategory Category { get; private set; }
        public readonly CategoryGroup Group;
        /// <summary>Yaw period (radians) of this track's detector: 2π for keypoint fits, π/2 for depth boxes.</summary>
        public float YawPeriod = 2f * Mathf.PI;
        private readonly float[] m_votes = new float[32];
        public readonly List<BoxObservation> Observations = new List<BoxObservation>(MaxObservations);

        public TrackState State = TrackState.Tentative;
        public UprightBox Estimate;
        public float Confidence;
        public ConfidenceBreakdown ConfidenceDetail;
        public float PositionSigma;
        public float YawSigmaDeg;
        public float SizeSigmaRel;
        public float UncertaintyCm = 99f;
        public float InlierFraction = 1f;
        public float ViewSpreadDeg;
        public int Hits;
        public float Misses;
        public float CreatedTime;
        public float LastSeenTime;
        public float ConfirmedTime = -1f;
        public float LowConfidenceSince = -1f;
        public BoxObservation LastObservation;

        /// <summary>Set when the object was detected somewhere else consistently and the history was reset.</summary>
        public bool Relocated;
        private int m_consecutiveOutliers;
        private float m_firstOutlierTime;

        public TrackedObject(ObjectCategory category, float now, float yawPeriodDeg = 360f)
        {
            Id = s_nextId++;
            Category = category;
            Group = Categories.Get(category).Group;
            YawPeriod = Mathf.Clamp(yawPeriodDeg, 90f, 360f) * Mathf.Deg2Rad;
            CreatedTime = now;
            LastSeenTime = now;
        }

        public static void ResetIds() => s_nextId = 1;

        public float AssociationGate()
        {
            var footprint = Mathf.Max(Estimate.Size.x, Estimate.Size.z);
            return Mathf.Max(0.6f * footprint, 0.07f) + 2f * PositionSigma + 0.05f;
        }

        public void AddObservation(in BoxObservation observation, in CategoryInfo info, float now)
        {
            if (Observations.Count >= MaxObservations)
            {
                // Drop the least useful of the oldest half to keep a diverse history.
                var worst = 0;
                var worstScore = float.MaxValue;
                for (var i = 0; i < Observations.Count / 2; i++)
                {
                    var o = Observations[i];
                    var score = o.Quality * Mathf.Exp(-(now - o.Time) / TimeDecaySec);
                    if (score < worstScore)
                    {
                        worstScore = score;
                        worst = i;
                    }
                }

                Observations.RemoveAt(worst);
            }

            // Moved-object detection: several consecutive views that disagree with the fused estimate
            // (but still fall inside the association gate) mean the object was moved → drop the old history.
            if (Observations.Count >= 4)
            {
                var d = HorizontalDistance(observation.Box.BottomCenter, Estimate.BottomCenter);
                if (d > Mathf.Max(0.12f, 3f * PositionSigma))
                {
                    if (m_consecutiveOutliers == 0)
                    {
                        m_firstOutlierTime = observation.Time;
                    }

                    m_consecutiveOutliers++;
                }
                else
                {
                    m_consecutiveOutliers = 0;
                }

                if (m_consecutiveOutliers >= 6)
                {
                    var cutoff = m_firstOutlierTime;
                    var removed = Observations.RemoveAll(o => o.Time < cutoff);
                    m_consecutiveOutliers = 0;
                    Relocated = true;
                    Misses = 0f;
                    Log.Info($"track_relocated id={Id} dropped={removed} kept={Observations.Count}");
                }
            }

            Observations.Add(observation);
            Vote(observation);
            LastObservation = observation;
            LastSeenTime = now;
            Hits++;
            Misses = Mathf.Max(0f, Misses * 0.7f - 0.5f);
            Recompute(info, now);
        }

        private void Vote(in BoxObservation o)
        {
            var c = (int)o.Category;
            if (c < 0 || c >= m_votes.Length)
            {
                return;
            }

            m_votes[c] += Mathf.Max(0.05f, o.Quality) * Mathf.Max(0.1f, o.Score);
            var best = (int)Category;
            for (var i = 0; i < m_votes.Length; i++)
            {
                if (m_votes[i] > m_votes[best] * 1.15f)
                {
                    best = i; // 15 % hysteresis so the label doesn't flicker between chair/bench
                }
            }

            Category = (ObjectCategory)best;
        }

        public void RegisterMiss(in CategoryInfo info, float now)
        {
            Misses += 1f;
            Recompute(info, now);
        }

        public void MergeFrom(TrackedObject other, in CategoryInfo info, float now)
        {
            foreach (var o in other.Observations)
            {
                if (Observations.Count < MaxObservations)
                {
                    Observations.Add(o);
                    Vote(o);
                }
            }

            Hits += other.Hits;
            LastSeenTime = Mathf.Max(LastSeenTime, other.LastSeenTime);
            Recompute(info, now);
        }

        public void Recompute(in CategoryInfo info, float now)
        {
            var n = Observations.Count;
            if (n == 0)
            {
                Confidence = 0f;
                return;
            }

            var period = Mathf.Min(YawPeriod, info.YawSymmetric180 ? Mathf.PI : 2f * Mathf.PI);
            var weights = new float[n];
            var sumW = 0f;
            for (var i = 0; i < n; i++)
            {
                var o = Observations[i];
                weights[i] = Mathf.Max(1e-4f, o.Quality) * Mathf.Exp(-(now - o.Time) / TimeDecaySec);
                sumW += weights[i];
            }

            // 1) robust centre: weighted mean, then re-estimate with inliers.
            var mean = WeightedMean(weights, null);
            var dists = new float[n];
            for (var i = 0; i < n; i++)
            {
                dists[i] = HorizontalDistance(Observations[i].Box.BottomCenter, mean);
            }

            var sortedD = (float[])dists.Clone();
            System.Array.Sort(sortedD);
            var mad = sortedD[n / 2];
            var posGate = Mathf.Max(0.10f, 2.5f * 1.48f * mad);
            var inlier = new bool[n];
            for (var i = 0; i < n; i++)
            {
                inlier[i] = dists[i] <= posGate;
            }

            mean = WeightedMean(weights, inlier);

            // 2) yaw (circular mean with category symmetry), reject yaw outliers.
            var meanYaw = CircularMean(weights, inlier, period, out _);
            for (var i = 0; i < n; i++)
            {
                if (inlier[i] && YawDistanceDeg(Observations[i].Box.Yaw, meanYaw, period) > YawInlierDeg)
                {
                    inlier[i] = false;
                }
            }

            meanYaw = CircularMean(weights, inlier, period, out var resultant);

            // 3) size and spreads over inliers.
            var sumIn = 0f;
            var size = Vector3.zero;
            var pos = Vector3.zero;
            var qualityW = 0f;
            var supportSigma = 0f;
            var obsSigma = 0f;
            for (var i = 0; i < n; i++)
            {
                if (!inlier[i])
                {
                    continue;
                }

                var w = weights[i];
                sumIn += w;
                size += AlignedSize(Observations[i].Box, meanYaw, period) * w;
                pos += Observations[i].Box.BottomCenter * w;
                qualityW += Observations[i].Quality * w;
                supportSigma += Observations[i].SupportSigma * w;
                obsSigma += Observations[i].SigmaPos * w;
            }

            if (sumIn <= 0f)
            {
                // Nothing agrees: fall back to the latest observation, very low confidence.
                Estimate = Observations[n - 1].Box;
                Confidence = 0.05f;
                return;
            }

            size /= sumIn;
            pos /= sumIn;
            var meanQuality = qualityW / sumIn;
            supportSigma /= sumIn;
            obsSigma /= sumIn;

            var varPos = 0f;
            var varSize = 0f;
            var sumW2 = 0f;
            for (var i = 0; i < n; i++)
            {
                if (!inlier[i])
                {
                    continue;
                }

                var w = weights[i];
                sumW2 += w * w;
                var d = Observations[i].Box.BottomCenter - pos;
                varPos += w * d.sqrMagnitude;
                var ds = AlignedSize(Observations[i].Box, meanYaw, period) - size;
                varSize += w * ds.sqrMagnitude;
            }

            var nEff = sumW2 > 0f ? sumIn * sumIn / sumW2 : 1f;
            PositionSigma = Mathf.Sqrt(varPos / sumIn);
            SizeSigmaRel = Mathf.Sqrt(varSize / sumIn) / Mathf.Max(0.05f, size.magnitude);
            YawSigmaDeg = resultant >= 0.999f
                ? 0f
                : Mathf.Sqrt(-2f * Mathf.Log(Mathf.Max(resultant, 1e-4f))) * period / (2f * Mathf.PI) * Mathf.Rad2Deg;
            InlierFraction = sumIn / sumW;
            ViewSpreadDeg = ViewSpread(inlier);

            Estimate = new UprightBox(pos, meanYaw, size);

            // Expected metric error of the fused centre: statistical part + floor-height part
            // (a support-height error slides the box along the viewing ray by ≈ the same order).
            var stat = PositionSigma / Mathf.Sqrt(Mathf.Max(1f, nEff));
            var single = obsSigma / Mathf.Sqrt(Mathf.Max(1f, nEff));
            UncertaintyCm = 100f * Mathf.Sqrt(stat * stat + single * single + 2f * supportSigma * supportSigma + 0.008f * 0.008f);

            ConfidenceDetail = ConfidenceModel.TrackConfidence(
                sumIn, meanQuality, PositionSigma, YawSigmaDeg, SizeSigmaRel, InlierFraction, ViewSpreadDeg, Misses, nEff);
            Confidence = ConfidenceDetail.Total;
        }

        private Vector3 WeightedMean(float[] weights, bool[] mask)
        {
            var sum = Vector3.zero;
            var sw = 0f;
            for (var i = 0; i < Observations.Count; i++)
            {
                if (mask != null && !mask[i])
                {
                    continue;
                }

                sum += Observations[i].Box.BottomCenter * weights[i];
                sw += weights[i];
            }

            return sw > 0f ? sum / sw : Observations[Observations.Count - 1].Box.BottomCenter;
        }

        private float CircularMean(float[] weights, bool[] mask, float period, out float resultant)
        {
            var s = 0f;
            var c = 0f;
            var sw = 0f;
            var k = 2f * Mathf.PI / period;
            for (var i = 0; i < Observations.Count; i++)
            {
                if (mask != null && !mask[i])
                {
                    continue;
                }

                var a = Observations[i].Box.Yaw * k;
                s += Mathf.Sin(a) * weights[i];
                c += Mathf.Cos(a) * weights[i];
                sw += weights[i];
            }

            if (sw <= 0f)
            {
                resultant = 0f;
                return Observations[Observations.Count - 1].Box.Yaw;
            }

            resultant = Mathf.Sqrt(s * s + c * c) / sw;
            return Mathf.Atan2(s, c) / k;
        }

        /// <summary>
        /// Size expressed in the frame of the mean yaw. When yaw is only known modulo 90° (depth boxes), an
        /// observation whose yaw is a quarter turn away from the mean describes the same box with x and z swapped.
        /// </summary>
        public static Vector3 AlignedSize(in UprightBox box, float meanYaw, float period)
        {
            var delta = Mathf.DeltaAngle(meanYaw * Mathf.Rad2Deg, box.Yaw * Mathf.Rad2Deg);
            var quarter = Mathf.RoundToInt(delta / 90f);
            return (quarter & 1) != 0 ? new Vector3(box.Size.z, box.Size.y, box.Size.x) : box.Size;
        }

        private static float YawDistanceDeg(float a, float b, float period)
        {
            var d = Mathf.Abs(a - b) % period;
            d = Mathf.Min(d, period - d);
            return d * Mathf.Rad2Deg;
        }

        private float ViewSpread(bool[] inlier)
        {
            var maxAngle = 0f;
            BoxObservation? first = null;
            for (var i = 0; i < Observations.Count; i++)
            {
                if (!inlier[i])
                {
                    continue;
                }

                if (first == null)
                {
                    first = Observations[i];
                    continue;
                }

                maxAngle = Mathf.Max(maxAngle, Vector3.Angle(first.Value.ViewDirection, Observations[i].ViewDirection));
            }

            return maxAngle;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
