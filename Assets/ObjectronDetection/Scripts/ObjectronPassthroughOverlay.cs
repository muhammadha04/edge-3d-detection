// 2D detection overlay for Quest passthrough Objectron.
//
// Meta PassthroughCameraApiSamples/MultiObjectDetection uses SentisInferenceUiManager:
//   - Pooled RectTransform prefabs (Image border + Text) parented under a spatial-anchor root
//   - Normalized model boxes → ViewportPointToRay(normUV, cameraPose) → world-space UI quads
//   - See: Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceUiManager.cs
//
// CameraViewer shows the PCA texture on a RawImage (no boxes). We draw boxes in screen space first
// so the user always sees feedback when multi_box_rects fires, then enable 3D markers separately.
//
// Mapping: MediaPipe NormalizedRect (0–1, top-left origin) → PCA viewport (Y flipped) →
// CenterEye Screen Space Camera canvas via WorldToScreenPoint (same viewport path as Meta).

using System.Collections.Generic;
using Mediapipe;
using Meta.XR;
using UnityEngine;
using UnityEngine.UI;

namespace QuestObjectron
{
    public readonly struct ObjectronOverlayRect
    {
        public readonly float XCenter;
        public readonly float YCenter;
        public readonly float Width;
        public readonly float Height;

        public ObjectronOverlayRect(float xCenter, float yCenter, float width, float height)
        {
            XCenter = xCenter;
            YCenter = yCenter;
            Width = width;
            Height = height;
        }

        public static ObjectronOverlayRect From(NormalizedRect r) =>
            new(r.XCenter, r.YCenter, r.Width, r.Height);
    }

