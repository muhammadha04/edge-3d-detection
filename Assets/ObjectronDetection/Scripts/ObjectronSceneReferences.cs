// Auto-wires Meta sample prefab references when inspector fields are left empty.

using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    [DefaultExecutionOrder(-50)]
    public class ObjectronSceneReferences : MonoBehaviour
    {
        [SerializeField] private ObjectronCupDetectionManager m_manager;

        private void Awake()
        {
            if (m_manager == null)
            {
                m_manager = GetComponent<ObjectronCupDetectionManager>();
            }

            if (m_manager == null)
            {
                return;
            }

            var camera = FindAnyObjectByType<PassthroughCameraAccess>();
            var raycast = FindAnyObjectByType<EnvironmentRayCastSampleManager>();
            var bootstrap = FindAnyObjectByType<Bootstrap>();
            var graph = GetComponent<ObjectronGraph>() ?? FindAnyObjectByType<ObjectronGraph>();
            var imageSource = GetComponent<PassthroughImageSource>() ?? gameObject.AddComponent<PassthroughImageSource>();
            var pool = GetComponent<TextureFramePool>() ?? gameObject.AddComponent<TextureFramePool>();
            var drawer = FindAnyObjectByType<OrientedBBoxDrawer>();
            if (drawer != null && FindAnyObjectByType<ObjectronDetectionDebug>() == null)
            {
                drawer.gameObject.AddComponent<ObjectronDetectionDebug>();
            }

            var questVisuals = m_manager.GetComponent<ObjectronQuestVisuals>()
                ?? FindAnyObjectByType<ObjectronQuestVisuals>();
            if (questVisuals == null)
            {
                questVisuals = m_manager.gameObject.AddComponent<ObjectronQuestVisuals>();
            }

            questVisuals.Prewarm();
            drawer?.Prewarm();

            if (m_manager.GetComponent<ObjectronHeadsetHud>() == null)
            {
                m_manager.gameObject.AddComponent<ObjectronHeadsetHud>();
            }

            if (m_manager.GetComponent<ObjectronPassthroughOverlay>() == null)
            {
                m_manager.gameObject.AddComponent<ObjectronPassthroughOverlay>();
            }

            var depthProvider = m_manager.GetComponent<ObjectronEnvironmentDepthProvider>()
                ?? m_manager.gameObject.AddComponent<ObjectronEnvironmentDepthProvider>();
            var overlay = m_manager.GetComponent<ObjectronPassthroughOverlay>();
            overlay?.BindDepth(depthProvider);

            m_manager.WireReferences(camera, imageSource, graph, bootstrap, pool, raycast, drawer);
        }
    }
}
