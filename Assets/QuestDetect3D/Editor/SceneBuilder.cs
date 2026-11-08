#if UNITY_EDITOR
// Builds Assets/QuestDetect3D/Scenes/Detect3D.unity from Meta's known-good MultiObjectDetection sample scene
// (camera rig + passthrough + Passthrough Camera Access + Environment Raycast), strips the YOLO/Sentis sample,
// and adds the MediaPipe bootstrap, the Objectron detector and the QuestDetect3D app.
// Menu: QuestDetect3D → Rebuild Scene. Batch: called by BuildScript.BuildAndroid.
using System.Linq;
using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace QuestDetect3D.EditorTools
{
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/QuestDetect3D/Scenes/Detect3D.unity";
        private const string TemplatePath = "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";
        private const string ShaderPath = "Assets/QuestDetect3D/Shaders/QD3D_UnlitTransparent.shader";
        private const string GraphFolder = "Assets/MediaPipeUnity/Samples/Scenes/Objectron/";

        private static readonly string[] RemoveRootNameFragments =
        {
            "DetectionManager",
            "SentisInference",
            "DetectionUiMenu",
            "ReturnToStartScene",
        };

        [MenuItem("QuestDetect3D/Rebuild Scene")]
        public static void BuildSceneMenu()
        {
            BuildScene();
            EditorUtility.DisplayDialog("QuestDetect3D", $"Scene rebuilt at {ScenePath}", "OK");
        }

        public static void BuildScene()
        {
            EnsureFolder("Assets/QuestDetect3D", "Scenes");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                AssetDatabase.DeleteAsset(ScenePath);
            }

            if (!AssetDatabase.CopyAsset(TemplatePath, ScenePath))
            {
                throw new System.Exception($"QuestDetect3D: could not copy template scene {TemplatePath}");
            }

            AssetDatabase.Refresh();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Remove the YOLO/Sentis sample objects at any depth (some live inside the camera rig).
            var toRemove = scene.GetRootGameObjects()
                .SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                .Where(t => RemoveRootNameFragments.Any(f => t.name.Contains(f)))
                .Select(t => t.gameObject)
                .ToList();
            foreach (var go in toRemove)
            {
                if (go == null)
                {
                    continue; // already destroyed with its parent
                }

                if (go.transform.parent != null)
                {
                    // Nested (e.g. inside the camera-rig prefab): deactivate rather than break the prefab instance.
                    Debug.Log($"QuestDetect3D: deactivating nested sample object '{go.name}'");
                    go.SetActive(false);
                    continue;
                }

                Debug.Log($"QuestDetect3D: removing sample object '{go.name}'");
                Object.DestroyImmediate(go);
            }

            // Belt and braces: disable any remaining sample behaviours by type name.
            foreach (var behaviour in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true)))
            {
                if (behaviour == null)
                {
                    continue;
                }

                var typeName = behaviour.GetType().Name;
                if (typeName == "DetectionUiMenuManager" || typeName == "DetectionManager" ||
                    typeName == "SentisInferenceRunManager" || typeName == "SentisInferenceUiManager" ||
                    typeName == "ReturnToStartScene")
                {
                    Debug.Log($"QuestDetect3D: disabling sample behaviour {typeName} on '{behaviour.name}'");
                    behaviour.enabled = false;
                    behaviour.gameObject.SetActive(false);
                }
            }

            var pca = Object.FindFirstObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            var raycast = Object.FindFirstObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);
            if (pca == null || raycast == null)
            {
                throw new System.Exception("QuestDetect3D: template scene is missing PassthroughCameraAccess or EnvironmentRaycastManager");
            }

            pca.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            pca.RequestedResolution = new Vector2Int(1280, 960);
            pca.gameObject.SetActive(true);
            pca.enabled = true;
            EditorUtility.SetDirty(pca);
            raycast.gameObject.SetActive(true);
            raycast.enabled = true;
            EditorUtility.SetDirty(raycast);

            // MediaPipe bootstrap (GPU inference: the Android AAR ships only the GPU Objectron subgraph).
            var bootstrapGo = new GameObject("MediaPipe Bootstrap");
            var bootstrap = bootstrapGo.AddComponent<Bootstrap>();
            var bso = new SerializedObject(bootstrap);
            bso.FindProperty("_defaultImageSource").enumValueIndex = (int)ImageSourceType.Unknown;
            bso.FindProperty("_preferableInferenceMode").enumValueIndex = (int)InferenceMode.GPU;
            bso.FindProperty("_assetLoaderType").enumValueIndex = (int)Bootstrap.AssetLoaderType.StreamingAssets;
            bso.FindProperty("_enableGlog").boolValue = false;
            bso.ApplyModifiedPropertiesWithoutUndo();

            // Detector.
            var detectorGo = new GameObject("Objectron Detector");
            var graph = detectorGo.AddComponent<ObjectronGraph>();
            var gso = new SerializedObject(graph);
            gso.FindProperty("_cpuConfig").objectReferenceValue = LoadText("objectron_cpu.txt");
            gso.FindProperty("_gpuConfig").objectReferenceValue = LoadText("objectron_gpu.txt");
            gso.FindProperty("_openGlEsConfig").objectReferenceValue = LoadText("objectron_opengles.txt");
            gso.ApplyModifiedPropertiesWithoutUndo();
            graph.category = ObjectronGraph.Category.Chair;
            graph.maxNumObjects = 4;
            var pool = detectorGo.AddComponent<TextureFramePool>();
            var imageSource = detectorGo.AddComponent<PcaImageSource>();
            var detector = detectorGo.AddComponent<ObjectronDetector>();
            var dso = new SerializedObject(detector);
            dso.FindProperty("m_graph").objectReferenceValue = graph;
            dso.FindProperty("m_pool").objectReferenceValue = pool;
            dso.FindProperty("m_imageSource").objectReferenceValue = imageSource;
            dso.FindProperty("m_bootstrap").objectReferenceValue = bootstrap;
            dso.ApplyModifiedPropertiesWithoutUndo();

            // App.
            var appGo = new GameObject("QuestDetect3D");
            var app = appGo.AddComponent<Detect3DApp>();
            var aso = new SerializedObject(app);
            aso.FindProperty("m_camera").objectReferenceValue = pca;
            aso.FindProperty("m_raycast").objectReferenceValue = raycast;
            aso.FindProperty("m_detector").objectReferenceValue = detector;
            aso.FindProperty("m_overlayShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            aso.ApplyModifiedPropertiesWithoutUndo();

            if (AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath) == null)
            {
                throw new System.Exception($"QuestDetect3D: shader not found at {ShaderPath}");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            var roots = string.Join(", ", scene.GetRootGameObjects().Select(r => r.name));
            Debug.Log($"QuestDetect3D: scene built at {ScenePath} roots=[{roots}]");
        }

        private static TextAsset LoadText(string file)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(GraphFolder + file);
            if (asset == null)
            {
                throw new System.Exception($"QuestDetect3D: MediaPipe graph config missing: {GraphFolder}{file}");
            }

            return asset;
        }

        private static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }
    }
}
#endif
