// Finds which side of a fitted chair box the backrest is on, from the Depth API — independent of how the
// network labelled the box axes.
//
// Idea: the top ~20% of a chair's height is occupied only by the backrest (armrests and seat are lower). We
// cast depth rays from the head at random points inside that top slab and keep the hits that land inside the
// slab. Their mean position in box-local coordinates points at the backrest, whatever side we look from
// (from the front the rays pass over the seat and stop on the backrest; from behind they hit its back).
// Votes for the four box sides (-Z, -X, +Z, +X) accumulate over frames; a side is accepted once it clearly
// dominates and is only replaced by a clearly stronger one.
using UnityEngine;
using Meta.XR;

namespace QuestDetect3D
{
    public sealed class BackrestEstimator
    {
        private const float SlabLow = 0.80f;   // fraction of box height
        private const float SlabHigh = 1.10f;
        private const float SampleLow = 0.83f;
        private const float SampleHigh = 0.97f;
        private const float FootprintMargin = 0.06f; // m
        private const int MinHits = 24;
        private const float DecideRatio = 1.35f;
        private const float SwitchRatio = 1.8f;
        private const float Decay = 0.995f;          // per probe frame, so old evidence fades slowly

        // Side index = quarter turns to apply to the canonical scan (backrest at -Z):
        //   0 → back at -Z, 1 → back at -X, 2 → back at +Z, 3 → back at +X.
        private readonly float[] m_score = new float[4];
        private float m_hits;
        private int m_totalRays;

        public bool Decided { get; private set; }
        public int QuarterTurns { get; private set; }
        public float Ratio { get; private set; }
        public int Hits => Mathf.RoundToInt(m_hits);

        public void Reset()
        {
            for (var i = 0; i < 4; i++)
            {
                m_score[i] = 0f;
            }

            m_hits = 0f;
            m_totalRays = 0;
            Decided = false;
            QuarterTurns = 0;
            Ratio = 0f;
        }

        /// <returns>True when the decision changed (became decided or switched side).</returns>
        public bool Probe(in UprightBox box, Vector3 eye, EnvironmentRaycastManager raycast, int rays)
        {
            if (raycast == null)
            {
                return false;
            }

            var rotation = box.Rotation;
            var inverse = Quaternion.Inverse(rotation);
            var halfX = 0.5f * box.Size.x;
            var halfZ = 0.5f * box.Size.z;
            var h = box.Size.y;

            for (var i = 0; i < 4; i++)
            {
                m_score[i] *= Decay;
            }

            m_hits *= Decay;

            for (var r = 0; r < rays; r++)
            {
                m_totalRays++;
                var local = new Vector3(
                    Random.Range(-0.8f, 0.8f) * halfX,
                    Random.Range(SampleLow, SampleHigh) * h,
                    Random.Range(-0.8f, 0.8f) * halfZ);
                var target = box.BottomCenter + rotation * local;
                var dir = target - eye;
                var dist = dir.magnitude;
                if (dist < 0.2f)
                {
                    continue;
                }

                if (!raycast.Raycast(new Ray(eye, dir / dist), out var hit, dist + Mathf.Max(halfX, halfZ) * 2f + 0.3f))
                {
                    continue;
                }

                var p = inverse * (hit.point - box.BottomCenter);
                if (p.y < SlabLow * h || p.y > SlabHigh * h ||
                    Mathf.Abs(p.x) > halfX + FootprintMargin || Mathf.Abs(p.z) > halfZ + FootprintMargin)
                {
                    continue;
                }

                var nx = Mathf.Clamp(p.x / Mathf.Max(halfX, 0.05f), -1.2f, 1.2f);
                var nz = Mathf.Clamp(p.z / Mathf.Max(halfZ, 0.05f), -1.2f, 1.2f);
                m_score[0] += Mathf.Max(0f, -nz);
                m_score[1] += Mathf.Max(0f, -nx);
                m_score[2] += Mathf.Max(0f, nz);
                m_score[3] += Mathf.Max(0f, nx);
                m_hits += 1f;
            }

            if (m_hits < MinHits)
            {
                return false;
            }

            var best = 0;
            for (var i = 1; i < 4; i++)
            {
                if (m_score[i] > m_score[best])
                {
                    best = i;
                }
            }

            var second = 0f;
            for (var i = 0; i < 4; i++)
            {
                if (i != best)
                {
                    second = Mathf.Max(second, m_score[i]);
                }
            }

            Ratio = m_score[best] / Mathf.Max(second, 1e-3f);
            if (!Decided)
            {
                if (Ratio >= DecideRatio)
                {
                    Decided = true;
                    QuarterTurns = best;
                    return true;
                }

                return false;
            }

            if (best != QuarterTurns && m_score[best] >= SwitchRatio * Mathf.Max(m_score[QuarterTurns], 1e-3f))
            {
                QuarterTurns = best;
                return true;
            }

            return false;
        }

        public static string SideName(int quarterTurns)
        {
            switch (quarterTurns & 3)
            {
                case 0: return "-Z(back of model frame)";
                case 1: return "-X";
                case 2: return "+Z(model front)";
                default: return "+X";
            }
        }

        public override string ToString()
        {
            return $"hits={Hits} rays={m_totalRays} scores=[{m_score[0]:F1},{m_score[1]:F1},{m_score[2]:F1},{m_score[3]:F1}] " +
                   $"ratio={Ratio:F2} decided={Decided} side={SideName(QuarterTurns)}";
        }
    }
}
