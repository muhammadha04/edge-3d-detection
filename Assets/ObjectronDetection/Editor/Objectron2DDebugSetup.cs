#if UNITY_EDITOR
using System.IO;
using System.Linq;
using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using PassthroughCameraSamples.CameraViewer;
using PassthroughCameraSamples.MultiObjectDetection;
using QuestObjectron;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace QuestObjectron.Editor
{
    public static class Objectron2DDebugSetup
    {
        private const string ScenePath = "Assets/ObjectronDetection/Scenes/Objectron2DDebug.unity";
        private const string TemplateScenePath = "Assets/ObjectronDetection/Scenes/ObjectronBoxDebug.unity";
        private const string CameraViewerPrefabPath =
            "Assets/PassthroughCameraApiSamples/CameraViewer/Prefabs/CameraViewerManagerPrefab.prefab";
        private const string ConfigurePrefKey = "QuestObjectron_Objectron2DDebugSceneConfigured_v3";

        [InitializeOnLoadMethod]
        private static void AutoConfigureOnLoad()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool(ConfigurePrefKey, false))
                {
                    return;
                }

                if (!File.Exists(ScenePath))
                {
                    return;
                }

                try
                {
                    ConfigureScene(silent: true);
                    EditorPrefs.SetBool(ConfigurePrefKey, true);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"QuestObjectron 2D Debug auto-configure failed: {ex.Message}\n{ex.StackTrace}");
                }
            };
        }

        [MenuItem("QuestObjectron/Create Objectron 2D Debug Scene")]
        public static void Create2DDebugScene()
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

            ConfigureScene(silent: false);
            EditorPrefs.SetBool(ConfigurePrefKey, true);
            AddSceneToBuildSettings(ScenePath);
            Debug.Log($"Objectron 2D Debug scene ready at {ScenePath}. Build to Quest and open from start menu.");
        }

        [MenuItem("QuestObjectron/Add Objectron 2D Debug Scene To Build Settings")]
        public static void Add2DDebugToBuildSettingsMenu()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"Scene not found: {ScenePath}. Run QuestObjectron/Create Objectron 2D Debug Scene first.");
                return;
            }

            AddSceneToBuildSettings(ScenePath);
        }

        private static void ConfigureScene(bool silent)
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            GameObject root = null;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name.Contains("ObjectronBoxDebug")
                    || go.name.Contains("Objectron2DDebug")
                    || go.name.Contains("QuestObjectron"))
                {
                    root = go;
                    break;
                }
            }

            if (root == null)
            {
                Debug.LogError("2D Debug setup: could not find Objectron root in scene.");
                return;
            }

            root.name = "Objectron2DDebug";

            RemoveComponent<ObjectronBoxDebugManager>(root);
            RemoveComponent<ObjectronBoxDebugUi>(root);
            RemoveComponent<ObjectronLabeledBoxVisuals>(root);
            RemoveComponent<ObjectronSceneReferences>(root);
            RemoveComponent<ObjectronChairDetectionManager>(root);
            RemoveComponent<ObjectronQuestVisuals>(root);
            RemoveComponent<ObjectronScanMeshVisuals>(root);
            RemoveComponent<ObjectronLiveMeshTuner>(root);
            RemoveComponent<ObjectronPassthroughOverlay>(root);

            var worldBoxes = root.transform.Find("WorldBoundingBoxes");
            if (worldBoxes != null)
            {
                Object.DestroyImmediate(worldBoxes.gameObject);
            }

            DisableSentisComponents();

            var cameraAccess = Object.FindFirstObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            var bootstrap = Object.FindFirstObjectByType<Bootstrap>(FindObjectsInactive.Include);
            if (bootstrap == null && !silent)
            {
                Debug.LogWarning("2D Debug setup: Bootstrap not found — ensure MediaPipe Bootstrap exists in scene.");
            }

            var feedImage = EnsureCameraViewerPanel(cameraAccess);

            var manager = root.GetComponent<Objectron2DDebugManager>();
            if (manager == null)
            {
                manager = root.AddComponent<Objectron2DDebugManager>();
            }

            var feedView = root.GetComponent<Objectron2DCameraFeedView>();
            if (feedView == null)
            {
                feedView = root.AddComponent<Objectron2DCameraFeedView>();
            }

            var imageSource = root.GetComponent<PassthroughImageSource>()
                ?? root.AddComponent<PassthroughImageSource>();
            var graph = root.GetComponent<ObjectronGraph>() ?? root.AddComponent<ObjectronGraph>();
            var pool = root.GetComponent<TextureFramePool>() ?? root.AddComponent<TextureFramePool>();
            var overlay = root.GetComponent<Objectron2DFeedOverlay>()
                ?? root.AddComponent<Objectron2DFeedOverlay>();

            ConfigureObjectronGraph(graph);
            ConfigureTextureFramePool(pool);
            WireFeedView(feedView, cameraAccess, feedImage);
            WireManager(manager, cameraAccess, imageSource, graph, bootstrap, pool, overlay, feedView, feedImage);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            if (!silent)
            {
                Debug.Log("QuestObjectron: Objectron2DDebug scene configured with CameraViewer panel.");
            }
        }

        private static RawImage EnsureCameraViewerPanel(PassthroughCameraAccess cameraAccess)
        {
            foreach (var viewer in Object.FindObjectsByType<CameraViewerManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var existingImage = viewer.GetComponentInChildren<RawImage>(true);
                if (existingImage != null)
                {
                    WireCameraViewerManager(viewer, cameraAccess);
                    return existingImage;
                }
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraViewerPrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning($"2D Debug setup: CameraViewer prefab not found at {CameraViewerPrefabPath}");
                return null;
            }

            var eyeAnchor = FindCenterEyeAnchor();
            if (eyeAnchor == null)
            {
                Debug.LogWarning("2D Debug setup: CenterEyeAnchor not found — camera feed will be created at runtime.");
                return null;
            }

            var instance = PrefabUtility.InstantiatePrefab(prefab, eyeAnchor) as GameObject;
            if (instance == null)
            {
                return null;
            }

            instance.name = "Objectron2DCameraViewer";
            var rt = instance.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.localPosition = new Vector3(0f, 0f, 1.15f);
                rt.localRotation = Quaternion.identity;
                rt.localScale = Vector3.one * 0.001f;
            }

            var viewerManager = instance.GetComponent<CameraViewerManager>();
            WireCameraViewerManager(viewerManager, cameraAccess);
            return instance.GetComponentInChildren<RawImage>(true);
        }

        private static void WireCameraViewerManager(CameraViewerManager viewer, PassthroughCameraAccess cameraAccess)
        {
            if (viewer == null || cameraAccess == null)
            {
                return;
            }

            var so = new SerializedObject(viewer);
            so.FindProperty("m_cameraAccess").objectReferenceValue = cameraAccess;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Transform FindCenterEyeAnchor()
        {
            var rig = Object.FindFirstObjectByType<OVRCameraRig>(FindObjectsInactive.Include);
            return rig != null ? rig.centerEyeAnchor : null;
        }

        private static void WireFeedView(
            Objectron2DCameraFeedView feedView,
            PassthroughCameraAccess cameraAccess,
            RawImage feedImage)
        {
            if (feedView == null)
            {
                return;
            }

            var so = new SerializedObject(feedView);
            if (cameraAccess != null)
            {
                so.FindProperty("m_cameraAccess").objectReferenceValue = cameraAccess;
            }

            if (feedImage != null)
            {
                so.FindProperty("m_image").objectReferenceValue = feedImage;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RemoveComponent<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            if (component != null)
            {
                Object.DestroyImmediate(component);
            }
        }

        private static void ConfigureObjectronGraph(ObjectronGraph graph)
        {
            var cpuConfig = AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Assets/MediaPipeUnity/Samples/Scenes/Objectron/objectron_cpu.txt");
            var gpuConfig = AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Assets/MediaPipeUnity/Samples/Scenes/Objectron/objectron_gpu.txt");
            var glesConfig = AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Assets/MediaPipeUnity/Samples/Scenes/Objectron/objectron_opengles.txt");

            var so = new SerializedObject(graph);
            SetObjectReference(so, "_cpuConfig", cpuConfig);
            SetObjectReference(so, "_gpuConfig", gpuConfig);
            SetObjectReference(so, "_openGlEsConfig", glesConfig);
            SetEnum(so, "category", (int)ObjectronGraph.Category.Chair);
            SetInt(so, "maxNumObjects", 3);
            SetFloat(so, "_minDetectionConfidence", 0.35f);
            SetFloat(so, "_minTrackingConfidence", 0.55f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureTextureFramePool(TextureFramePool pool)
        {
            var poolSo = new SerializedObject(pool);
            SetInt(poolSo, "_poolSize", 3);
            poolSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void WireManager(
            Objectron2DDebugManager manager,
            PassthroughCameraAccess cameraAccess,
            PassthroughImageSource imageSource,
            ObjectronGraph graph,
            Bootstrap bootstrap,
            TextureFramePool pool,
            Objectron2DFeedOverlay feedOverlay,
            Objectron2DCameraFeedView feedView,
            RawImage feedImage)
        {
            var managerSo = new SerializedObject(manager);
            if (cameraAccess != null)
            {
                managerSo.FindProperty("m_cameraAccess").objectReferenceValue = cameraAccess;
            }

            managerSo.FindProperty("m_imageSource").objectReferenceValue = imageSource;
            managerSo.FindProperty("m_objectronGraph").objectReferenceValue = graph;
            managerSo.FindProperty("m_bootstrap").objectReferenceValue = bootstrap;
            managerSo.FindProperty("m_textureFramePool").objectReferenceValue = pool;
            managerSo.FindProperty("m_feedOverlay").objectReferenceValue = feedOverlay;
            managerSo.FindProperty("m_cameraFeedView").objectReferenceValue = feedView;
            managerSo.FindProperty("m_cameraFeedImage").objectReferenceValue = feedImage;
            SetEnum(managerSo, "m_runningMode", (int)RunningMode.Async);
            SetFloat(managerSo, "m_minDetectionConfidence", 0.35f);
            SetFloat(managerSo, "m_minTrackingConfidence", 0.55f);
            managerSo.ApplyModifiedPropertiesWithoutUndo();

            if (imageSource != null)
            {
                var imageSo = new SerializedObject(imageSource);
                SetEnum(imageSo, "m_rotation", (int)RotationAngle.Rotation0);
                var flipProp = imageSo.FindProperty("m_horizontallyFlipped");
                if (flipProp != null)
                {
                    flipProp.boolValue = true;
                }

                imageSo.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void DisableSentisComponents()
        {
            foreach (var sentis in Object.FindObjectsByType<SentisInferenceRunManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sentis.enabled = false;
            }

            foreach (var ui in Object.FindObjectsByType<SentisInferenceUiManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                ui.enabled = false;
            }

            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (go.name.Contains("SentisInference"))
                {
                    go.SetActive(false);
                }
            }
        }

        private static void SetObjectReference(SerializedObject so, string propertyName, Object value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"2D Debug setup: missing property '{propertyName}' on {so.targetObject.GetType().Name}");
                return;
            }

            prop.objectReferenceValue = value;
        }

        private static void SetFloat(SerializedObject so, string propertyName, float value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                return;
            }

            prop.floatValue = value;
        }

        private static void SetInt(SerializedObject so, string propertyName, int value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                return;
            }

            prop.intValue = value;
        }

        private static void SetEnum(SerializedObject so, string propertyName, int enumValueOrIndex)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null || prop.propertyType != SerializedPropertyType.Enum)
            {
                return;
            }

            // Sequential enums (RunningMode): index matches value. RotationAngle uses 0/90/180/270 — use intValue.
            if (enumValueOrIndex >= 0 && enumValueOrIndex < prop.enumDisplayNames.Length)
            {
                prop.enumValueIndex = enumValueOrIndex;
            }
            else
            {
                prop.intValue = enumValueOrIndex;
            }
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