    public readonly struct ObjectronOverlayLandmark
    {
        public readonly float X;
        public readonly float Y;

        public ObjectronOverlayLandmark(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    public sealed class ObjectronOverlayFrame
    {
        public readonly List<ObjectronOverlayRect> Rects = new();
        public readonly List<List<ObjectronOverlayLandmark>> LandmarkLists = new();
    }

    public class ObjectronPassthroughOverlay : MonoBehaviour
    {
        private const float OverlayPlaneDistance = 0.45f;
        private const int MaxBoxes = 5;
        private const int MaxLandmarksPerBox = 32;
        private const float KeypointSizePx = 14f;
        private const float BoxPersistSeconds = 0.35f;

        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [Tooltip("Undo PassthroughImageSource horizontal flip so boxes align with passthrough view.")]
        [SerializeField] private bool m_mirrorOverlayHorizontal = true;
        [SerializeField] private UnityEngine.Color m_boxColor = new(0.1f, 1f, 0.25f, 0.95f);
        [SerializeField] private UnityEngine.Color m_keypointColor = new(1f, 0.85f, 0.1f, 1f);
        [SerializeField] private bool m_drawKeypoints;
        [SerializeField] private float m_borderThickness = 4f;
        [SerializeField] private bool m_showDepthInsideBoxes = true;
        [SerializeField] private ObjectronEnvironmentDepthProvider m_depthProvider;
        [SerializeField] [Range(0.2f, 1f)] private float m_depthFillAlpha = 0.62f;

        private Canvas m_canvas;
        private RectTransform m_canvasRect;
        private Camera m_eventCamera;
        private readonly List<BoxUi> m_activeBoxes = new();
        private readonly List<BoxUi> m_pool = new();
        private readonly List<Image> m_keypointPool = new();
        private readonly List<Image> m_activeKeypoints = new();
        private ObjectronOverlayFrame m_pending;
        private readonly object m_pendingLock = new();
        private bool m_hasPending;
        private float m_lastDrawTime;
        private int m_lastLoggedCount = -1;
        private bool m_readyLogged;
        private Material m_depthMaterial;
        private bool m_depthMaterialFailed;

        public bool HasDrawn2dThisSession { get; private set; }

        public bool ShowDepthInsideBoxes =>
            m_showDepthInsideBoxes
            && m_depthProvider != null
            && m_depthProvider.ShowDepthInBoxes;

        public bool IsDepthInBoxReady =>
            ShowDepthInsideBoxes && m_depthProvider != null && m_depthProvider.IsDepthReady;

        public void Bind(PassthroughCameraAccess cameraAccess, bool mirrorInferenceFlip = true)
        {
            m_cameraAccess = cameraAccess;
            m_mirrorOverlayHorizontal = mirrorInferenceFlip;
        }

        public void SetMirrorOverlayHorizontal(bool mirror) => m_mirrorOverlayHorizontal = mirror;

        public void BindDepth(ObjectronEnvironmentDepthProvider provider) => m_depthProvider = provider;

        public void SetShowDepthInsideBoxes(bool show) => m_showDepthInsideBoxes = show;

        public void Enqueue(ObjectronOverlayFrame frame)
        {
            if (frame == null)
            {
                return;
            }

            lock (m_pendingLock)
            {
                m_pending ??= new ObjectronOverlayFrame();
                m_pending.Rects.Clear();
                m_pending.LandmarkLists.Clear();
                foreach (var r in frame.Rects)
                {
                    m_pending.Rects.Add(r);
                }

                foreach (var list in frame.LandmarkLists)
                {
                    var copy = new List<ObjectronOverlayLandmark>(list.Count);
                    copy.AddRange(list);
                    m_pending.LandmarkLists.Add(copy);
                }

                m_hasPending = true;
            }
        }

        public void Clear()
        {
            lock (m_pendingLock)
            {
                m_hasPending = false;
                m_pending?.Rects.Clear();
                m_pending?.LandmarkLists.Clear();
            }

            HideAll();
        }

        private void Awake()
        {
            ResolveDepthProvider();
        }

        private void LateUpdate()
        {
            TryToggleDepthInBoxInput();
            RefreshDepthMaterial();

            ObjectronOverlayFrame frame = null;
            lock (m_pendingLock)
            {
                if (m_hasPending)
                {
                    frame = ClonePending();
                    m_hasPending = false;
                }
            }

            if (frame != null)
            {
                Draw(frame, m_cameraAccess != null && m_cameraAccess.IsPlaying
                    ? m_cameraAccess.GetCameraPose()
                    : default);
            }
            else if (Time.realtimeSinceStartup - m_lastDrawTime > BoxPersistSeconds)
            {
                HideAll();
            }
        }

        private ObjectronOverlayFrame ClonePending()
        {
            var clone = new ObjectronOverlayFrame();
            foreach (var r in m_pending.Rects)
            {
                clone.Rects.Add(r);
            }

            foreach (var list in m_pending.LandmarkLists)
            {
                var copy = new List<ObjectronOverlayLandmark>(list.Count);
                copy.AddRange(list);
                clone.LandmarkLists.Add(copy);
            }

            return clone;
        }

        private void Draw(ObjectronOverlayFrame frame, Pose cameraPose)
        {
            EnsureUi();
            if (m_canvas == null || m_eventCamera == null)
            {
                return;
            }

            m_lastDrawTime = Time.realtimeSinceStartup;
            ReturnActiveToPool();

            var boxCount = Mathf.Min(frame.Rects.Count, MaxBoxes);
            for (var i = 0; i < boxCount; i++)
            {
                var ui = GetBoxFromPool();
                m_activeBoxes.Add(ui);
                PlaceBoxUi(ui, frame.Rects[i], cameraPose);
            }

            if (m_drawKeypoints)
            {
                var kpIndex = 0;
                for (var b = 0; b < frame.LandmarkLists.Count && kpIndex < MaxBoxes * MaxLandmarksPerBox; b++)
                {
                    var landmarks = frame.LandmarkLists[b];
                    for (var j = 0; j < landmarks.Count && kpIndex < MaxBoxes * MaxLandmarksPerBox; j++, kpIndex++)
                    {
                        var img = GetKeypointFromPool();
                        m_activeKeypoints.Add(img);
                        PlaceKeypoint(img.rectTransform, landmarks[j], cameraPose);
                    }
                }
            }

            if (boxCount > 0)
            {
                HasDrawn2dThisSession = true;
            }

            if (boxCount != m_lastLoggedCount)
            {
                m_lastLoggedCount = boxCount;
                QuestObjectronLogger.Viz(
                    $"overlay_2d_boxes={boxCount} keypoints={m_activeKeypoints.Count} canvas={m_canvas.name} cam={m_eventCamera.name}");
            }

            RefreshDepthMaterial();
        }

        private void PlaceKeypoint(RectTransform rt, ObjectronOverlayLandmark lm, Pose cameraPose)
        {
            var vp = NormalizedToViewport(lm.X, lm.Y);
            if (m_cameraAccess != null && m_cameraAccess.IsPlaying
                && TryProjectViewportPoint(vp, cameraPose, out var screen))
            {
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        m_canvasRect, screen, m_eventCamera, out var local))
                {
                    rt.anchoredPosition = local;
                }
            }
            else
            {
                var size = m_canvasRect.rect.size;
                rt.anchoredPosition = new Vector2(
                    (vp.x - 0.5f) * size.x,
                    (vp.y - 0.5f) * size.y);
            }

            rt.sizeDelta = Vector2.one * KeypointSizePx;
            rt.gameObject.SetActive(true);
        }

        /// <summary>MediaPipe top-left normalized → PCA viewport (Y flipped; optional X mirror).</summary>
        private Vector2 NormalizedToViewport(float x, float y)
        {
            if (m_mirrorOverlayHorizontal)
            {
                x = 1f - x;
            }

            return new Vector2(x, 1f - y);
        }

        private UnityEngine.Rect NormalizedRectToViewport(ObjectronOverlayRect rect)
        {
            var xmin = rect.XCenter - rect.Width * 0.5f;
            var ymin = rect.YCenter - rect.Height * 0.5f;
            if (m_mirrorOverlayHorizontal)
            {
                xmin = 1f - xmin - rect.Width;
            }

            return new UnityEngine.Rect(xmin, 1f - ymin - rect.Height, rect.Width, rect.Height);
        }

        private bool TryProjectViewportRect(UnityEngine.Rect normViewport, Pose cameraPose, out Vector2 minScreen, out Vector2 maxScreen)
        {
            var c = normViewport.center;
            var min = normViewport.min;
            var max = normViewport.max;
            minScreen = default;
            maxScreen = default;
            if (!TryProjectViewportPoint(min, cameraPose, out minScreen)
                || !TryProjectViewportPoint(max, cameraPose, out maxScreen)
                || !TryProjectViewportPoint(c, cameraPose, out _))
            {
                return false;
            }

            if (minScreen.x > maxScreen.x)
            {
                (minScreen.x, maxScreen.x) = (maxScreen.x, minScreen.x);
            }

            if (minScreen.y > maxScreen.y)
            {
                (minScreen.y, maxScreen.y) = (maxScreen.y, minScreen.y);
            }

            return true;
        }

        private bool TryProjectViewportPoint(Vector2 viewport, Pose cameraPose, out Vector2 screen)
        {
            screen = default;
            if (m_cameraAccess == null)
            {
                return false;
            }

            const float projectDistance = 0.6f;
            var ray = m_cameraAccess.ViewportPointToRay(viewport, cameraPose);
            var world = ray.GetPoint(projectDistance);
            var sp = m_eventCamera.WorldToScreenPoint(world);
            if (sp.z <= 0f)
            {
                return false;
            }

            screen = new Vector2(sp.x, sp.y);
            return true;
        }

        private void SetRectFromScreenCorners(RectTransform rt, Vector2 minScreen, Vector2 maxScreen)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(m_canvasRect, minScreen, m_eventCamera, out var bl);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(m_canvasRect, maxScreen, m_eventCamera, out var tr);
            var size = new Vector2(Mathf.Abs(tr.x - bl.x), Mathf.Abs(tr.y - bl.y));
            var center = (bl + tr) * 0.5f;
            rt.anchoredPosition = center;
            rt.sizeDelta = size;
        }

