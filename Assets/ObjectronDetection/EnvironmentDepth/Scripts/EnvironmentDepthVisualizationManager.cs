// Real-time environment depth on Quest 3 — fullscreen UI over passthrough (world quads render behind PT).
using System.Collections;
using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.UI;

namespace QuestObjectron
{
    [DefaultExecutionOrder(-1000)]
    public class EnvironmentDepthVisualizationManager : MonoBehaviour
    {
        private const float CanvasPlaneDistance = 0.12f;
        private const string LogTag = "QuestObj3D";

        [SerializeField] private Material m_depthMaterial;
        [SerializeField] private bool m_usePreprocessedTexture = true;
        [SerializeField] private bool m_requestScenePermissionOnStart = true;
        [SerializeField] private bool m_dimPassthroughWhileViewingDepth = true;
        [SerializeField] [Range(0f, 1f)] private float m_passthroughAlphaWhileViewing = 0.15f;

        private EnvironmentDepthManager m_depthManager;
        private Canvas m_canvas;
        private Image m_depthImage;
        private Text m_statusLabel;
        private OVRPassthroughLayer m_passthroughLayer;
        private float m_savedPassthroughOpacity = 1f;
        private bool m_depthPipelineActive;
        private bool m_tornDown;

        private void Awake()
        {
            EnvironmentDepthSceneBootstrap.StripCameraPreview();
        }

        private void Start()
        {
            StartCoroutine(StartDepthVisualization());
        }

        private IEnumerator StartDepthVisualization()
        {
            EnvironmentDepthSceneBootstrap.StripCameraPreview();
            UpdateStatus("Starting depth…");

            if (m_requestScenePermissionOnStart)
            {
                yield return RequestScenePermissionCoroutine();
            }

            EnvironmentDepthSupport.LogBootDiagnostics();

            if (!EnvironmentDepthManager.IsSupported)
            {
                var reason = EnvironmentDepthSupport.BuildUnsupportedReason();
                UpdateStatus("Depth API not supported — see logcat BOOT line.");
                Debug.LogWarning($"{LogTag}: {reason}");
                yield break;
            }

            m_depthPipelineActive = true;

            m_depthManager = gameObject.GetComponent<EnvironmentDepthManager>();
            if (m_depthManager == null)
            {
                m_depthManager = gameObject.AddComponent<EnvironmentDepthManager>();
            }

            m_depthManager.enabled = true;
            m_depthManager.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
            m_depthManager.RemoveHands = false;

            UpdateStatus("Waiting for depth texture…");
            var timeout = Time.realtimeSinceStartup + 25f;
            while (m_depthManager != null && !m_depthManager.IsDepthAvailable && Time.realtimeSinceStartup < timeout)
            {
                yield return null;
            }

            if (m_depthManager == null || !m_depthManager.IsDepthAvailable)
            {
                UpdateStatus("No depth. Settings → app → enable Scene/Spatial data.");
                Debug.LogWarning($"{LogTag}: depth IsDepthAvailable=false");
                yield break;
            }

            LogGlobalDepthTextures();
            CreateFullscreenUi();
            if (m_dimPassthroughWhileViewingDepth)
            {
                DimPassthrough();
            }

            UpdateStatus("Depth ON — A/B=toggle source — Menu=back");
            Debug.Log($"{LogTag}: depth_ui_ready preprocessed={m_usePreprocessedTexture}");
        }

        private static void LogGlobalDepthTextures()
        {
            var pre = Shader.GetGlobalTexture("_PreprocessedEnvironmentDepthTexture");
            var raw = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
            Debug.Log($"{LogTag}: depth_tex preprocessed={(pre != null ? pre.name : "null")} raw={(raw != null ? raw.name : "null")}");
        }

        private void CreateFullscreenUi()
        {
            var eye = FindCenterEye();
            if (eye == null)
            {
                UpdateStatus("No CenterEyeAnchor.");
                return;
            }

            var cam = eye.GetComponent<Camera>() ?? Camera.main;
            if (cam == null)
            {
                UpdateStatus("No center eye camera.");
                return;
            }

            EnsureMaterial();

            if (m_canvas == null)
            {
                var root = new GameObject("EnvironmentDepthCanvas");
                root.transform.SetParent(eye, false);
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;

                m_canvas = root.AddComponent<Canvas>();
                m_canvas.renderMode = RenderMode.ScreenSpaceCamera;
                m_canvas.worldCamera = cam;
                m_canvas.planeDistance = CanvasPlaneDistance;
                m_canvas.sortingOrder = 32000;
                m_canvas.overrideSorting = true;

                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;

                root.AddComponent<GraphicRaycaster>();

                var imageGo = new GameObject("DepthImage");
                imageGo.transform.SetParent(root.transform, false);
                var imageRt = imageGo.AddComponent<RectTransform>();
                imageRt.anchorMin = Vector2.zero;
                imageRt.anchorMax = Vector2.one;
                imageRt.offsetMin = Vector2.zero;
                imageRt.offsetMax = Vector2.zero;

                m_depthImage = imageGo.AddComponent<Image>();
                m_depthImage.raycastTarget = false;
                m_depthImage.color = Color.white;

                var statusGo = new GameObject("DepthStatus");
                statusGo.transform.SetParent(root.transform, false);
                var statusRt = statusGo.AddComponent<RectTransform>();
                statusRt.anchorMin = new Vector2(0.02f, 0.82f);
                statusRt.anchorMax = new Vector2(0.98f, 0.98f);
                statusRt.offsetMin = Vector2.zero;
                statusRt.offsetMax = Vector2.zero;

                m_statusLabel = statusGo.AddComponent<Text>();
                m_statusLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                m_statusLabel.fontSize = 28;
                m_statusLabel.alignment = TextAnchor.UpperLeft;
                m_statusLabel.color = Color.white;
                m_statusLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
                m_statusLabel.verticalOverflow = VerticalWrapMode.Overflow;

                var statusBg = statusGo.AddComponent<Image>();
                statusBg.color = new Color(0f, 0f, 0f, 0.65f);
                statusBg.raycastTarget = false;
            }

            m_depthImage.material = m_depthMaterial;
            ApplyMaterialMode();
            Debug.Log($"{LogTag}: depth_canvas plane={CanvasPlaneDistance} sort={m_canvas.sortingOrder}");
        }

