// Headset menu: toggle placement coordinate fixes (combinable). Point + trigger to click on Quest.

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QuestObjectron
{
    public class ObjectronPlacementFixMenu : MonoBehaviour
    {
        private const float PanelLocalX = 0.42f;
        private const float PanelLocalY = 0.08f;
        private const float PanelLocalZ = 0.55f;

        private Canvas m_canvas;
        private Toggle m_toggleFix1;
        private Toggle m_toggleFix2;
        private Toggle m_toggleFix3;
        private Text m_statusText;
        private bool m_readyLogged;

        public static ObjectronPlacementFixMenu Instance { get; private set; }

        private void Awake()
        {
            EnsureActiveOptions();
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            EnsureEventSystem();
            BuildUi();
            SyncTogglesFromSettings();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            SyncTogglesFromSettings();
            UpdateStatusLabel();
            TryEditorKeyboardToggles();
        }

        private static void EnsureEventSystem()
        {
            var es = FindAnyObjectByType<EventSystem>();
            if (es != null)
            {
                EnsureOvrInputModule(es.gameObject);
                return;
            }

            var go = new GameObject("ObjectronEventSystem");
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

            var root = new GameObject("ObjectronPlacementFixMenu");
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
            panelRect.anchorMin = new Vector2(1f, 1f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 1f);
            panelRect.anchoredPosition = new Vector2(-20f, -20f);
            panelRect.sizeDelta = new Vector2(520f, 380f);

            var panelImage = panelGo.AddComponent<Image>();
            panelImage.color = new Color(0.05f, 0.08f, 0.12f, 0.92f);

            CreateLabel(panelGo.transform, "Placement fixes (tap to combine)", 28, new Vector2(-16f, -12f),
                new Vector2(-16f, -48f), FontStyle.Bold);

            m_toggleFix1 = CreateToggleRow(panelGo.transform, "Y-up camera frame", 0, OnFix1Changed);
            m_toggleFix2 = CreateToggleRow(panelGo.transform, "Mask if bad rotation", 1, OnFix2Changed);
            m_toggleFix3 = CreateToggleRow(panelGo.transform, "Mirror 3D local X", 2, OnFix3Changed);

            var statusGo = new GameObject("Status");
            statusGo.transform.SetParent(panelGo.transform, false);
            var statusRect = statusGo.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0f, 0f);
            statusRect.anchorMax = new Vector2(1f, 0f);
            statusRect.pivot = new Vector2(1f, 0f);
            statusRect.anchoredPosition = new Vector2(-16f, 16f);
            statusRect.sizeDelta = new Vector2(-32f, 72f);

            m_statusText = statusGo.AddComponent<Text>();
            m_statusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_statusText.fontSize = 22;
            m_statusText.color = new Color(0.75f, 0.95f, 1f, 1f);
            m_statusText.alignment = TextAnchor.LowerRight;
            m_statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            m_statusText.verticalOverflow = VerticalWrapMode.Overflow;

            if (!m_readyLogged)
            {
                m_readyLogged = true;
                QuestObjectronLogger.Boot("placement_fix_menu ready (point + trigger on toggles; editor keys 1/2/3)");
            }
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

        private Toggle CreateToggleRow(Transform parent, string label, int rowIndex, UnityEngine.Events.UnityAction<bool> onChanged)
        {
            var rowY = -72f - rowIndex * 88f;
            var rowGo = new GameObject($"Toggle_{rowIndex}");
            rowGo.transform.SetParent(parent, false);
            var rowRect = rowGo.AddComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchoredPosition = new Vector2(0f, rowY);
            rowRect.sizeDelta = new Vector2(-32f, 72f);

            var bg = rowGo.AddComponent<Image>();
            bg.color = new Color(0.15f, 0.18f, 0.22f, 0.95f);

            var toggleGo = new GameObject("Toggle");
            toggleGo.transform.SetParent(rowGo.transform, false);
            var toggleRect = toggleGo.AddComponent<RectTransform>();
            toggleRect.anchorMin = new Vector2(0f, 0.5f);
            toggleRect.anchorMax = new Vector2(0f, 0.5f);
            toggleRect.pivot = new Vector2(0f, 0.5f);
            toggleRect.anchoredPosition = new Vector2(16f, 0f);
            toggleRect.sizeDelta = new Vector2(52f, 52f);

            var toggle = toggleGo.AddComponent<Toggle>();
            toggle.targetGraphic = toggleGo.AddComponent<Image>();
            ((Image)toggle.targetGraphic).color = new Color(0.35f, 0.38f, 0.42f, 1f);

            var checkGo = new GameObject("Check");
            checkGo.transform.SetParent(toggleGo.transform, false);
            var checkRect = checkGo.AddComponent<RectTransform>();
            checkRect.anchorMin = Vector2.zero;
            checkRect.anchorMax = Vector2.one;
            checkRect.offsetMin = new Vector2(8f, 8f);
            checkRect.offsetMax = new Vector2(-8f, -8f);
            var checkImage = checkGo.AddComponent<Image>();
            checkImage.color = new Color(0.2f, 0.95f, 0.45f, 1f);
            toggle.graphic = checkImage;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(84f, 8f);
            labelRect.offsetMax = new Vector2(-12f, -8f);

            var labelText = labelGo.AddComponent<Text>();
            labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labelText.fontSize = 26;
            labelText.color = Color.white;
            labelText.alignment = TextAnchor.MiddleLeft;
            labelText.text = label;

            var rowButton = rowGo.AddComponent<Button>();
            rowButton.targetGraphic = bg;
            rowButton.onClick.AddListener(() =>
            {
                toggle.isOn = !toggle.isOn;
            });

            toggle.onValueChanged.AddListener(onChanged);
            return toggle;
        }

        private static void EnsureActiveOptions()
        {
            if (ObjectronPlacementFixSettings.Active != null)
            {
                return;
            }

            var manager = FindAnyObjectByType<ObjectronChairDetectionManager>();
            if (manager != null)
            {
                ObjectronPlacementFixSettings.Active = manager.PlacementOptions;
            }
        }

        private void SyncTogglesFromSettings()
        {
            EnsureActiveOptions();
            if (m_toggleFix1 == null)
            {
                return;
            }

            m_toggleFix1.SetIsOnWithoutNotify(ObjectronPlacementFixSettings.UseUnityCameraFrame);
            m_toggleFix2.SetIsOnWithoutNotify(ObjectronPlacementFixSettings.OrientationMaskFallback);
            m_toggleFix3.SetIsOnWithoutNotify(ObjectronPlacementFixSettings.Mirror3DWhenFlipped);
        }

        private void UpdateStatusLabel()
        {
            if (m_statusText == null)
            {
                return;
            }

            m_statusText.text = ObjectronPlacementFixSettings.Summary;
        }

        private void OnFix1Changed(bool on)
        {
            ObjectronPlacementFixSettings.UseUnityCameraFrame = on;
            LogChange();
        }

        private void OnFix2Changed(bool on)
        {
            ObjectronPlacementFixSettings.OrientationMaskFallback = on;
            LogChange();
        }

        private void OnFix3Changed(bool on)
        {
            ObjectronPlacementFixSettings.Mirror3DWhenFlipped = on;
            LogChange();
        }

        private static void LogChange()
        {
            QuestObjectronLogger.Boot($"placement_fix: {ObjectronPlacementFixSettings.Summary}");
        }

        private void TryEditorKeyboardToggles()
        {
#if UNITY_EDITOR
            if (Input.GetKeyDown(KeyCode.Alpha1))
            {
                ObjectronPlacementFixSettings.UseUnityCameraFrame =
                    !ObjectronPlacementFixSettings.UseUnityCameraFrame;
                LogChange();
            }

            if (Input.GetKeyDown(KeyCode.Alpha2))
            {
                ObjectronPlacementFixSettings.OrientationMaskFallback =
                    !ObjectronPlacementFixSettings.OrientationMaskFallback;
                LogChange();
            }

            if (Input.GetKeyDown(KeyCode.Alpha3))
            {
                ObjectronPlacementFixSettings.Mirror3DWhenFlipped =
                    !ObjectronPlacementFixSettings.Mirror3DWhenFlipped;
                LogChange();
            }
#endif
        }

        private static Transform FindEyeAnchor()
        {
#if UNITY_2023_1_OR_NEWER
            var rig = Object.FindAnyObjectByType<OVRCameraRig>();
#else
            var rig = Object.FindObjectOfType<OVRCameraRig>();
#endif
            if (rig != null && rig.centerEyeAnchor != null)
            {
                return rig.centerEyeAnchor;
            }

            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }
    }
}
