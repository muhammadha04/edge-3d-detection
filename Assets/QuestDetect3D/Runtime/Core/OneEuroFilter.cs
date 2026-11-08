// One-Euro filter (Casiez et al., CHI 2012): adaptive low-pass that is very smooth when the signal is
// still and responsive when it moves fast. Used for displayed box pose/size and labels.
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class OneEuroFilter
    {
        private readonly float m_minCutoff;
        private readonly float m_beta;
        private readonly float m_derivativeCutoff;
        private float m_x;
        private float m_dx;
        private float m_lastTime;
        private bool m_initialised;

        public OneEuroFilter(float minCutoffHz, float beta, float derivativeCutoffHz = 1f)
        {
            m_minCutoff = minCutoffHz;
            m_beta = beta;
            m_derivativeCutoff = derivativeCutoffHz;
        }

        public float Value => m_x;

        public void Reset(float value, float time)
        {
            m_x = value;
            m_dx = 0f;
            m_lastTime = time;
            m_initialised = true;
        }

        public float Filter(float value, float time)
        {
            if (!m_initialised)
            {
                Reset(value, time);
                return value;
            }

            var dt = Mathf.Max(1e-4f, time - m_lastTime);
            m_lastTime = time;
            var dxRaw = (value - m_x) / dt;
            m_dx = Mathf.Lerp(m_dx, dxRaw, Alpha(m_derivativeCutoff, dt));
            var cutoff = m_minCutoff + m_beta * Mathf.Abs(m_dx);
            m_x = Mathf.Lerp(m_x, value, Alpha(cutoff, dt));
            return m_x;
        }

        private static float Alpha(float cutoffHz, float dt)
        {
            var tau = 1f / (2f * Mathf.PI * cutoffHz);
            return 1f / (1f + tau / dt);
        }
    }

    public sealed class OneEuroFilter3
    {
        private readonly OneEuroFilter m_x;
        private readonly OneEuroFilter m_y;
        private readonly OneEuroFilter m_z;

        public OneEuroFilter3(float minCutoffHz, float beta, float derivativeCutoffHz = 1f)
        {
            m_x = new OneEuroFilter(minCutoffHz, beta, derivativeCutoffHz);
            m_y = new OneEuroFilter(minCutoffHz, beta, derivativeCutoffHz);
            m_z = new OneEuroFilter(minCutoffHz, beta, derivativeCutoffHz);
        }

        public void Reset(Vector3 value, float time)
        {
            m_x.Reset(value.x, time);
            m_y.Reset(value.y, time);
            m_z.Reset(value.z, time);
        }

        public Vector3 Filter(Vector3 value, float time) =>
            new Vector3(m_x.Filter(value.x, time), m_y.Filter(value.y, time), m_z.Filter(value.z, time));
    }
}