        private void SetRectFromNormalized(RectTransform rt, UnityEngine.Rect normViewport)
        {
            var size = m_canvasRect.rect.size;
            var center = normViewport.center;
            rt.anchoredPosition = new Vector2(
                (center.x - 0.5f) * size.x,
                (center.y - 0.5f) * size.y);
            rt.sizeDelta = new Vector2(normViewport.width * size.x, normViewport.height * size.y);
        }

        private void EnsureUi()
        {
            if (m_canvas != null)
            {
                if (!m_canvas.enabled)
                {
                    m_canvas.enabled = true;
                }

                return;
            }

            var anchor = FindEyeAnchor();
            if (anchor == null)
            {
                return;
            }

            m_eventCamera = anchor.GetComponent<Camera>() ?? Camera.main;
            if (m_eventCamera == null)
            {
                return;
            }

            var root = new GameObject("ObjectronPassthroughOverlay");
            root.transform.SetParent(anchor, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            m_canvas = root.AddComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceCamera;
            m_canvas.worldCamera = m_eventCamera;
            m_canvas.planeDistance = OverlayPlaneDistance;
            m_canvas.sortingOrder = 9000;
            m_canvas.enabled = true;

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            root.AddComponent<GraphicRaycaster>();
            m_canvasRect = m_canvas.GetComponent<RectTransform>();

            if (!m_readyLogged)
            {
                m_readyLogged = true;
                QuestObjectronLogger.Viz(
                    $"overlay_ready parent={anchor.name} eventCam={m_eventCamera.name} sorting={m_canvas.sortingOrder}");
            }
        }

        private BoxUi GetBoxFromPool()
        {
            if (m_pool.Count > 0)
            {
                var ui = m_pool[m_pool.Count - 1];
                m_pool.RemoveAt(m_pool.Count - 1);
                return ui;
            }

            return CreateBoxUi();
        }

        private Image GetKeypointFromPool()
        {
            Image img;
            if (m_keypointPool.Count > 0)
            {
                img = m_keypointPool[m_keypointPool.Count - 1];
                m_keypointPool.RemoveAt(m_keypointPool.Count - 1);
            }
            else
            {
                var go = new GameObject("Keypoint");
                go.transform.SetParent(m_canvasRect, false);
                img = go.AddComponent<Image>();
                img.color = m_keypointColor;
                img.raycastTarget = false;
            }

            return img;
        }

        private BoxUi CreateBoxUi()
        {
            var root = new GameObject("DetectionBox2D");
            root.transform.SetParent(m_canvasRect, false);
            var rt = root.AddComponent<RectTransform>();
            rt.pivot = new Vector2(0.5f, 0.5f);

            var depthFill = CreateDepthFill(rt);
            var border = m_borderThickness;
            var top = CreateEdge(rt, "Top", m_boxColor);
            var bottom = CreateEdge(rt, "Bottom", m_boxColor);
            var left = CreateEdge(rt, "Left", m_boxColor);
            var right = CreateEdge(rt, "Right", m_boxColor);

            return new BoxUi
            {
                Root = rt,
                DepthFill = depthFill,
                Top = top,
                Bottom = bottom,
                Left = left,
                Right = right,
                Border = border
            };
        }

        private static Image CreateDepthFill(RectTransform parent)
        {
            var go = new GameObject("DepthFill");
            go.transform.SetParent(parent, false);
            go.transform.SetAsFirstSibling();
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.color = new UnityEngine.Color(1f, 1f, 1f, 0.62f);
            go.SetActive(false);
            return img;
        }

        private static Image CreateEdge(RectTransform parent, string name, UnityEngine.Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static void LayoutBoxEdges(BoxUi ui)
        {
            var w = ui.Root.sizeDelta.x;
            var h = ui.Root.sizeDelta.y;
            var t = ui.Border;

            SetEdge(ui.Top, new Vector2(0, h * 0.5f - t * 0.5f), new Vector2(w, t));
            SetEdge(ui.Bottom, new Vector2(0, -h * 0.5f + t * 0.5f), new Vector2(w, t));
            SetEdge(ui.Left, new Vector2(-w * 0.5f + t * 0.5f, 0), new Vector2(t, h));
            SetEdge(ui.Right, new Vector2(w * 0.5f - t * 0.5f, 0), new Vector2(t, h));
        }

        private static void SetEdge(Image img, Vector2 pos, Vector2 size)
        {
            var rt = img.rectTransform;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        private void PlaceBoxUi(BoxUi ui, ObjectronOverlayRect rect, Pose cameraPose)
        {
            var norm = NormalizedRectToViewport(rect);
            if (m_cameraAccess != null && m_cameraAccess.IsPlaying && TryProjectViewportRect(norm, cameraPose, out var minScreen, out var maxScreen))
            {
                SetRectFromScreenCorners(ui.Root, minScreen, maxScreen);
            }
            else
            {
                SetRectFromNormalized(ui.Root, norm);
            }

            LayoutBoxEdges(ui);
            UpdateBoxDepthFill(ui);
            ui.Root.gameObject.SetActive(true);
        }

        private void UpdateBoxDepthFill(BoxUi ui)
        {
            if (ui.DepthFill == null)
            {
                return;
            }

            var show = IsDepthInBoxReady;
            if (!show)
            {
                ui.DepthFill.gameObject.SetActive(false);
                return;
            }

            if (!EnsureDepthMaterial())
            {
                ui.DepthFill.gameObject.SetActive(false);
                return;
            }

            ui.DepthFill.material = m_depthMaterial;
            ui.DepthFill.color = new UnityEngine.Color(1f, 1f, 1f, m_depthFillAlpha);
            ui.DepthFill.gameObject.SetActive(true);
        }

        private void ResolveDepthProvider()
        {
            if (m_depthProvider != null)
            {
                return;
            }

#if UNITY_2023_1_OR_NEWER
            m_depthProvider = UnityEngine.Object.FindAnyObjectByType<ObjectronEnvironmentDepthProvider>();
#else
            m_depthProvider = UnityEngine.Object.FindObjectOfType<ObjectronEnvironmentDepthProvider>();
#endif
        }

        private void RefreshDepthMaterial()
        {
            if (m_depthMaterial == null || m_depthProvider == null)
            {
                return;
            }

            m_depthProvider.ApplyToMaterial(m_depthMaterial);
        }

        private bool EnsureDepthMaterial()
        {
            if (m_depthMaterial != null)
            {
                return true;
            }

            if (m_depthMaterialFailed)
            {
                return false;
            }

            ResolveDepthProvider();
            if (m_depthProvider == null || !m_depthProvider.IsDepthReady)
            {
                return false;
            }

            m_depthMaterial = m_depthProvider.CreateInBoxMaterial();
            if (m_depthMaterial == null)
            {
                m_depthMaterialFailed = true;
                return false;
            }

            return true;
        }

        private void TryToggleDepthInBoxInput()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_depthProvider == null)
            {
                return;
            }

            if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.RTouch))
            {
                m_depthProvider.ShowDepthInBoxes = !m_depthProvider.ShowDepthInBoxes;
                QuestObjectronLogger.Viz(
                    $"depth_in_box toggle show={m_depthProvider.ShowDepthInBoxes}");
            }

