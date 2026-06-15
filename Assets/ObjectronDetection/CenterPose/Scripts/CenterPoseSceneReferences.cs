using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    [DefaultExecutionOrder(-50)]
    public class CenterPoseSceneReferences : MonoBehaviour
    {
        [SerializeField] private CenterPoseChairDetectionManager m_manager;

        private void Awake()
        {
            if (m_manager == null)
            {
                m_manager = GetComponent<CenterPoseChairDetectionManager>();
            }

            if (m_manager == null)
            {
                return;
            }

            var camera = FindAnyObjectByType<PassthroughCameraAccess>();
            var raycast = FindAnyObjectByType<EnvironmentRayCastSampleManager>();
            var imageSource = GetComponent<PassthroughImageSource>() ?? gameObject.AddComponent<PassthroughImageSource>();
            var questVisuals = m_manager.GetComponent<ObjectronQuestVisuals>()
                ?? FindAnyObjectByType<ObjectronQuestVisuals>();
            if (questVisuals == null)
            {
                questVisuals = m_manager.gameObject.AddComponent<ObjectronQuestVisuals>();
            }

            questVisuals.Prewarm();

            if (m_manager.GetComponent<ObjectronHeadsetHud>() == null)
            {
                m_manager.gameObject.AddComponent<ObjectronHeadsetHud>();
            }

            m_manager.WireReferences(camera, imageSource, raycast, questVisuals);
        }
    }
}
