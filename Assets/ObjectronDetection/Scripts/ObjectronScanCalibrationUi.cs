// VR status panel for Scan Calibration (capture/clear + live detection hints).

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QuestObjectron
{
    public class ObjectronScanCalibrationUi : MonoBehaviour
    {
        private const float PanelLocalX = 0.42f;
        private const float PanelLocalY = 0.08f;
        private const float PanelLocalZ = 0.55f;

        [SerializeField] private ObjectronScanCalibrationManager m_manager;

        private Canvas m_canvas;
        private Text m_statusText;
        private bool m_uiBuilt;

        private void Awake()
        {
            m_manager ??= GetComponent<ObjectronScanCalibrationManager>()
                ?? FindAnyObjectByType<ObjectronScanCalibrationManager>();
            EnsureEventSystem();
        }

        private void Start() => TryBuildUi();

        private void Update()
        {
            if (!m_uiBuilt)
            {
                TryBuildUi();
            }

            UpdateStatus();
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

        private void UpdateStatus()
        {
            if (m_statusText == null || m_manager == null)
            {
                return;
            }

            m_statusText.text = m_manager.State switch
            {
                ScanCalibrationState.Booting => "Booting detection...",
                ScanCalibrationState.Ready => m_manager.HasLatchedCorners
                    ? $"Chair latched ({m_manager.LiveDetectionCount}) — press B to show box"
                    : "Point at chair — wait for live detect, then press B",
                ScanCalibrationState.BoxShown => "Box shown — trigger tap to spawn scan at box center",
                ScanCalibrationState.ScanSpawned =>
                    "Grip=move | Trigger=rotate | Both grips=scale | Left Y=freeze | Left X=save",
                ScanCalibrationState.ScanFrozen => "Frozen — Left X to save calibration",
                ScanCalibrationState.Saved => "Calibration saved",
                _ => "Ready",
            };
        }

        private static void EnsureEventSystem()
        {
            var es = FindAnyObjectByType<EventSystem>();
            if (es != null)
            {
                EnsureOvrInputModule(es.gameObject);
                return;
            }

            var go = new GameObject("ObjectronScanCalibrationEventSystem");
            go.AddComponent<EventSystem>();
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

            var root = new GameObject("ObjectronScanCalibrationUi");
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
            panelRect.sizeDelta = new Vector2(560f, 560f);

            var panelImage = panelGo.AddComponent<Image>();
            panelImage.color = new Color(0.05f, 0.08f, 0.12f, 0.92f);

            CreateLabel(panelGo.transform, "Scan Calibration", 30, new Vector2(16f, -12f), new Vector2(-16f, -52f), FontStyle.Bold);
            CreateButtonRow(panelGo.transform, "Show Box — Right B", 0, () => m_manager?.CaptureAndDetect());
            CreateButtonRow(panelGo.transform, "Clear Box — Right A", 1, () => m_manager?.ClearDetectionBox());
            CreateButtonRow(panelGo.transform, "Spawn Scan — Right Trigger", 2, () => m_manager?.TrySpawnScan());
            CreateButtonRow(panelGo.transform, "Freeze — Left Y", 3, () => m_manager?.TryFreezeScan());
            CreateButtonRow(panelGo.transform, "Save — Left X", 4, () => m_manager?.TrySaveCalibration());

            var statusGo = new GameObject("Status");
            statusGo.transform.SetParent(panelGo.transform, false);
            var statusRect = statusGo.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0f, 0f);
            statusRect.anchorMax = new Vector2(1f, 0f);
            statusRect.pivot = new Vector2(0f, 0f);
            statusRect.anchoredPosition = new Vector2(16f, 16f);
            statusRect.sizeDelta = new Vector2(-32f, 120f);

            m_statusText = statusGo.AddComponent<Text>();
            m_statusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_statusText.fontSize = 22;
            m_statusText.color = new Color(0.75f, 0.95f, 1f, 1f);
            m_statusText.alignment = TextAnchor.LowerLeft;
            m_statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            m_statusText.verticalOverflow = VerticalWrapMode.Overflow;
            m_statusText.text = "Booting...";
        }

        private static Text CreateLabel(
            Transform parent,
            string text,
            int fontSize,
            Vector2 offsetMin,
            Vector2 offsetMax,
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

        private static void CreateButtonRow(Transform parent, string label, int rowIndex, UnityEngine.Events.UnityAction onClick)
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
        }

        private static Transform FindEyeAnchor()
        {
            var rig = FindAnyObjectByType<OVRCameraRig>();
            return rig != null ? rig.centerEyeAnchor : Camera.main?.transform;
        }
    }
}
