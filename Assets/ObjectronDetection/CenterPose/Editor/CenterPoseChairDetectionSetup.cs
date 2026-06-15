#if UNITY_EDITOR
using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using QuestObjectron;
using QuestObjectron.CenterPose;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestObjectron.CenterPose.Editor
{
    public static class CenterPoseChairDetectionSetup
    {
        private const string ScenePath = "Assets/ObjectronDetection/Scenes/CenterPoseChairDetection.unity";
        private const string CupScenePath = "Assets/ObjectronDetection/Scenes/ObjectronCupDetection.unity";
        private const string SentisPath = "Assets/ObjectronDetection/CenterPose/Models/chair.sentis";
        private const string OnnxPath = "Assets/ObjectronDetection/CenterPose/Models/chair.onnx";
        private const string ConfigurePrefKey = "QuestObjectron_CenterPoseChairSceneConfigured_v1";

        private static string DefaultOnnxSourcePath => System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Application.dataPath, "../../pose_estimation_app/models/centerpose/chair.onnx"));

        /// <summary>Unity batchmode: -executeMethod QuestObjectron.CenterPose.Editor.CenterPoseChairDetectionSetup.FullSetupBatch</summary>
        public static void FullSetupBatch()
        {
            try
            {
                if (!CopyOnnxFromDefaultPath(showDialog: false))
                {
                    Debug.LogError("QuestObjectron CenterPose: chair.onnx not found. Expected pose_estimation_app/models/centerpose/chair.onnx");
                    EditorApplication.Exit(1);
                    return;
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                ConvertOnnxToSentis();
                CreateSceneFromCupTemplate(silent: true);
                AddSceneToBuildSettings(ScenePath);
                AssignStartMenuChairModel();
                EditorPrefs.SetBool(ConfigurePrefKey, true);
                AssetDatabase.SaveAssets();
                Debug.Log("QuestObjectron CenterPose: FullSetupBatch completed.");
                EditorApplication.Exit(0);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"QuestObjectron CenterPose FullSetupBatch failed: {ex.Message}\n{ex.StackTrace}");
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("QuestObjectron/CenterPose/Run Full Setup (Copy ONNX + Sentis + Scene)")]
        public static void FullSetupMenu()
        {
            if (!CopyOnnxFromDefaultPath(showDialog: true))
            {
                return;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            ConvertOnnxToSentis();
            CreateSceneFromCupTemplate(silent: false);
            AddSceneToBuildSettings(ScenePath);
            AssignStartMenuChairModel();
            EditorPrefs.SetBool(ConfigurePrefKey, true);
        }

        [MenuItem("QuestObjectron/CenterPose/Copy Chair ONNX From Pose Estimation App")]
        public static void CopyOnnxFromPoseApp()
        {
            CopyOnnxFromDefaultPath(showDialog: true);
        }

        private static bool CopyOnnxFromDefaultPath(bool showDialog)
        {
            var source = DefaultOnnxSourcePath;
            if (!System.IO.File.Exists(source) && showDialog)
            {
                source = EditorUtility.OpenFilePanel(
                    "Select chair.onnx",
                    System.IO.Path.GetDirectoryName(DefaultOnnxSourcePath) ?? "",
                    "onnx");
            }

            if (string.IsNullOrEmpty(source) || !System.IO.File.Exists(source))
            {
                return false;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OnnxPath) ?? OnnxPath);
            System.IO.File.Copy(source, OnnxPath, true);
            AssetDatabase.Refresh();
            Debug.Log($"QuestObjectron: copied ONNX to {OnnxPath}");
            return true;
        }

        [MenuItem("QuestObjectron/CenterPose/Create Scene From Cup Template")]
        public static void CreateSceneFromCupTemplateMenu()
        {
            CreateSceneFromCupTemplate(silent: false);
        }

        private static void CreateSceneFromCupTemplate(bool silent)
        {
            if (!System.IO.File.Exists(CupScenePath))
            {
                Debug.LogError($"QuestObjectron: cup template scene missing at {CupScenePath}");
                return;
            }

            EditorSceneManager.OpenScene(CupScenePath, OpenSceneMode.Single);

            var bootstrap = GameObject.Find("Bootstrap");
            if (bootstrap != null)
            {
                bootstrap.SetActive(false);
            }

            DisableSentisComponents();

            var cameraAccess = Object.FindFirstObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            var environmentRaycast = Object.FindFirstObjectByType<EnvironmentRayCastSampleManager>(FindObjectsInactive.Include);

            var questRoot = GameObject.Find("QuestObjectron");
            if (questRoot == null)
            {
                Debug.LogError("QuestObjectron: QuestObjectron root not found in cup scene.");
                return;
            }

            MigrateQuestRootToCenterPose(questRoot, cameraAccess, environmentRaycast);

            if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath))
            {
                Debug.LogError($"QuestObjectron: failed to save {ScenePath}");
                return;
            }

            AddSceneToBuildSettings(ScenePath);
            if (!silent)
            {
                Debug.Log($"QuestObjectron: saved CenterPose scene to {ScenePath}");
            }
        }

        private static void MigrateQuestRootToCenterPose(
            GameObject questRoot,
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager environmentRaycast)
        {
            var imageSource = questRoot.GetComponent<PassthroughImageSource>();
            var questVisuals = questRoot.GetComponent<ObjectronQuestVisuals>();

            DestroyIfPresent<ObjectronCupDetectionManager>(questRoot);
            DestroyIfPresent<ObjectronGraph>(questRoot);
            DestroyIfPresent<TextureFramePool>(questRoot);
            DestroyIfPresent<ObjectronSceneReferences>(questRoot);
            DestroyIfPresent<ObjectronEnvironmentDepthProvider>(questRoot);
            DestroyIfPresent<ObjectronPassthroughOverlay>(questRoot);
            DestroyIfPresent<ObjectronHeadsetHud>(questRoot);
            DestroyIfPresent<ObjectronPlacementFixMenu>(questRoot);

            questRoot.name = "QuestCenterPose";

            if (questRoot.GetComponent<ObjectronDisableSentis>() == null)
            {
                questRoot.AddComponent<ObjectronDisableSentis>();
            }

            if (questRoot.GetComponent<CenterPoseSceneReferences>() == null)
            {
                questRoot.AddComponent<CenterPoseSceneReferences>();
            }

            var manager = questRoot.GetComponent<CenterPoseChairDetectionManager>()
                ?? questRoot.AddComponent<CenterPoseChairDetectionManager>();

            if (imageSource == null)
            {
                imageSource = questRoot.AddComponent<PassthroughImageSource>();
            }

            if (questVisuals == null)
            {
                questVisuals = questRoot.AddComponent<ObjectronQuestVisuals>();
            }

            WireManager(manager, cameraAccess, imageSource, environmentRaycast, questVisuals);

            var refs = questRoot.GetComponent<CenterPoseSceneReferences>();
            var refsSo = new SerializedObject(refs);
            refsSo.FindProperty("m_manager").objectReferenceValue = manager;
            refsSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void DestroyIfPresent<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            if (component != null)
            {
                Object.DestroyImmediate(component);
            }
        }

        [MenuItem("QuestObjectron/CenterPose/Copy Chair ONNX From Pose Estimation App (Browse)")]
        public static void CopyOnnxFromPoseAppBrowse()
        {
            var defaultSource = DefaultOnnxSourcePath;
            var source = EditorUtility.OpenFilePanel("Select chair.onnx", System.IO.Path.GetDirectoryName(defaultSource) ?? "", "onnx");
            if (string.IsNullOrEmpty(source))
            {
                return;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OnnxPath) ?? OnnxPath);
            System.IO.File.Copy(source, OnnxPath, true);
            AssetDatabase.Refresh();
            Debug.Log($"QuestObjectron: copied ONNX to {OnnxPath}. Next: convert to Sentis.");
        }

        [MenuItem("QuestObjectron/CenterPose/Convert Chair ONNX To Sentis")]
        public static void ConvertOnnxToSentis()
        {
            if (!System.IO.File.Exists(OnnxPath))
            {
                Debug.LogError($"QuestObjectron: ONNX not found at {OnnxPath}. Run Copy Chair ONNX first.");
                return;
            }

            var onnxAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(OnnxPath);
            if (onnxAsset == null)
            {
                Debug.LogError($"QuestObjectron: could not load ModelAsset at {OnnxPath}");
                return;
            }

            var model = ModelLoader.Load(onnxAsset);
            ModelWriter.Save(SentisPath, model);
            AssetDatabase.Refresh();
            Debug.Log($"QuestObjectron: wrote Sentis model to {SentisPath}");
        }

        [MenuItem("QuestObjectron/CenterPose/Configure CenterPose Chair Detection Scene")]
        public static void ConfigureExistingSceneMenu()
        {
            ConfigureExistingScene(silent: false);
            EditorPrefs.SetBool(ConfigurePrefKey, true);
        }

        [MenuItem("QuestObjectron/CenterPose/Create CenterPose Chair Scene (Empty)")]
        public static void CreateEmptyScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateQuestCenterPoseRoot(null, null);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);
            EditorPrefs.SetBool(ConfigurePrefKey, true);
            Debug.Log($"QuestObjectron: created empty scene at {ScenePath}. Add Meta PCA + MRUK prefabs from MultiObjectDetection sample.");
        }

        [MenuItem("QuestObjectron/CenterPose/Add Scene To Build Settings")]
        public static void AddToBuild()
        {
            AddSceneToBuildSettings(ScenePath);
        }

        private static void ConfigureExistingScene(bool silent)
        {
            if (!System.IO.File.Exists(ScenePath))
            {
                if (!silent)
                {
                    Debug.LogWarning($"QuestObjectron: scene not found at {ScenePath}. Creating empty scene.");
                }

                CreateEmptyScene();
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            DisableSentisComponents();

            var cameraAccess = Object.FindFirstObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            var environmentRaycast = Object.FindFirstObjectByType<EnvironmentRayCastSampleManager>(FindObjectsInactive.Include);

            var questRoot = GameObject.Find("QuestCenterPose");
            if (questRoot == null)
            {
                CreateQuestCenterPoseRoot(cameraAccess, environmentRaycast);
            }
            else
            {
                WireQuestCenterPoseRoot(questRoot, cameraAccess, environmentRaycast);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AddSceneToBuildSettings(ScenePath);

            if (!silent)
            {
                Debug.Log("QuestObjectron: CenterPoseChairDetection scene configured.");
            }
        }

        private static void CreateQuestCenterPoseRoot(
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager environmentRaycast)
        {
            var detectionRoot = new GameObject("QuestCenterPose");
            detectionRoot.AddComponent<ObjectronDisableSentis>();
            detectionRoot.AddComponent<CenterPoseSceneReferences>();

            var manager = detectionRoot.AddComponent<CenterPoseChairDetectionManager>();
            var imageSource = detectionRoot.AddComponent<PassthroughImageSource>();
            var questVisuals = detectionRoot.AddComponent<ObjectronQuestVisuals>();

            WireManager(manager, cameraAccess, imageSource, environmentRaycast, questVisuals);

            var refs = detectionRoot.GetComponent<CenterPoseSceneReferences>();
            var refsSo = new SerializedObject(refs);
            refsSo.FindProperty("m_manager").objectReferenceValue = manager;
            refsSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void WireQuestCenterPoseRoot(
            GameObject detectionRoot,
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager environmentRaycast)
        {
            if (detectionRoot.GetComponent<ObjectronDisableSentis>() == null)
            {
                detectionRoot.AddComponent<ObjectronDisableSentis>();
            }

            if (detectionRoot.GetComponent<CenterPoseSceneReferences>() == null)
            {
                detectionRoot.AddComponent<CenterPoseSceneReferences>();
            }

            var manager = detectionRoot.GetComponent<CenterPoseChairDetectionManager>()
                ?? detectionRoot.AddComponent<CenterPoseChairDetectionManager>();
            var imageSource = detectionRoot.GetComponent<PassthroughImageSource>()
                ?? detectionRoot.AddComponent<PassthroughImageSource>();
            var questVisuals = detectionRoot.GetComponent<ObjectronQuestVisuals>()
                ?? detectionRoot.AddComponent<ObjectronQuestVisuals>();
            WireManager(manager, cameraAccess, imageSource, environmentRaycast, questVisuals);

            var refs = detectionRoot.GetComponent<CenterPoseSceneReferences>();
            var refsSo = new SerializedObject(refs);
            refsSo.FindProperty("m_manager").objectReferenceValue = manager;
            refsSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void WireManager(
            CenterPoseChairDetectionManager manager,
            PassthroughCameraAccess cameraAccess,
            PassthroughImageSource imageSource,
            EnvironmentRayCastSampleManager environmentRaycast,
            ObjectronQuestVisuals questVisuals)
        {
            var so = new SerializedObject(manager);
            if (cameraAccess != null)
            {
                so.FindProperty("m_cameraAccess").objectReferenceValue = cameraAccess;
            }

            so.FindProperty("m_imageSource").objectReferenceValue = imageSource;
            if (environmentRaycast != null)
            {
                so.FindProperty("m_environmentRaycast").objectReferenceValue = environmentRaycast;
            }

            so.FindProperty("m_questVisuals").objectReferenceValue = questVisuals;

            var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(SentisPath)
                ?? AssetDatabase.LoadAssetAtPath<ModelAsset>(OnnxPath);
            if (model != null)
            {
                so.FindProperty("m_centerPoseModel").objectReferenceValue = model;
            }
            else
            {
                Debug.LogWarning($"QuestObjectron: assign {SentisPath} or {OnnxPath} after import.");
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            manager.WireReferences(cameraAccess, imageSource, environmentRaycast, questVisuals);
        }

        private static void AssignStartMenuChairModel()
        {
            const string startScenePath = "Assets/PassthroughCameraApiSamples/StartScene/StartScene.unity";
            if (!System.IO.File.Exists(startScenePath))
            {
                return;
            }

            var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(SentisPath)
                ?? AssetDatabase.LoadAssetAtPath<ModelAsset>(OnnxPath);
            if (model == null)
            {
                return;
            }

            var scene = EditorSceneManager.OpenScene(startScenePath, OpenSceneMode.Additive);
            foreach (var menu in Object.FindObjectsByType<PassthroughCameraSamples.StartScene.StartMenu>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var so = new SerializedObject(menu);
                so.FindProperty("m_centerPoseChairModel").objectReferenceValue = model;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            EditorSceneManager.CloseScene(scene, true);
        }

        private static void DisableSentisComponents()
        {
            foreach (var sentis in Object.FindObjectsByType<SentisInferenceRunManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sentis.gameObject.SetActive(false);
            }
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var existing in scenes)
            {
                if (existing.path == scenePath)
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
