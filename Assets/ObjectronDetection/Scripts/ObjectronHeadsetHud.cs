// On-headset debug overlay (Screen Space Camera on CenterEyeAnchor).

using System.Text;
using Mediapipe.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace QuestObjectron
{
    public readonly struct ObjectronHudSnapshot
    {
        public readonly string DetectState;
        public readonly string RotationLabel;
        public readonly Vector3? WorldCenter;
        public readonly float ExtentMeters;
        public readonly float DistHmd;
        public readonly int QuestVisualsCount;
        public readonly int ObjectId;
        public readonly string PlacementMethod;
        public readonly int FrameId;
        public readonly string PcaResolution;
        public readonly string Hint;
        public readonly bool Overlay2dActive;
        public readonly bool DepthInBoxEnabled;
        public readonly bool DepthInBoxReady;

        public ObjectronHudSnapshot(
            string detectState,
            string rotationLabel,
            Vector3? worldCenter,
            float extentMeters,
            float distHmd,
            int questVisualsCount,
            int objectId,
            string placementMethod,
            int frameId,
            string pcaResolution,
            string hint,
            bool overlay2dActive = false,
            bool depthInBoxEnabled = false,
            bool depthInBoxReady = false)
        {
            DetectState = detectState;
            RotationLabel = rotationLabel;
            WorldCenter = worldCenter;
            ExtentMeters = extentMeters;
            DistHmd = distHmd;
            QuestVisualsCount = questVisualsCount;
            ObjectId = objectId;
            PlacementMethod = placementMethod;
            FrameId = frameId;
            PcaResolution = pcaResolution;
            Hint = hint;
            Overlay2dActive = overlay2dActive;
            DepthInBoxEnabled = depthInBoxEnabled;
            DepthInBoxReady = depthInBoxReady;
        }
    }

    public class ObjectronHeadsetHud : MonoBehaviour
    {
        private const float HudLocalZ = 0.55f;
        private const float HudLocalY = -0.06f;
        private const int FontSize = 36;

        private readonly StringBuilder m_text = new();
        private Canvas m_canvas;
        private Text m_label;
        private bool m_readyLogged;
        private string m_lastRendered = "";

        public void Apply(ObjectronHudSnapshot snapshot)
        {
            EnsureUi();
            if (m_label == null)
            {
                return;
            }

            m_text.Clear();
            m_text.AppendLine($"detect: {snapshot.DetectState}");
            m_text.AppendLine($"rot: {snapshot.RotationLabel}");
            if (snapshot.WorldCenter.HasValue)
            {
                var c = snapshot.WorldCenter.Value;
                m_text.AppendLine($"center: ({c.x:F2}, {c.y:F2}, {c.z:F2})");
                m_text.AppendLine($"extent: {snapshot.ExtentMeters:F3}m  dist: {snapshot.DistHmd:F2}m");
            }
            else
            {
                m_text.AppendLine("center: —");
            }

            m_text.AppendLine(
                $"viz: {snapshot.QuestVisualsCount}  2d: {(snapshot.Overlay2dActive ? "on" : "off")}");
            m_text.AppendLine(
                $"depth_in_box: {(snapshot.DepthInBoxEnabled ? (snapshot.DepthInBoxReady ? "on ready" : "on waiting") : "off")}");
            m_text.AppendLine($"id: {snapshot.ObjectId}  place: {snapshot.PlacementMethod}");
            m_text.AppendLine(ObjectronPlacementFixSettings.Summary);
            m_text.AppendLine("log: PLACEMENT_DEBUG  BOX_PROJ_DEBUG  ORIENT_GATE");
            m_text.AppendLine($"frame: {snapshot.FrameId}  pca: {snapshot.PcaResolution}");
            if (!string.IsNullOrEmpty(snapshot.Hint))
            {
                m_text.AppendLine(snapshot.Hint);
            }

            var body = m_text.ToString();
            if (body == m_lastRendered)
            {
                return;
            }

            m_lastRendered = body;
            m_label.text = body;
        }

        private void EnsureUi()
        {
            if (m_canvas != null)
            {
                if (!m_canvas.enabled)
                {
                    m_canvas.enabled = true;
                }

                if (m_canvas.worldCamera == null)
                {
                    var eyeAnchor = FindEyeAnchor();
                    m_canvas.worldCamera = eyeAnchor != null
                        ? eyeAnchor.GetComponent<Camera>() ?? Camera.main
                        : Camera.main;
                }

                return;
            }

            var anchor = FindEyeAnchor();
            if (anchor == null)
            {
                return;
            }

            var hudRoot = new GameObject("ObjectronHeadsetHud");
            hudRoot.transform.SetParent(anchor, false);
            hudRoot.transform.localPosition = new Vector3(0f, HudLocalY, HudLocalZ);
            hudRoot.transform.localRotation = Quaternion.identity;
            hudRoot.transform.localScale = Vector3.one;

            m_canvas = hudRoot.AddComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceCamera;
            m_canvas.worldCamera = anchor.GetComponent<Camera>() ?? Camera.main;
            m_canvas.planeDistance = HudLocalZ;
            m_canvas.sortingOrder = 9999;
            m_canvas.enabled = true;
            m_canvas.overrideSorting = true;

            var scaler = hudRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            hudRoot.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(hudRoot.transform, false);
            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(24f, -24f);
            panelRect.sizeDelta = new Vector2(920f, 520f);

            var panelImage = panelGo.AddComponent<Image>();
            panelImage.color = new Color(0f, 0f, 0f, 0.82f);

            var textGo = new GameObject("HudText");
            textGo.transform.SetParent(panelGo.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(16f, 12f);
            textRect.offsetMax = new Vector2(-16f, -12f);

            m_label = textGo.AddComponent<Text>();
            m_label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_label.fontSize = FontSize;
            m_label.color = Color.white;
            m_label.alignment = TextAnchor.UpperLeft;
            m_label.horizontalOverflow = HorizontalWrapMode.Wrap;
            m_label.verticalOverflow = VerticalWrapMode.Overflow;
            m_label.supportRichText = false;
            m_label.text = "QuestObj3D HUD…";

            if (!m_readyLogged)
            {
                m_readyLogged = true;
                QuestObjectronLogger.LogHud($"ready parent={anchor.name}");
            }
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
