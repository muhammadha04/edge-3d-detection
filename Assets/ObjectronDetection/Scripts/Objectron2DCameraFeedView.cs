// World-space PCA camera feed (same layout as PassthroughCameraApiSamples/CameraViewer).

using System.Collections;
using Mediapipe.Unity;
using Meta.XR;
using UnityEngine;
using UnityEngine.UI;

namespace QuestObjectron
{
    public class Objectron2DCameraFeedView : MonoBehaviour
    {
        private const float DefaultPanelDistance = 1.15f;
        private static readonly Vector2 DefaultPanelSize = new(1280f, 960f);

        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughImageSource m_imageSource;
        [SerializeField] private RawImage m_image;
        [SerializeField] private float m_panelDistance = DefaultPanelDistance;
        [SerializeField] private Vector2 m_panelSize = DefaultPanelSize;
        [SerializeField] [Range(0.2f, 1f)] private float m_feedAlpha = 0.92f;

        public RawImage Image => m_image;
        public RectTransform FeedRect => m_image != null ? m_image.rectTransform : null;

        public void BindImageSource(PassthroughImageSource imageSource)
        {
            m_imageSource = imageSource;
            ApplyInferenceDisplayTransform();
        }

        private void ApplyInferenceDisplayTransform()
        {
            if (m_image == null || m_imageSource == null)
            {
                return;
            }

            // Match MediaPipe Screen preview so boxes align with the inference frame.
            m_image.rectTransform.localEulerAngles = m_imageSource.rotation.Reverse().GetEulerAngles();
        }

        private void Awake()
        {
            if (m_cameraAccess == null)
            {
                m_cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
            }
        }

        private IEnumerator Start()
        {
            if (m_image == null)
            {
                EnsureWorldCanvas();
            }

            if (m_cameraAccess == null)
            {
                QuestObjectronLogger.Err("2d_feed no PassthroughCameraAccess");
                yield break;
            }

            while (!m_cameraAccess.IsPlaying)
            {
                yield return null;
            }

            if (m_image != null)
            {
                ApplyInferenceDisplayTransform();
                m_image.texture = m_cameraAccess.GetTexture();
                var color = m_image.color;
                color.a = m_feedAlpha;
                m_image.color = color;
                QuestObjectronLogger.Viz(
                    $"2d_feed bound tex={m_image.texture?.width}x{m_image.texture?.height} dist={m_panelDistance:F2}");
            }
        }

        public void EnsureWorldCanvas()
        {
            if (m_image != null)
            {
                return;
            }

            var anchor = FindEyeAnchor();
            if (anchor == null)
            {
                QuestObjectronLogger.Err("2d_feed no CenterEyeAnchor — cannot create camera canvas");
                return;
            }

            var root = new GameObject("Objectron2DCameraFeed");
            root.transform.SetParent(anchor, false);
            root.transform.localPosition = new Vector3(0f, 0f, m_panelDistance);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one * 0.001f;

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var canvasRt = root.GetComponent<RectTransform>();
            canvasRt.sizeDelta = m_panelSize;

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            root.AddComponent<GraphicRaycaster>();

            var feedGo = new GameObject("RawImage");
            feedGo.transform.SetParent(root.transform, false);
            var feedRt = feedGo.AddComponent<RectTransform>();
            feedRt.anchorMin = new Vector2(0.5f, 0.5f);
            feedRt.anchorMax = new Vector2(0.5f, 0.5f);
            feedRt.pivot = new Vector2(0.5f, 0.5f);
            feedRt.sizeDelta = m_panelSize;
            feedRt.anchoredPosition = Vector2.zero;

            m_image = feedGo.AddComponent<RawImage>();
            m_image.raycastTarget = false;
            m_image.color = new UnityEngine.Color(1f, 1f, 1f, m_feedAlpha);
        }

        private static Transform FindEyeAnchor()
        {
            var rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null)
            {
                return rig.centerEyeAnchor;
            }

            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }
    }
}
