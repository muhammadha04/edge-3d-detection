// Enables Meta environment depth for in-box UI visualization on the Objectron overlay.
using System.Collections;
using Meta.XR.EnvironmentDepth;
using UnityEngine;

namespace QuestObjectron
{
    [DefaultExecutionOrder(-900)]
    public class ObjectronEnvironmentDepthProvider : MonoBehaviour
    {
        private const string InBoxShaderName = "QuestObjectron/EnvironmentDepthInBoxUI";
        private const string InBoxMaterialResourcePath = "QuestObjectron/EnvironmentDepthInBox";

        [SerializeField] private bool m_usePreprocessed = true;
        [SerializeField] private bool m_requestScenePermissionOnStart = true;
        [SerializeField] [Range(0f, 1f)] private float m_passthroughDim = 0f;

        private EnvironmentDepthManager m_depthManager;
        private Material m_inBoxMaterialTemplate;
        private bool m_pipelineStarted;
        private bool m_loggedReady;
        private bool m_loggedFailed;

        public bool UsePreprocessed
        {
            get => m_usePreprocessed;
            set => m_usePreprocessed = value;
        }

        public bool ShowDepthInBoxes { get; set; } = true;

        public bool IsDepthReady => m_depthManager != null && m_depthManager.IsDepthAvailable;

        private void OnEnable()
        {
            m_shutdownForSceneExit = false;
        }

        private void Start()
        {
            if (!m_shutdownForSceneExit)
            {
                StartCoroutine(StartDepthPipeline());
            }
        }

        public void ShutdownForSceneExit()
        {
            m_shutdownForSceneExit = true;
            StopAllCoroutines();
            m_pipelineStarted = false;
            m_loggedReady = false;
            m_loggedFailed = false;
            if (m_depthManager != null)
            {
                m_depthManager.enabled = false;
                Destroy(m_depthManager);
                m_depthManager = null;
            }
        }

        private void OnDestroy()
        {
            ShutdownForSceneExit();
        }

        private bool m_shutdownForSceneExit;

        private IEnumerator StartDepthPipeline()
        {
            if (m_pipelineStarted)
            {
                yield break;
            }

            m_pipelineStarted = true;

            if (m_requestScenePermissionOnStart)
            {
                yield return RequestScenePermissionCoroutine();
            }

            EnvironmentDepthSupport.LogBootDiagnostics();

            if (!EnvironmentDepthManager.IsSupported)
            {
                LogFailedOnce(EnvironmentDepthSupport.BuildUnsupportedReason());
                yield break;
            }

            m_depthManager = GetComponent<EnvironmentDepthManager>();
            if (m_depthManager == null)
            {
                m_depthManager = gameObject.AddComponent<EnvironmentDepthManager>();
            }

            m_depthManager.enabled = true;
            m_depthManager.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
            m_depthManager.RemoveHands = false;

            var timeout = Time.realtimeSinceStartup + 25f;
            while (m_depthManager != null && !m_depthManager.IsDepthAvailable && Time.realtimeSinceStartup < timeout)
            {
                yield return null;
            }

            if (m_depthManager == null || !m_depthManager.IsDepthAvailable)
            {
                LogFailedOnce("depth IsDepthAvailable=false — enable Scene/Spatial data for the app");
                yield break;
            }

            if (!m_loggedReady)
            {
                m_loggedReady = true;
                var pre = Shader.GetGlobalTexture("_PreprocessedEnvironmentDepthTexture");
                var raw = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
                QuestObjectronLogger.Viz(
                    $"depth_in_box ready preprocessed={m_usePreprocessed} pre_tex={(pre != null)} raw_tex={(raw != null)}");
            }
        }

        public Material CreateInBoxMaterial()
        {
            var template = ResolveInBoxMaterialTemplate();
            if (template != null)
            {
                var material = new Material(template);
                ApplyToMaterial(material);
                return material;
            }

            var shader = Shader.Find(InBoxShaderName);
            if (shader == null)
            {
                QuestObjectronLogger.Err(
                    "depth_in_box shader not in build — ensure Resources/QuestObjectron/EnvironmentDepthInBox.mat exists and rebuild");
                return null;
            }

            var fallback = new Material(shader);
            ApplyToMaterial(fallback);
            return fallback;
        }

        private Material ResolveInBoxMaterialTemplate()
        {
            if (m_inBoxMaterialTemplate != null)
            {
                return m_inBoxMaterialTemplate;
            }

            m_inBoxMaterialTemplate = Resources.Load<Material>(InBoxMaterialResourcePath);
            return m_inBoxMaterialTemplate;
        }

        public void ApplyToMaterial(Material material)
        {
            if (material == null)
            {
                return;
            }

            material.SetFloat("_UsePreprocessed", m_usePreprocessed ? 1f : 0f);
            material.SetFloat("_PassthroughDim", m_passthroughDim);
        }

        public void ToggleDepthSource()
        {
            m_usePreprocessed = !m_usePreprocessed;
            QuestObjectronLogger.Viz($"depth_in_box source preprocessed={m_usePreprocessed}");
        }

        private void LogFailedOnce(string reason)
        {
            if (m_loggedFailed)
            {
                return;
            }

            m_loggedFailed = true;
            QuestObjectronLogger.Err($"depth_in_box failed: {reason}");
        }

        private static IEnumerator RequestScenePermissionCoroutine()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string androidScene = "com.oculus.permission.USE_SCENE";
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(androidScene))
            {
                UnityEngine.Android.Permission.RequestUserPermission(androidScene);
            }

            var waitUntil = Time.realtimeSinceStartup + 10f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(androidScene)
                   && Time.realtimeSinceStartup < waitUntil)
            {
                yield return null;
            }

            if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene))
            {
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });
                waitUntil = Time.realtimeSinceStartup + 10f;
                while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene)
                       && Time.realtimeSinceStartup < waitUntil)
                {
                    yield return null;
                }
            }

            QuestObjectronLogger.Perm(
                $"depth_in_box_scene={(OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene) ? "granted" : "denied")}");
#else
            yield break;
#endif
        }
    }
}
