// IDepthRaycaster backed by MRUK's EnvironmentRaycastManager (Quest Depth API, stereo depth ~30 Hz).
using Meta.XR;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class EnvironmentDepthRaycaster : IDepthRaycaster
    {
        private readonly EnvironmentRaycastManager m_manager;

        public EnvironmentDepthRaycaster(EnvironmentRaycastManager manager)
        {
            m_manager = manager;
        }

        public bool IsAvailable => m_manager != null && EnvironmentRaycastManager.IsSupported;

        public bool Raycast(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.up;
            if (!IsAvailable)
            {
                return false;
            }

            if (!m_manager.Raycast(new Ray(ray.origin, ray.direction.normalized), out var hit, maxDistance))
            {
                return false;
            }

            point = hit.point;
            normal = hit.normal;
            return true;
        }
    }
}
