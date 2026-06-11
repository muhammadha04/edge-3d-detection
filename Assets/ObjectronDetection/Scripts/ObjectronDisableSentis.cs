// Disables Meta YOLO/Sentis sample UI and inference so Objectron starts immediately.

using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    [DefaultExecutionOrder(-200)]
    public class ObjectronDisableSentis : MonoBehaviour
    {
        private void Awake()
        {
            StripSentisInference();
            StripMetaSampleMenus();
        }

        private static void StripSentisInference()
        {
            foreach (var sentis in FindObjectsByType<SentisInferenceRunManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sentis.enabled = false;
            }

            foreach (var ui in FindObjectsByType<SentisInferenceUiManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                ui.enabled = false;
            }

            QuestObjectronLogger.Boot("disabled_sentis_inference");
        }

        private static void StripMetaSampleMenus()
        {
            foreach (var writer in FindObjectsByType<DetectionUiTextWritter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                writer.enabled = false;
            }

            foreach (var blink in FindObjectsByType<DetectionUiBlinkText>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                blink.enabled = false;
            }

            foreach (var menu in FindObjectsByType<DetectionUiMenuManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                menu.enabled = false;
                menu.gameObject.SetActive(false);
            }

            foreach (var detection in FindObjectsByType<DetectionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                detection.enabled = false;
            }

            foreach (var root in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var name = root.name;
                if (name == "DetectionUiMenuPrefab" || name.Contains("SentisInference"))
                {
                    root.gameObject.SetActive(false);
                }
            }

            QuestObjectronLogger.Boot("stripped_meta_sample_ui");
        }
    }
}
