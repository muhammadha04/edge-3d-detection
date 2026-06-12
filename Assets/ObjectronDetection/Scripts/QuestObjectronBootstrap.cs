// Ensures MediaPipe Bootstrap runs before Objectron chair detection.

using Mediapipe.Unity;
using UnityEngine;

namespace QuestObjectron
{
    [DefaultExecutionOrder(-100)]
    public class QuestObjectronBootstrap : MonoBehaviour
    {
        [SerializeField] private Bootstrap m_bootstrap;

        private void Awake()
        {
            if (m_bootstrap == null)
            {
                var existing = FindAnyObjectByType<Bootstrap>();
                if (existing != null)
                {
                    m_bootstrap = existing;
                    return;
                }

                var go = new GameObject("Bootstrap");
                m_bootstrap = go.AddComponent<Bootstrap>();
                DontDestroyOnLoad(go);
                QuestObjectronLogger.Boot("created_runtime_bootstrap");
            }
        }
    }
}
