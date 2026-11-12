// Depth queries used by the geometry code. On the headset this is the Meta Depth API
// (EnvironmentDepthRaycaster); in tests it is an analytic synthetic room. Keeping geometry code behind this
// interface is what lets the whole 3D pipeline be debugged without a Quest.
using UnityEngine;

namespace QuestDetect3D
{
    public interface IDepthRaycaster
    {
        bool IsAvailable { get; }

        /// <summary>First environment surface hit along <paramref name="ray"/> (direction need not be unit).</summary>
        bool Raycast(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal);
    }
}
