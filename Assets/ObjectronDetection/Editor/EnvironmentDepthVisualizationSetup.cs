#if UNITY_EDITOR
using PassthroughCameraSamples.CameraViewer;
using QuestObjectron;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace QuestObjectron.Editor
{
    public static class EnvironmentDepthVisualizationSetup
    {
        private const string ScenePath = "Assets/ObjectronDetection/Scenes/EnvironmentDepthVisualization.unity";
        private const string MaterialPath = "Assets/ObjectronDetection/EnvironmentDepth/Materials/EnvironmentDepthVisualization.mat";
        private const string ShaderPath = "Assets/ObjectronDetection/EnvironmentDepth/Shaders/EnvironmentDepthVisualization.shader";
        private const string CameraViewerScene =
            "Assets/PassthroughCameraApiSamples/CameraViewer/CameraViewer.unity";
        private const string ReturnPrefabPath =
            "Assets/PassthroughCameraApiSamples/StartScene/Prefabs/ReturnToStartScene.prefab";

        [MenuItem("QuestObjectron/Create Environment Depth Visualization Scene")]
        public static void CreateSceneMenu()
        {
            CreateOrUpdateScene(silent: false);
        }

        [InitializeOnLoadMethod]
        private static void EnsureBuildSettingsEntry()
        {
            EditorApplication.delayCall += () =>
            {
                if (System.IO.File.Exists(ScenePath))
                {
                    AddSceneToBuildSettings(ScenePath);
                }
            };
        }

        public static void CreateOrUpdateScene(bool silent)
        {
            EnsureMaterial();

            if (!System.IO.File.Exists(CameraViewerScene))
            {
                if (!silent)
                {
                    Debug.LogError($"QuestObjectron: base scene not found: {CameraViewerScene}");
                }

                return;
            }

            if (!System.IO.File.Exists(ScenePath))
            {
                if (!AssetDatabase.CopyAsset(CameraViewerScene, ScenePath))
                {
                    if (!silent)
                    {
                        Debug.LogError("QuestObjectron: failed to copy CameraViewer scene.");
                    }

                    return;
                }
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            foreach (var viewer in Object.FindObjectsByType<CameraViewerManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(viewer.gameObject);
            }

            foreach (var raw in Object.FindObjectsByType<UnityEngine.UI.RawImage>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (raw.gameObject.name.Contains("Camera") || raw.gameObject.name.Contains("Preview"))
                {
                    Object.DestroyImmediate(raw.transform.root.gameObject);
                }
            }

            var manager = Object.FindFirstObjectByType<EnvironmentDepthVisualizationManager>(
                FindObjectsInactive.Include);
            if (manager == null)
            {
                var root = new GameObject("EnvironmentDepthVisualization");
                manager = root.AddComponent<EnvironmentDepthVisualizationManager>();
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat != null)
            {
                var so = new SerializedObject(manager);
                so.FindProperty("m_depthMaterial").objectReferenceValue = mat;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var returnPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ReturnPrefabPath);
            if (returnPrefab != null
                && Object.FindFirstObjectByType<PassthroughCameraSamples.StartScene.ReturnToStartScene>(
                    FindObjectsInactive.Include) == null)
            {
                PrefabUtility.InstantiatePrefab(returnPrefab);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AddSceneToBuildSettings(ScenePath);

            if (!silent)
            {
                Debug.Log($"QuestObjectron: Environment depth scene ready at {ScenePath}. Added to Build Settings.");
            }
        }

        private static void EnsureMaterial()
        {
            var uiShaderPath = "Assets/ObjectronDetection/EnvironmentDepth/Shaders/EnvironmentDepthVisualizationUI.shader";
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(uiShaderPath);
            if (shader == null)
            {
                shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            }

            if (shader == null)
            {
                shader = Shader.Find("QuestObjectron/EnvironmentDepthVisualizationUI");
            }

            if (shader == null)
            {
                shader = Shader.Find("QuestObjectron/EnvironmentDepthVisualization");
            }

            if (shader == null)
            {
                Debug.LogError("QuestObjectron: EnvironmentDepthVisualization shader not found.");
                return;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat != null)
            {
                return;
            }

            System.IO.Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(MaterialPath) ?? string.Empty);
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MaterialPath);
            AssetDatabase.SaveAssets();
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var s in scenes)
            {
                if (s.path == scenePath)
                {
                    return;
                }
            }

            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
#endif