        private void EnsureMaterial()
        {
            if (m_depthMaterial != null)
            {
                return;
            }

            var shader = Shader.Find("QuestObjectron/EnvironmentDepthVisualizationUI");
            if (shader == null)
            {
                shader = Shader.Find("QuestObjectron/EnvironmentDepthVisualization");
            }

            if (shader == null)
            {
                Debug.LogError($"{LogTag}: depth UI shader not found in build");
                return;
            }

            m_depthMaterial = new Material(shader);
        }

        private void ApplyMaterialMode()
        {
            if (m_depthMaterial == null)
            {
                return;
            }

            m_depthMaterial.SetFloat("_UsePreprocessed", m_usePreprocessedTexture ? 1f : 0f);
            if (m_depthImage != null)
            {
                m_depthImage.material = m_depthMaterial;
            }
        }

        private void DimPassthrough()
        {
#if UNITY_2023_1_OR_NEWER
            m_passthroughLayer = UnityEngine.Object.FindAnyObjectByType<OVRPassthroughLayer>();
#else
            m_passthroughLayer = UnityEngine.Object.FindObjectOfType<OVRPassthroughLayer>();
#endif
            if (m_passthroughLayer == null)
            {
                return;
            }

            m_savedPassthroughOpacity = m_passthroughLayer.textureOpacity;
            m_passthroughLayer.textureOpacity = m_passthroughAlphaWhileViewing;
            Debug.Log($"{LogTag}: passthrough_dim opacity={m_passthroughAlphaWhileViewing}");
        }

        private void RestorePassthrough()
        {
            if (m_passthroughLayer != null)
            {
                m_passthroughLayer.textureOpacity = m_savedPassthroughOpacity;
            }
        }

        private void Update()
        {
            if (!m_depthPipelineActive || !WasTogglePressed())
            {
                return;
            }

            m_usePreprocessedTexture = !m_usePreprocessedTexture;
            ApplyMaterialMode();
            UpdateStatus(m_usePreprocessedTexture
                ? "Source: preprocessed depth"
                : "Source: raw environment depth");
            Debug.Log($"{LogTag}: depth_toggle preprocessed={m_usePreprocessedTexture}");
        }

        private static bool WasTogglePressed()
        {
            return OVRInput.GetDown(OVRInput.Button.One)
                   || OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)
                   || OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)
                   || OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)
                   || OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch);
        }

        public void ShutdownForSceneExit()
        {
            if (m_tornDown)
            {
                return;
            }

            m_tornDown = true;
            m_depthPipelineActive = false;
            StopAllCoroutines();
            RestorePassthrough();
            DestroyDepthUi();
            DestroyDepthManager();
        }

        private void DestroyDepthUi()
        {
            if (m_canvas != null)
            {
                Destroy(m_canvas.gameObject);
                m_canvas = null;
                m_depthImage = null;
                m_statusLabel = null;
            }
        }

        private void DestroyDepthManager()
        {
            if (m_depthManager == null)
            {
                return;
            }

            m_depthManager.enabled = false;
            Destroy(m_depthManager);
            m_depthManager = null;
        }

        private void OnDestroy()
        {
            ShutdownForSceneExit();
            if (m_depthMaterial != null && m_depthMaterial.shader != null
                && m_depthMaterial.shader.name.StartsWith("QuestObjectron/"))
            {
                Destroy(m_depthMaterial);
            }
        }

        private void UpdateStatus(string message)
        {
            if (m_statusLabel != null)
            {
                m_statusLabel.text = message;
                return;
            }

            Debug.Log($"{LogTag}: depth_status {message}");
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
                $"depth_scene={(OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene) ? "granted" : "denied")}");
#else
            yield break;
#endif
        }

        private static Transform FindCenterEye()
        {
#if UNITY_2023_1_OR_NEWER
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
#else
            var rig = UnityEngine.Object.FindObjectOfType<OVRCameraRig>();
#endif
            return rig != null && rig.centerEyeAnchor != null ? rig.centerEyeAnchor : Camera.main?.transform;
        }
    }
}
