// Runtime migration when Objectron2DDebug.unity was copied from Box Debug but not reconfigured in the editor.

using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestObjectron
{
    public static class Objectron2DDebugSceneBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Ensure2DDebugPipeline()
        {
            var scene = SceneManager.GetActiveScene();
            if (!Is2DDebugScene(scene))
            {
                return;
            }

            var legacy = Object.FindAnyObjectByType<ObjectronBoxDebugManager>(FindObjectsInactive.Include);
            if (legacy == null)
            {
                return;
            }

            var root = legacy.gameObject;
            legacy.enabled = false;

            if (root.GetComponent<ObjectronBoxDebugUi>() is { } legacyUi)
            {
                legacyUi.enabled = false;
            }

            if (root.GetComponent<ObjectronLabeledBoxVisuals>() is { } labeled)
            {
                labeled.enabled = false;
            }

            var worldBoxes = root.transform.Find("WorldBoundingBoxes");
            if (worldBoxes != null)
            {
                worldBoxes.gameObject.SetActive(false);
            }

            if (root.GetComponent<Objectron2DDebugManager>() == null)
            {
                root.AddComponent<Objectron2DDebugManager>();
            }

            if (root.GetComponent<ObjectronPassthroughOverlay>() is { } passthroughOverlay)
            {
                passthroughOverlay.enabled = false;
            }

            Object.Destroy(legacy);
            QuestObjectronLogger.Boot("2d_debug auto-migrated from BoxDebugManager");
        }

        private static bool Is2DDebugScene(Scene scene) =>
            scene.path.Contains("Objectron2DDebug") || scene.name.Contains("Objectron2DDebug");
    }
}
