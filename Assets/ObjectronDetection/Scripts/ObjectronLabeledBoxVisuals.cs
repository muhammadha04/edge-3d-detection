// World-space 3D box wireframe with per-edge axis labels (model Scale values).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace QuestObjectron
{
    public class ObjectronLabeledBoxVisuals : MonoBehaviour
    {
        private const int EdgeCount = 12;
        private const int DefaultLayer = 0;
        private const int VisualsVersion = 1;

        private static readonly int[][] s_boxEdges =
        {
            new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 },
            new[] { 0, 2 }, new[] { 1, 3 }, new[] { 4, 6 }, new[] { 5, 7 },
            new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 },
        };

        private static Transform s_worldRoot;
        private readonly BoxVisual m_visual = new();
        private Vector3[] m_activeCorners;
        private Vector3 m_activeScale;
        private Vector3 m_activeTranslation;
        private Vector3 m_activeRotationEuler;
        private Pose? m_activeCameraPose;
        private Vector3[] m_localizedCorners;
        private Vector3 m_localizedScale;
        private Vector3 m_localizedTranslation;
        private Vector3 m_localizedRotationEuler;
        private bool m_prewarmed;

        public bool IsLocalized => m_localizedCorners != null;
        public bool HasActiveBox => m_activeCorners != null || IsLocalized;

        public void Prewarm()
        {
            GetOrCreateWorldRoot();
            if (m_prewarmed)
            {
                return;
            }

            CreateVisual();
            m_visual.Root.SetActive(false);
            m_prewarmed = true;
        }

        private void Awake()
        {
            Prewarm();
        }

        private void LateUpdate()
        {
            if (IsLocalized)
            {
                ApplyBox(m_localizedCorners, m_localizedScale, m_localizedTranslation, m_localizedRotationEuler, m_activeCameraPose);
                return;
            }

            if (m_activeCorners != null)
            {
                ApplyBox(m_activeCorners, m_activeScale, m_activeTranslation, m_activeRotationEuler, m_activeCameraPose);
            }
        }

        public void Show(
            Vector3[] corners,
            Vector3 scale,
            Vector3 translation,
            Vector3 rotationEuler,
            Pose? cameraPose = null)
        {
            if (IsLocalized || corners == null || corners.Length < 9)
            {
                return;
            }

            m_activeCorners = (Vector3[])corners.Clone();
            m_activeScale = scale;
            m_activeTranslation = translation;
            m_activeRotationEuler = rotationEuler;
            m_activeCameraPose = cameraPose;
            ApplyBox(m_activeCorners, m_activeScale, m_activeTranslation, m_activeRotationEuler, cameraPose);
        }

        public void Localize(
            Vector3[] corners,
            Vector3 scale,
            Vector3 translation,
            Vector3 rotationEuler,
            Pose? cameraPose = null)
        {
            if (corners == null || corners.Length < 9)
            {
                return;
            }

            m_localizedCorners = (Vector3[])corners.Clone();
            m_localizedScale = scale;
            m_localizedTranslation = translation;
            m_localizedRotationEuler = rotationEuler;
            m_activeCorners = null;
            m_activeCameraPose = cameraPose;
            ApplyBox(m_localizedCorners, m_localizedScale, m_localizedTranslation, m_localizedRotationEuler, cameraPose);
        }

        public void ClearLocalization()
        {
            m_localizedCorners = null;
            m_activeCorners = null;
            HideAll();
        }

        public void Clear()
        {
            ClearLocalization();
        }

        public void ClearAllForSceneExit()
        {
            Clear();
            m_prewarmed = false;
        }

        public static void DestroyPersistentWorldRoot()
        {
            if (s_worldRoot != null)
            {
                Object.Destroy(s_worldRoot.gameObject);
                s_worldRoot = null;
            }

            var existing = GameObject.Find("ObjectronLabeledVisualsRoot");
            if (existing != null)
            {
                Object.Destroy(existing);
            }
        }

        private void ApplyBox(
            Vector3[] corners,
            Vector3 scale,
            Vector3 translation,
            Vector3 rotationEuler,
            Pose? cameraPose)
        {
            if (!m_prewarmed)
            {
                Prewarm();
            }

            if (!ObjectronBoxValidation.TryGetExtentMeters(corners, out _))
            {
                HideAll();
                return;
            }

            m_visual.Root.SetActive(true);
            m_visual.Wireframe.SetActive(true);
            UpdateWireframe(corners);
            UpdateEdgeLabels(corners, scale);
            UpdateCenterLabel(translation, rotationEuler, cameraPose);
        }

        private void UpdateWireframe(Vector3[] corners)
        {
            for (var e = 0; e < s_boxEdges.Length; e++)
            {
                var lr = m_visual.WireEdges[e];
                var a = corners[s_boxEdges[e][0] + 1];
                var b = corners[s_boxEdges[e][1] + 1];
                lr.SetPosition(0, a);
                lr.SetPosition(1, b);
            }
        }

        private void UpdateEdgeLabels(Vector3[] corners, Vector3 scale)
        {
            if (!TryGetBoxBasis(corners, out var right, out var up, out var forward))
            {
                return;
            }

            for (var e = 0; e < EdgeCount; e++)
            {
                var a = corners[s_boxEdges[e][0] + 1];
                var b = corners[s_boxEdges[e][1] + 1];
                var edgeDir = (b - a).normalized;
                var axis = ClassifyAxis(edgeDir, right, up, forward);
                var label = axis switch
                {
                    0 => $"X: {scale.x:F2}",
                    1 => $"Y: {scale.y:F2}",
                    _ => $"Z: {scale.z:F2}",
                };

                var midpoint = (a + b) * 0.5f;
                var labelRoot = m_visual.EdgeLabels[e];
                labelRoot.gameObject.SetActive(true);
                labelRoot.position = midpoint;
                m_visual.EdgeLabelTexts[e].text = label;
                Billboard(labelRoot);
            }
        }

        private void UpdateCenterLabel(Vector3 translation, Vector3 rotationEuler, Pose? cameraPose)
        {
            if (m_visual.CenterLabel == null)
            {
                return;
            }

            var corners = IsLocalized ? m_localizedCorners : m_activeCorners;
            if (corners == null)
            {
                m_visual.CenterLabel.gameObject.SetActive(false);
                return;
            }

            m_visual.CenterLabel.gameObject.SetActive(true);
            m_visual.CenterLabel.position = corners[0];
            var camLine = cameraPose.HasValue
                ? $"Cam=({cameraPose.Value.rotation.eulerAngles.x:F0},{cameraPose.Value.rotation.eulerAngles.y:F0},{cameraPose.Value.rotation.eulerAngles.z:F0})"
                : "Cam=n/a";
            m_visual.CenterLabelText.text =
                $"T=({translation.x:F2},{translation.y:F2},{translation.z:F2})\n" +
                $"R=({rotationEuler.x:F0},{rotationEuler.y:F0},{rotationEuler.z:F0})\n" +
                camLine;
            Billboard(m_visual.CenterLabel);
        }

        private static bool TryGetBoxBasis(Vector3[] corners, out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            right = up = forward = Vector3.zero;
            if (corners == null || corners.Length < 9)
            {
                return false;
            }

            right = (corners[2] - corners[1]).normalized;
            up = (corners[3] - corners[1]).normalized;
            forward = (corners[5] - corners[1]).normalized;
            return right.sqrMagnitude > 1e-6f && up.sqrMagnitude > 1e-6f && forward.sqrMagnitude > 1e-6f;
        }

        private static int ClassifyAxis(Vector3 edgeDir, Vector3 right, Vector3 up, Vector3 forward)
        {
            var dotRight = Mathf.Abs(Vector3.Dot(edgeDir, right));
            var dotUp = Mathf.Abs(Vector3.Dot(edgeDir, up));
            var dotForward = Mathf.Abs(Vector3.Dot(edgeDir, forward));
            if (dotRight >= dotUp && dotRight >= dotForward)
            {
                return 0;
            }

            return dotUp >= dotForward ? 1 : 2;
        }

        private static void Billboard(Transform target)
        {
            var cam = Camera.main;
            if (cam == null || target == null)
            {
                return;
            }

            var toCam = cam.transform.position - target.position;
            if (toCam.sqrMagnitude < 1e-6f)
            {
                return;
            }

            target.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
        }

        private void CreateVisual()
        {
            var root = new GameObject("ObjectronLabeledBox");
            root.layer = DefaultLayer;
            root.transform.SetParent(GetOrCreateWorldRoot(), false);

            var wireRoot = new GameObject("Wireframe");
            wireRoot.layer = DefaultLayer;
            wireRoot.transform.SetParent(root.transform, false);

            var wireEdges = new LineRenderer[EdgeCount];
            for (var e = 0; e < EdgeCount; e++)
            {
                var edgeGo = new GameObject($"Edge_{e}");
                edgeGo.layer = DefaultLayer;
                edgeGo.transform.SetParent(wireRoot.transform, false);
                var lr = edgeGo.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.startWidth = m_wireframeWidth;
                lr.endWidth = m_wireframeWidth;
                lr.alignment = LineAlignment.TransformZ;
                lr.numCapVertices = 2;
                ObjectronMrVisibleMaterial.ApplyToLineRenderer(lr, m_wireframeColor);
                lr.shadowCastingMode = ShadowCastingMode.Off;
                lr.receiveShadows = false;
                wireEdges[e] = lr;
            }

            var edgeLabels = new Transform[EdgeCount];
            var edgeLabelTexts = new Text[EdgeCount];
            for (var e = 0; e < EdgeCount; e++)
            {
                edgeLabels[e] = CreateWorldLabel($"EdgeLabel_{e}", root.transform, m_edgeLabelFontSize, out edgeLabelTexts[e]);
                edgeLabels[e].gameObject.SetActive(false);
            }

            var centerLabel = CreateWorldLabel("CenterLabel", root.transform, m_centerLabelFontSize, out var centerText);
            centerLabel.gameObject.SetActive(false);

            root.SetActive(false);
            wireRoot.SetActive(false);

            m_visual.Root = root;
            m_visual.Wireframe = wireRoot;
            m_visual.WireEdges = wireEdges;
            m_visual.EdgeLabels = edgeLabels;
            m_visual.EdgeLabelTexts = edgeLabelTexts;
            m_visual.CenterLabel = centerLabel;
            m_visual.CenterLabelText = centerText;
        }

        private static Transform CreateWorldLabel(string name, Transform parent, int fontSize, out Text text)
        {
            var go = new GameObject(name);
            go.layer = DefaultLayer;
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * 0.001f;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(220f, 56f);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.color = m_labelColor;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            return go.transform;
        }

        private static Transform GetOrCreateWorldRoot()
        {
            if (s_worldRoot != null)
            {
                var marker = s_worldRoot.GetComponent<ObjectronLabeledVisualsVersionMarker>();
                if (marker == null || marker.Version != VisualsVersion)
                {
                    Object.Destroy(s_worldRoot.gameObject);
                    s_worldRoot = null;
                }
            }

            if (s_worldRoot != null)
            {
                return s_worldRoot;
            }

            var existing = GameObject.Find("ObjectronLabeledVisualsRoot");
            if (existing != null)
            {
                Object.Destroy(existing);
            }

            var go = new GameObject("ObjectronLabeledVisualsRoot");
            go.layer = DefaultLayer;
            go.AddComponent<ObjectronLabeledVisualsVersionMarker>().Version = VisualsVersion;
            DontDestroyOnLoad(go);
            s_worldRoot = go.transform;
            return s_worldRoot;
        }

        private void HideAll()
        {
            if (m_visual.Root != null)
            {
                m_visual.Root.SetActive(false);
            }
        }

        [SerializeField] private float m_wireframeWidth = 0.004f;
        [SerializeField] private Color m_wireframeColor = new(0.15f, 1f, 0.35f, 1f);
        [SerializeField] private int m_edgeLabelFontSize = 28;
        [SerializeField] private int m_centerLabelFontSize = 22;
        private static readonly Color m_labelColor = new(1f, 1f, 0.35f, 1f);

        private sealed class ObjectronLabeledVisualsVersionMarker : MonoBehaviour
        {
            public int Version;
        }

        private sealed class BoxVisual
        {
            public GameObject Root;
            public GameObject Wireframe;
            public LineRenderer[] WireEdges;
            public Transform[] EdgeLabels;
            public Text[] EdgeLabelTexts;
            public Transform CenterLabel;
            public Text CenterLabelText;
        }
    }
}
