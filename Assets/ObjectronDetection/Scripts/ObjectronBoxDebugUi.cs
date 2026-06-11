// VR UI for Box Debug scene: capture, localize, clear.

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QuestObjectron
{
    public class ObjectronBoxDebugUi : MonoBehaviour
    {
        private const float PanelLocalX = -0.42f;
        private const float PanelLocalY = 0.08f;
        private const float PanelLocalZ = 0.55f;

        [SerializeField] private ObjectronBoxDebugManager m_manager;

        private Canvas m_canvas;
        private Text m_statusText;
        private Button m_captureButton;
        private Button m_localizeButton;
        private Button m_clearButton;

        private bool m_uiBuilt;

        private void Awake()
        {
            if (m_manager == null)
            {
                m_manager = FindAnyObjectByType<ObjectronBoxDebugManager>();
            }

            EnsureEventSystem();
        }

        private void Start()
        {
            TryBuildUi();
        }

        private void Update()
        {
            if (!m_uiBuilt)
            {
                TryBuildUi();
            }

            UpdateStatus();
            UpdateButtonStates();
        }

        private void TryBuildUi()
        {
            if (m_uiBuilt)
            {
                return;
            }

            BuildUi();
            m_uiBuilt = m_canvas != null;
        }

        public void OnCaptureClicked()
        {
            m_manager?.CaptureAndDetect();
        }

        public void OnLocalizeClicked()
        {
            m_manager?.Localize();
        }

        public void OnClearClicked()
        {
            m_manager?.ClearLocalization();
        }

        private void UpdateStatus()
        {
            if (m_statusText == null || m_manager == null)
            {
                return;
            }

            m_statusText.text = m_manager.State switch
            {
                BoxDebugState.Booting => "Booting...",
                BoxDebugState.Ready => m_manager.LiveDetectionCount > 0
                    ? $"Cup latched ({m_manager.LiveDetectionCount}) — Capture & Detect or B"
                    : "Point at mug — wait for live detect, then Capture & Detect or B",
                BoxDebugState.Detected => "Box shown — compare, then Localize",
                BoxDebugState.NoDetection => "No cup latched yet — point at mug and retry",
                BoxDebugState.Localized => "Box pinned — A or Clear to remove",
                _ => "Ready",
            };
        }

        private void UpdateButtonStates()
        {
            if (m_manager == null)
            {
                return;
            }

            if (m_captureButton != null)
            {
                m_captureButton.interactable = m_manager.IsReady && m_manager.State != BoxDebugState.Localized;
            }

            if (m_localizeButton != null)
            {
                m_localizeButton.interactable = m_manager.CanLocalize;
            }

            if (m_clearButton != null)
            {
                m_clearButton.interactable = m_manager.CanClear;
            }
        }

        private static void EnsureEventSystem()
        {
            var es = FindAnyObjectByType<EventSystem>();
            if (es != null)
            {
                EnsureOvrInputModule(es.gameObject);
                return;
            }

            var go = new GameObject("ObjectronBoxDebugEventSystem");
            es = go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
            EnsureOvrInputModule(go);
        }

        private static void EnsureOvrInputModule(GameObject eventSystemGo)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (eventSystemGo.GetComponent<OVRInputModule>() == null)
            {
                var standalone = eventSystemGo.GetComponent<StandaloneInputModule>();
                if (standalone != null)
                {
                    Destroy(standalone);
                }

                eventSystemGo.AddComponent<OVRInputModule>();
            }
#endif
        }

        private void BuildUi()
        {
            var anchor = FindEyeAnchor();
            if (anchor == null)
            {
                return;
            }

            var root = new GameObject("ObjectronBoxDebugUi");
            root.transform.SetParent(anchor, false);
            root.transform.localPosition = new Vector3(PanelLocalX, PanelLocalY, PanelLocalZ);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            m_canvas = root.AddComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceCamera;
            m_canvas.worldCamera = anchor.GetComponent<Camera>() ?? Camera.main;
            m_canvas.planeDistance = PanelLocalZ;
            m_canvas.sortingOrder = 10000;
            m_canvas.overrideSorting = true;

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            root.AddComponent<GraphicRaycaster>();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (root.GetComponent<OVRRaycaster>() == null)
            {
                root.AddComponent<OVRRaycaster>();
            }
#endif

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(root.transform, false);
            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(20f, -20f);
            panelRect.sizeDelta = new Vector2(520f, 420f);

            var panelImage = panelGo.AddComponent<Image>();
            panelImage.color = new Color(0.05f, 0.08f, 0.12f, 0.92f);

            CreateLabel(panelGo.transform, "Box Debug", 30, new Vector2(16f, -12f), new Vector2(-16f, -52f), FontStyle.Bold);

            m_captureButton = CreateButtonRow(panelGo.transform, "Capture & Detect", 0, OnCaptureClicked);
            m_localizeButton = CreateButtonRow(panelGo.transform, "Localize Box", 1, OnLocalizeClicked);
            m_clearButton = CreateButtonRow(panelGo.transform, "Clear Box", 2, OnClearClicked);

            var statusGo = new GameObject("Status");
            statusGo.transform.SetParent(panelGo.transform, false);
            var statusRect = statusGo.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0f, 0f);
            statusRect.anchorMax = new Vector2(1f, 0f);
            statusRect.pivot = new Vector2(0f, 0f);
            statusRect.anchoredPosition = new Vector2(16f, 16f);
            statusRect.sizeDelta = new Vector2(-32f, 88f);

            m_statusText = statusGo.AddComponent<Text>();
            m_statusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_statusText.fontSize = 22;
            m_statusText.color = new Color(0.75f, 0.95f, 1f, 1f);
            m_statusText.alignment = TextAnchor.LowerLeft;
            m_statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            m_statusText.verticalOverflow = VerticalWrapMode.Overflow;
            m_statusText.text = "Booting...";
        }

        private static Text CreateLabel(Transform parent, string text, int fontSize, Vector2 offsetMin, Vector2 offsetMax,
            FontStyle style)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var label = go.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = Color.white;
            label.alignment = TextAnchor.UpperLeft;
            label.text = text;
            return label;
        }

        private Button CreateButtonRow(Transform parent, string label, int rowIndex, UnityEngine.Events.UnityAction onClick)
        {
            var rowY = -72f - rowIndex * 88f;
            var rowGo = new GameObject($"Button_{rowIndex}");
            rowGo.transform.SetParent(parent, false);
            var rowRect = rowGo.AddComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchoredPosition = new Vector2(0f, rowY);
            rowRect.sizeDelta = new Vector2(-32f, 72f);

            var bg = rowGo.AddComponent<Image>();
            bg.color = new Color(0.15f, 0.18f, 0.22f, 0.95f);

            var button = rowGo.AddComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(onClick);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(16f, 8f);
            labelRect.offsetMax = new Vector2(-16f, -8f);

            var labelText = labelGo.AddComponent<Text>();
            labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labelText.fontSize = 26;
            labelText.color = Color.white;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.text = label;

            return button;
        }

        private static Transform FindEyeAnchor()
        {
            var rig = FindAnyObjectByType<OVRCameraRig>();
            return rig != null ? rig.centerEyeAnchor : Camera.main?.transform;
        }
    }
}
