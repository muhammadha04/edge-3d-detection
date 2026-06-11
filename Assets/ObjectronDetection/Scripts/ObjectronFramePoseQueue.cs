// Pairs each submitted inference frame with the PCA camera pose at capture time.

using System.Collections.Generic;
using UnityEngine;

namespace QuestObjectron
{
    public sealed class ObjectronFramePoseQueue
    {
        private readonly Queue<Pose> m_poses = new();
        private const int MaxQueued = 8;

        public void Enqueue(Pose pose)
        {
            m_poses.Enqueue(pose);
            while (m_poses.Count > MaxQueued)
            {
                _ = m_poses.Dequeue();
            }
        }

        public Pose DequeueOrCurrent(System.Func<Pose> currentPose)
        {
            return m_poses.Count > 0 ? m_poses.Dequeue() : currentPose();
        }

        public void Clear() => m_poses.Clear();

        public int Count => m_poses.Count;
    }
}
