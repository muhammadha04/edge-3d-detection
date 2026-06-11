#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestObjectron.Editor
{
    public static class ObjectronBoxDebugSetup
    {
        private const string ScenePath = "Assets/ObjectronDetection/Scenes/ObjectronBoxDebug.unity";
        private const string TemplateScenePath = "Assets/ObjectronDetection/Scenes/ObjectronCupDetection.unity";

        [MenuItem("QuestObjectron/Create Box Debug Scene")]
        public static void CreateBoxDebugScene()
        {
            if (!File.Exists(ScenePath))
            {
                if (!File.Exists(TemplateScenePath))
                {
                    Debug.LogError($"Template scene not found: {TemplateScenePath}");
                    return;
                }

                File.Copy(TemplateScenePath, ScenePath);
                AssetDatabase.ImportAsset(ScenePath);
            }

            AddSceneToBuildSettings(ScenePath);
            Debug.Log($"Box Debug scene ready at {ScenePath}. Open it and build to Quest.");
        }

        [MenuItem("QuestObjectron/Add Box Debug Scene To Build Settings")]
        public static void AddBoxDebugToBuildSettingsMenu()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"Scene not found: {ScenePath}. Run QuestObjectron/Create Box Debug Scene first.");
                return;
            }

            AddSceneToBuildSettings(ScenePath);
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == scenePath))
            {
                Debug.Log($"Build settings already contains {scenePath}");
                return;
            }

            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log($"Added {scenePath} to build settings.");
        }
    }
}
#endif
