#if UNITY_EDITOR
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
        private const string SentisPath = "Assets/ObjectronDetection/CenterPose/Models/chair.sentis";
        private const string OnnxPath = "Assets/ObjectronDetection/CenterPose/Models/chair.onnx";
        private const string ConfigurePrefKey = "QuestObjectron_CenterPoseChairSceneConfigured_v1";

        [MenuItem("QuestObjectron/CenterPose/Copy Chair ONNX From Pose Estimation App")]
        public static void CopyOnnxFromPoseApp()
        {
            var defaultSource = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "../../../pose_estimation_app/models/centerpose/chair.onnx"));
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
            questVisuals.Prewarm();

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
            questVisuals.Prewarm();
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

            var sentis = AssetDatabase.LoadAssetAtPath<ModelAsset>(SentisPath);
            if (sentis != null)
            {
                so.FindProperty("m_centerPoseModel").objectReferenceValue = sentis;
            }
            else
            {
                Debug.LogWarning($"QuestObjectron: assign {SentisPath} after ONNX conversion.");
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            manager.WireReferences(cameraAccess, imageSource, environmentRaycast, questVisuals);
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
