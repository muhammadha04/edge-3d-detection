#if UNITY_EDITOR
// Builds Assets/QuestDetect3D/Scenes/Detect3D.unity from Meta's known-good MultiObjectDetection sample scene
// (camera rig + passthrough + Passthrough Camera Access + Environment Raycast), strips the sample's own YOLO UI,
// and adds the MediaPipe bootstrap, the Objectron detector and the QuestDetect3D app (which runs the sample's
// YOLOv9-t model itself through Unity Inference Engine).
// Menu: QuestDetect3D → Rebuild Scene. Batch: called by BuildScript.BuildAndroid.
using System.Linq;
using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using Oculus.Interaction;
using QuestDetect3D.Annotation;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QuestDetect3D.EditorTools
{
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/QuestDetect3D/Scenes/Detect3D.unity";
        private const string TemplatePath = "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";
        private const string ShaderPath = "Assets/QuestDetect3D/Shaders/QD3D_UnlitTransparent.shader";
        private const string GraphFolder = "Assets/MediaPipeUnity/Samples/Scenes/Objectron/";
        /// <summary>Model the app ships: our FP16 export of the sample's YOLOv9-t ONNX (see YoloModelExport).</summary>
        public const string YoloModelPath = YoloModelExport.Fp16Path;
        /// <summary>Meta's prebuilt uint8 export (kept for comparison in the self-test).</summary>
        public const string MetaYoloModelPath = "Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/yolov9sentis.sentis";
        public const string YoloLabelsPath = "Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/SentisYoloClasses.txt";

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
            YoloModelExport.EnsureExported();
            var yoloModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(YoloModelPath);
            var yoloLabels = AssetDatabase.LoadAssetAtPath<TextAsset>(YoloLabelsPath);
            if (yoloModel == null || yoloLabels == null)
            {
                throw new System.Exception($"QuestDetect3D: YOLO model/labels missing ({YoloModelPath})");
            }

            aso.FindProperty("m_yoloModel").objectReferenceValue = yoloModel;
            aso.FindProperty("m_yoloLabels").objectReferenceValue = yoloLabels;
            aso.FindProperty("m_overlayShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            aso.FindProperty("m_annotate").objectReferenceValue = BuildAnnotateMode();
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

        /// <summary>QD3D Annotate: the mode controller plus an EventSystem with the Interaction SDK canvas module
        /// (inactive until Annotate is started, so Detect mode runs exactly as before). Panels and controller
        /// rays are created from code at runtime.</summary>
        private static AnnotationMode BuildAnnotateMode()
        {
            var go = new GameObject("QD3D Annotate");
            var mode = go.AddComponent<AnnotationMode>();
            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.transform.SetParent(go.transform, false);
            eventSystemGo.AddComponent<EventSystem>();
            eventSystemGo.AddComponent<PointableCanvasModule>();
            eventSystemGo.SetActive(false);
            var mso = new SerializedObject(mode);
            mso.FindProperty("m_eventSystem").objectReferenceValue = eventSystemGo;
            mso.ApplyModifiedPropertiesWithoutUndo();
            return mode;
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
