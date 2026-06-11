// Ensures depth manager exists and camera-viewer preview is removed before its Start() runs.
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestObjectron
{
    public static class EnvironmentDepthSceneBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!IsDepthScene(scene))
            {
                return;
            }

            StripCameraPreview();
            EnsureManager();
            QuestObjectronLogger.Boot("depth_scene_loaded strip_preview=done");
        }

        private static bool IsDepthScene(Scene scene) =>
            scene.name.Contains("EnvironmentDepth")
            || scene.path.Contains("EnvironmentDepthVisualization");

        public static void StripCameraPreview()
        {
#if UNITY_2023_1_OR_NEWER
            var viewers = UnityEngine.Object.FindObjectsByType<PassthroughCameraSamples.CameraViewer.CameraViewerManager>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var rawImages = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.RawImage>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var viewers = UnityEngine.Object.FindObjectsOfType<PassthroughCameraSamples.CameraViewer.CameraViewerManager>(true);
            var rawImages = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.RawImage>(true);
#endif
            foreach (var viewer in viewers)
            {
                if (viewer != null)
                {
                    viewer.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(viewer.gameObject);
                }
            }

            foreach (var raw in rawImages)
            {
                if (raw == null)
                {
                    continue;
                }

                var n = raw.gameObject.name;
                if (n.Contains("Camera") || n.Contains("Preview") || n.Contains("Raw") || raw.texture != null)
                {
                    raw.enabled = false;
                    raw.gameObject.SetActive(false);
                }
            }
        }

        private static void EnsureManager()
        {
#if UNITY_2023_1_OR_NEWER
            if (UnityEngine.Object.FindAnyObjectByType<EnvironmentDepthVisualizationManager>() != null)
#else
            if (UnityEngine.Object.FindObjectOfType<EnvironmentDepthVisualizationManager>() != null)
#endif
            {
                return;
            }

            var root = new GameObject("EnvironmentDepthVisualization");
            root.AddComponent<EnvironmentDepthVisualizationManager>();
        }
    }
}