            if (OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.RTouch))
            {
                m_depthProvider.ToggleDepthSource();
            }
#endif
        }

        private void OnDestroy()
        {
            if (m_depthMaterial != null)
            {
                Destroy(m_depthMaterial);
                m_depthMaterial = null;
            }
        }

        private void ReturnActiveToPool()
        {
            foreach (var box in m_activeBoxes)
            {
                box.Root.gameObject.SetActive(false);
                m_pool.Add(box);
            }

            m_activeBoxes.Clear();

            foreach (var kp in m_activeKeypoints)
            {
                kp.gameObject.SetActive(false);
                m_keypointPool.Add(kp);
            }

            m_activeKeypoints.Clear();
        }

        private void HideAll() => ReturnActiveToPool();

        private static Transform FindEyeAnchor()
        {
#if UNITY_2023_1_OR_NEWER
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
#else
            var rig = UnityEngine.Object.FindObjectOfType<OVRCameraRig>();
#endif
            if (rig != null && rig.centerEyeAnchor != null)
            {
                return rig.centerEyeAnchor;
            }

            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        private sealed class BoxUi
        {
            public RectTransform Root;
            public Image DepthFill;
            public Image Top;
            public Image Bottom;
            public Image Left;
            public Image Right;
            public float Border;
        }
    }
}
