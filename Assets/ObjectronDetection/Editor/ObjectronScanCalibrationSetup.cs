#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestObjectron.Editor
{
    public static class ObjectronScanCalibrationSetup
    {
        private const string ScenePath = "Assets/ObjectronDetection/Scenes/ObjectronScanCalibration.unity";
        private const string TemplateScenePath = "Assets/ObjectronDetection/Scenes/ObjectronBoxDebug.unity";
        private const string ChairModelPath = "Assets/lab-chair.obj";
        private const string ResourcesFolder = "Assets/Resources/ScanCalibration";
        private const string ResourcesPrefabPath = "Assets/Resources/ScanCalibration/LabChair.prefab";

        [InitializeOnLoadMethod]
        private static void AutoEnsureLabChairResources()
        {
            EditorApplication.delayCall += () =>
            {
                if (!File.Exists(ChairModelPath))
                {
                    return;
                }

                if (!File.Exists(ResourcesPrefabPath) && !File.Exists($"{ResourcesFolder}/LabChair.obj"))
                {
                    EnsureLabChairPrefab();
                }
            };
        }

        [MenuItem("QuestObjectron/Ensure Lab Chair Resources")]
        public static void EnsureLabChairResourcesMenu()
        {
            EnsureLabChairPrefab();
            AssetDatabase.Refresh();
            Debug.Log($"Lab chair resources ready. Prefab: {ResourcesPrefabPath}");
        }

        [MenuItem("QuestObjectron/Create Scan Calibration Scene")]
        public static void CreateScanCalibrationScene()
        {
            EnsureLabChairPrefab();
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

            ConfigureScene();
            AddSceneToBuildSettings(ScenePath);
            Debug.Log($"Scan Calibration scene ready at {ScenePath}. Build to Quest and use main menu entry.");
        }

        [MenuItem("QuestObjectron/Add Scan Calibration Scene To Build Settings")]
        public static void AddScanCalibrationToBuildSettingsMenu()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"Scene not found: {ScenePath}. Run QuestObjectron/Create Scan Calibration Scene first.");
                return;
            }

            AddSceneToBuildSettings(ScenePath);
        }

        private static void ConfigureScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            GameObject root = null;
            foreach (var go in roots)
            {
                if (go.name.Contains("ObjectronBoxDebug") || go.name.Contains("ObjectronScanCalibration"))
                {
                    root = go;
                    break;
                }
            }

            if (root == null)
            {
                Debug.LogError("Scan Calibration setup: could not find Objectron root in scene.");
                return;
            }

            root.name = "ObjectronScanCalibration";

            var boxManager = root.GetComponent<ObjectronBoxDebugManager>();
            if (boxManager != null)
            {
                Object.DestroyImmediate(boxManager);
            }

            var boxUi = root.GetComponent<ObjectronBoxDebugUi>();
            if (boxUi != null)
            {
                Object.DestroyImmediate(boxUi);
            }

            var scanManager = root.GetComponent<ObjectronScanCalibrationManager>();
            if (scanManager == null)
            {
                scanManager = root.AddComponent<ObjectronScanCalibrationManager>();
            }

            var manipulator = root.GetComponent<ObjectronScanManipulator>();
            if (manipulator == null)
            {
                manipulator = root.AddComponent<ObjectronScanManipulator>();
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ResourcesPrefabPath);
            if (prefab != null)
            {
                var so = new SerializedObject(scanManager);
                so.FindProperty("m_scanModelPrefab").objectReferenceValue = prefab;
                so.ApplyModifiedPropertiesWithoutUndo();

                var manipSo = new SerializedObject(manipulator);
                manipSo.FindProperty("m_scanModelPrefab").objectReferenceValue = prefab;
                manipSo.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static void EnsureLabChairPrefab()
        {
            if (!File.Exists(ChairModelPath))
            {
                Debug.LogError($"Chair model not found: {ChairModelPath}");
                return;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }

            if (!AssetDatabase.IsValidFolder(ResourcesFolder))
            {
                AssetDatabase.CreateFolder("Assets/Resources", "ScanCalibration");
            }

            var resourcesModelPath = $"{ResourcesFolder}/LabChair.obj";
            if (!File.Exists(resourcesModelPath))
            {
                File.Copy(ChairModelPath, resourcesModelPath, overwrite: true);
                AssetDatabase.ImportAsset(resourcesModelPath);
            }

            if (File.Exists(ResourcesPrefabPath))
            {
                return;
            }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(resourcesModelPath)
                ?? AssetDatabase.LoadAssetAtPath<GameObject>(ChairModelPath);
            if (model == null)
            {
                Debug.LogError($"Failed to load chair model: {ChairModelPath}");
                return;
            }

            var instance = PrefabUtility.InstantiatePrefab(model) as GameObject;
            if (instance == null)
            {
                instance = Object.Instantiate(model);
            }

            instance.name = "LabChair";
            PrefabUtility.SaveAsPrefabAsset(instance, ResourcesPrefabPath);
            Object.DestroyImmediate(instance);
            AssetDatabase.SaveAssets();
            Debug.Log($"Created Resources prefab at {ResourcesPrefabPath}");
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            var existing = scenes.FirstOrDefault(s => s.path == scenePath);
            if (existing != null)
            {
                if (!existing.enabled)
                {
                    existing.enabled = true;
                    EditorBuildSettings.scenes = scenes.ToArray();
                    Debug.Log($"Enabled {scenePath} in build settings.");
                }
                else
                {
                    Debug.Log($"Build settings already contains {scenePath}");
                }

                return;
            }

            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log($"Added {scenePath} to build settings.");
        }
    }
}
#endif
