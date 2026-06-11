#if UNITY_EDITOR
using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using QuestObjectron;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestObjectron.Editor
{
    public static class ObjectronCupDetectionSetup
    {
        private const string ScenePath = "Assets/ObjectronDetection/Scenes/ObjectronCupDetection.unity";
        private const string ConfigurePrefKey = "QuestObjectron_ObjectronCupSceneConfigured_v4";
        private const string InBoxMaterialPath =
            "Assets/ObjectronDetection/EnvironmentDepth/Materials/EnvironmentDepthInBox.mat";
        private const string InBoxShaderPath =
            "Assets/ObjectronDetection/EnvironmentDepth/Shaders/EnvironmentDepthInBoxUI.shader";
        private const string InBoxResourcesMaterialPath =
            "Assets/ObjectronDetection/Resources/QuestObjectron/EnvironmentDepthInBox.mat";

        [InitializeOnLoadMethod]
        private static void AutoConfigureOnLoad()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool(ConfigurePrefKey, false))
                {
                    return;
                }

                if (!System.IO.File.Exists(ScenePath))
                {
                    return;
                }

                try
                {
                    ConfigureExistingScene(silent: true);
                    EditorPrefs.SetBool(ConfigurePrefKey, true);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"QuestObjectron: auto scene configure failed: {ex.Message}\n{ex.StackTrace}");
                }
            };
        }

        [MenuItem("QuestObjectron/Configure Objectron Cup Detection Scene")]
        public static void ConfigureExistingSceneMenu()
        {
            ConfigureExistingScene(silent: false);
            EditorPrefs.SetBool(ConfigurePrefKey, true);
        }

        /// <summary>Unity batchmode: -executeMethod QuestObjectron.Editor.ObjectronCupDetectionSetup.ConfigureExistingSceneBatch</summary>
        public static void ConfigureExistingSceneBatch()
        {
            ConfigureExistingScene(silent: false);
            EditorPrefs.SetBool(ConfigurePrefKey, true);
        }

        [MenuItem("QuestObjectron/Create Objectron Cup Detection Scene (Empty)")]
        public static void CreateEmptyScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var bootstrap = CreateBootstrap();
            CreateQuestObjectronRoot(null, null, bootstrap);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);
            EditorPrefs.SetBool(ConfigurePrefKey, true);
            Debug.Log($"QuestObjectron: created empty scene at {ScenePath}. Add Meta Building Blocks + PCA prefabs from MultiObjectDetection sample.");
        }

        [MenuItem("QuestObjectron/Add Scene To Build Settings")]
        public static void AddToBuild()
        {
            AddSceneToBuildSettings(ScenePath);
        }

        private static void ConfigureExistingScene(bool silent)
        {
            if (!System.IO.File.Exists(ScenePath))
            {
                Debug.LogError($"QuestObjectron: scene not found at {ScenePath}");
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            EnsureInBoxDepthMaterial();

            var cameraAccess = Object.FindFirstObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            var environmentRaycast = Object.FindFirstObjectByType<EnvironmentRayCastSampleManager>(FindObjectsInactive.Include);

            if (cameraAccess == null && !silent)
            {
                Debug.LogWarning("QuestObjectron: PassthroughCameraAccess not found — ensure PCA prefab is in the scene.");
            }

            DisableSentisComponents();

            var bootstrap = Object.FindFirstObjectByType<Bootstrap>(FindObjectsInactive.Include);
            if (bootstrap == null)
            {
                bootstrap = CreateBootstrap();
            }
            else
            {
                ConfigureBootstrap(bootstrap);
            }

            var questRoot = GameObject.Find("QuestObjectron");
            if (questRoot == null)
            {
                CreateQuestObjectronRoot(cameraAccess, environmentRaycast, bootstrap);
            }
            else
            {
                WireQuestObjectronRoot(questRoot, cameraAccess, environmentRaycast, bootstrap);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AddSceneToBuildSettings(ScenePath);

            if (!silent)
            {
                Debug.Log("QuestObjectron: ObjectronCupDetection scene configured. Open the scene and build to Quest 3.");
            }
        }

        private static Bootstrap CreateBootstrap()
        {
            var bootstrapGo = new GameObject("Bootstrap");
            bootstrapGo.tag = "Global Resource";
            var bootstrap = bootstrapGo.AddComponent<Bootstrap>();
            ConfigureBootstrap(bootstrap);
            return bootstrap;
        }

        private static void ConfigureBootstrap(Bootstrap bootstrap)
        {
            var so = new SerializedObject(bootstrap);
            so.FindProperty("_defaultImageSource").enumValueIndex = (int)ImageSourceType.Unknown; // external PassthroughImageSource
            // Objectron on Android is GPU-only in homuler's prebuilt AAR (no ObjectronCpuSubgraph).
            so.FindProperty("_preferableInferenceMode").enumValueIndex = (int)InferenceMode.GPU;
            so.FindProperty("_assetLoaderType").enumValueIndex = 0; // StreamingAssets
            so.FindProperty("_enableGlog").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateQuestObjectronRoot(
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager environmentRaycast,
            Bootstrap bootstrap)
        {
            var detectionRoot = new GameObject("QuestObjectron");
            detectionRoot.AddComponent<ObjectronDisableSentis>();
            detectionRoot.AddComponent<ObjectronSceneReferences>();

            var manager = detectionRoot.AddComponent<ObjectronCupDetectionManager>();
            var imageSource = detectionRoot.AddComponent<PassthroughImageSource>();
            var graph = detectionRoot.AddComponent<ObjectronGraph>();
            var pool = detectionRoot.AddComponent<TextureFramePool>();

            ConfigureObjectronGraph(graph);
            ConfigureTextureFramePool(pool);

            var bboxGo = new GameObject("WorldBoundingBoxes");
            bboxGo.transform.SetParent(detectionRoot.transform, false);
            var drawer = bboxGo.AddComponent<OrientedBBoxDrawer>();
            var detectionDebug = bboxGo.AddComponent<ObjectronDetectionDebug>();
            var questVisuals = detectionRoot.AddComponent<ObjectronQuestVisuals>();

            WireManager(manager, cameraAccess, imageSource, graph, bootstrap, pool, environmentRaycast, drawer, detectionDebug, questVisuals);
            WireDepthInBox(detectionRoot);

            var refs = detectionRoot.GetComponent<ObjectronSceneReferences>();
            var refsSo = new SerializedObject(refs);
            refsSo.FindProperty("m_manager").objectReferenceValue = manager;
            refsSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void WireQuestObjectronRoot(
            GameObject detectionRoot,
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager environmentRaycast,
            Bootstrap bootstrap)
        {
            if (detectionRoot.GetComponent<ObjectronDisableSentis>() == null)
            {
                detectionRoot.AddComponent<ObjectronDisableSentis>();
            }

            if (detectionRoot.GetComponent<ObjectronSceneReferences>() == null)
            {
                detectionRoot.AddComponent<ObjectronSceneReferences>();
            }

            var manager = detectionRoot.GetComponent<ObjectronCupDetectionManager>()
                ?? detectionRoot.AddComponent<ObjectronCupDetectionManager>();
            var imageSource = detectionRoot.GetComponent<PassthroughImageSource>()
                ?? detectionRoot.AddComponent<PassthroughImageSource>();
            var graph = detectionRoot.GetComponent<ObjectronGraph>()
                ?? detectionRoot.AddComponent<ObjectronGraph>();
            var pool = detectionRoot.GetComponent<TextureFramePool>()
                ?? detectionRoot.AddComponent<TextureFramePool>();

            ConfigureObjectronGraph(graph);
            ConfigureTextureFramePool(pool);

            var bboxTransform = detectionRoot.transform.Find("WorldBoundingBoxes");
            OrientedBBoxDrawer drawer;
            if (bboxTransform == null)
            {
                var bboxGo = new GameObject("WorldBoundingBoxes");
                bboxGo.transform.SetParent(detectionRoot.transform, false);
                drawer = bboxGo.AddComponent<OrientedBBoxDrawer>();
            }
            else
            {
                drawer = bboxTransform.GetComponent<OrientedBBoxDrawer>()
                    ?? bboxTransform.gameObject.AddComponent<OrientedBBoxDrawer>();
            }

            var detectionDebug = drawer.GetComponent<ObjectronDetectionDebug>()
                ?? drawer.gameObject.AddComponent<ObjectronDetectionDebug>();
            var questVisuals = detectionRoot.GetComponent<ObjectronQuestVisuals>()
                ?? detectionRoot.AddComponent<ObjectronQuestVisuals>();

            WireManager(manager, cameraAccess, imageSource, graph, bootstrap, pool, environmentRaycast, drawer, detectionDebug, questVisuals);
            WireDepthInBox(detectionRoot);

            var refs = detectionRoot.GetComponent<ObjectronSceneReferences>();
            if (refs != null)
            {
                var refsSo = new SerializedObject(refs);
                refsSo.FindProperty("m_manager").objectReferenceValue = manager;
                refsSo.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void WireDepthInBox(GameObject detectionRoot)
        {
            if (detectionRoot.GetComponent<ObjectronEnvironmentDepthProvider>() == null)
            {
                detectionRoot.AddComponent<ObjectronEnvironmentDepthProvider>();
            }
        }

        private static void EnsureInBoxDepthMaterial()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(InBoxShaderPath);
            if (shader == null)
            {
                shader = Shader.Find("QuestObjectron/EnvironmentDepthInBoxUI");
            }

            if (shader == null)
            {
                Debug.LogError($"QuestObjectron: in-box depth shader missing at {InBoxShaderPath}");
                return;
            }

            EnsureMaterialAtPath(InBoxMaterialPath, shader);
            var resourcesDir = System.IO.Path.GetDirectoryName(InBoxResourcesMaterialPath);
            if (!string.IsNullOrEmpty(resourcesDir))
            {
                System.IO.Directory.CreateDirectory(resourcesDir);
            }

            EnsureMaterialAtPath(InBoxResourcesMaterialPath, shader);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureMaterialAtPath(string materialPath, Shader shader)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat != null)
            {
                if (mat.shader != shader)
                {
                    mat.shader = shader;
                    EditorUtility.SetDirty(mat);
                }

                return;
            }

            var dir = System.IO.Path.GetDirectoryName(materialPath);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, materialPath);
        }

        private static void ConfigureObjectronGraph(ObjectronGraph graph)
        {
            if (graph == null)
            {
                Debug.LogError("QuestObjectron: ObjectronGraph is null");
                return;
            }

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

            SetEnum(so, "category", (int)ObjectronGraph.Category.Cup);
            SetInt(so, "maxNumObjects", 3);
            // Serialized backing fields (not the public property names).
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

        private static void SetObjectReference(SerializedObject so, string propertyName, Object value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"QuestObjectron: missing serialized property '{propertyName}' on {so.targetObject.GetType().Name}");
                return;
            }

            prop.objectReferenceValue = value;
        }

        private static void SetFloat(SerializedObject so, string propertyName, float value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"QuestObjectron: missing serialized property '{propertyName}' on {so.targetObject.GetType().Name}");
                return;
            }

            prop.floatValue = value;
        }

        private static void SetInt(SerializedObject so, string propertyName, int value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"QuestObjectron: missing serialized property '{propertyName}' on {so.targetObject.GetType().Name}");
                return;
            }

            prop.intValue = value;
        }

        private static void SetEnum(SerializedObject so, string propertyName, int enumValueIndex)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"QuestObjectron: missing serialized property '{propertyName}' on {so.targetObject.GetType().Name}");
                return;
            }

            prop.enumValueIndex = enumValueIndex;
        }

        private static void WireManager(
            ObjectronCupDetectionManager manager,
            PassthroughCameraAccess cameraAccess,
            PassthroughImageSource imageSource,
            ObjectronGraph graph,
            Bootstrap bootstrap,
            TextureFramePool pool,
            EnvironmentRayCastSampleManager environmentRaycast,
            OrientedBBoxDrawer drawer,
            ObjectronDetectionDebug detectionDebug = null,
            ObjectronQuestVisuals questVisuals = null)
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
            if (environmentRaycast != null)
            {
                managerSo.FindProperty("m_environmentRaycast").objectReferenceValue = environmentRaycast;
            }

            managerSo.FindProperty("m_bboxDrawer").objectReferenceValue = drawer;
            if (detectionDebug != null)
            {
                managerSo.FindProperty("m_detectionDebug").objectReferenceValue = detectionDebug;
            }

            if (questVisuals != null)
            {
                managerSo.FindProperty("m_questVisuals").objectReferenceValue = questVisuals;
            }
            // Async + ObserveOutputStream — safe on Quest. NonBlockingSync must not also register callbacks.
            SetEnum(managerSo, "m_runningMode", (int)RunningMode.Async);
            SetFloat(managerSo, "m_minDetectionConfidence", 0.35f);
            SetFloat(managerSo, "m_minTrackingConfidence", 0.55f);
            managerSo.ApplyModifiedPropertiesWithoutUndo();

            if (imageSource != null)
            {
                var imageSo = new SerializedObject(imageSource);
                SetEnum(imageSo, "m_rotation", (int)RotationAngle.Rotation90);
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
            foreach (var sentis in Object.FindObjectsByType<SentisInferenceRunManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sentis.enabled = false;
            }

            foreach (var ui in Object.FindObjectsByType<SentisInferenceUiManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
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

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            var found = false;
            foreach (var s in scenes)
            {
                if (s.path == scenePath)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                scenes.Add(new EditorBuildSettingsScene(scenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
        }
    }
}
#endif
