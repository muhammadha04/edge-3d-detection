// World-space markers + logcat HUD for Objectron detections on Quest.

using System.Text;
using UnityEngine;

namespace QuestObjectron
{
    public class ObjectronDetectionDebug : MonoBehaviour
    {
        private const int MaxMarkers = 5;

        [SerializeField] private float m_markerRadius = 0.035f;
        [SerializeField] private float m_crossArmLength = 0.12f;
        [SerializeField] private bool m_showWorldMarkers;
        [SerializeField] private bool m_showOnGuiHud = true;

        private readonly StringBuilder m_hud = new();
        private readonly GameObject[] m_markers = new GameObject[MaxMarkers];
        private int m_activeMarkers;
        private string m_lastSummary = "waiting for detection";
        private bool m_prewarmed;

        public void Prewarm()
        {
            if (m_prewarmed)
            {
                return;
            }

            for (var i = 0; i < MaxMarkers; i++)
            {
                var marker = GetOrCreateMarker(i);
                marker.SetActive(false);
            }

            m_prewarmed = true;
        }

        private void Awake()
        {
            m_showWorldMarkers = false;
            Prewarm();
            HideMarkers();
#if UNITY_ANDROID && !UNITY_EDITOR
            m_showOnGuiHud = false;
#endif
        }

        public void Report(DetectionDebugInfo info)
        {
            if (!m_prewarmed)
            {
                Prewarm();
            }

            m_lastSummary = info.Summary;
            m_hud.Clear();
            m_hud.AppendLine(info.Summary);
            if (!string.IsNullOrEmpty(info.Detail))
            {
                m_hud.AppendLine(info.Detail);
            }

            QuestObjectronLogger.Dbg(info.Summary);
            if (!string.IsNullOrEmpty(info.Detail))
            {
                QuestObjectronLogger.Dbg(info.Detail);
            }

            if (!m_showWorldMarkers)
            {
                HideMarkers();
                return;
            }

            HideMarkers();
            if (info.WorldCorners != null && info.WorldCorners.Length >= 9
                && ObjectronBoxValidation.HasValidExtents(info.WorldCorners))
            {
                ShowMarker(0, info.WorldCorners[0], UnityEngine.Color.green);
                for (var i = 1; i <= 8 && i < info.WorldCorners.Length; i++)
                {
                    ShowMarker(i, info.WorldCorners[i], UnityEngine.Color.cyan);
                }

                m_activeMarkers = Mathf.Min(9, info.WorldCorners.Length);
            }
            else if (info.WorldCenter.HasValue)
            {
                ShowMarker(0, info.WorldCenter.Value, UnityEngine.Color.green);
                m_activeMarkers = 1;
            }
        }

        public void Clear()
        {
            m_lastSummary = "no detection";
            m_hud.Clear();
            m_hud.AppendLine(m_lastSummary);
            HideMarkers();
        }

        private void OnGUI()
        {
            if (!m_showOnGuiHud)
            {
                return;
            }

            const int w = 720;
            const int h = 200;
            GUI.Box(new Rect(12, 12, w, h), "QuestObj3D debug");
            GUI.Label(new Rect(24, 40, w - 24, h - 52), m_hud.Length > 0 ? m_hud.ToString() : m_lastSummary);
        }

        private void ShowMarker(int index, Vector3 worldPosition, UnityEngine.Color color)
        {
            if (index < 0 || index >= MaxMarkers)
            {
                return;
            }

            var marker = GetOrCreateMarker(index);
            marker.SetActive(true);
            marker.transform.position = worldPosition;

            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                renderer.sharedMaterial.color = color;
            }

            if (index == 0)
            {
                DrawCross(marker.transform, color);
            }
        }

        private void DrawCross(Transform root, UnityEngine.Color color)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var line = root.Find($"Cross_{axis}")?.GetComponent<LineRenderer>();
                if (line == null)
                {
                    continue;
                }

                var dir = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
                line.SetPosition(0, root.position - dir * m_crossArmLength);
                line.SetPosition(1, root.position + dir * m_crossArmLength);
                line.startColor = color;
                line.endColor = color;
            }
        }

        private GameObject GetOrCreateMarker(int index)
        {
            if (m_markers[index] != null)
            {
                return m_markers[index];
            }

            var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            root.name = $"ObjectronDebugMarker_{index}";
            root.transform.SetParent(transform, false);
            root.transform.localScale = Vector3.one * (m_markerRadius * 2f);
            var collider = root.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                root.GetComponent<Renderer>().sharedMaterial = new Material(shader);
            }

            if (index == 0)
            {
                var crossShader = Shader.Find("Sprites/Default");
                var crossMat = crossShader != null ? new Material(crossShader) : null;
                for (var axis = 0; axis < 3; axis++)
                {
                    var lineGo = new GameObject($"Cross_{axis}");
                    lineGo.transform.SetParent(root.transform, false);
                    var lr = lineGo.AddComponent<LineRenderer>();
                    lr.useWorldSpace = true;
                    lr.positionCount = 2;
                    lr.startWidth = 0.008f;
                    lr.endWidth = 0.008f;
                    if (crossMat != null)
                    {
                        lr.sharedMaterial = crossMat;
                    }
                }
            }

            m_markers[index] = root;
            return root;
        }

        private void HideMarkers()
        {
            m_activeMarkers = 0;
            foreach (var marker in m_markers)
            {
                if (marker != null)
                {
                    marker.SetActive(false);
                }
            }
        }
    }

    public readonly struct DetectionDebugInfo
    {
        public readonly string Summary;
        public readonly string Detail;
        public readonly Vector3? WorldCenter;
        public readonly Vector3[] WorldCorners;

        public DetectionDebugInfo(string summary, string detail, Vector3? worldCenter, Vector3[] worldCorners)
        {
            Summary = summary;
            Detail = detail;
            WorldCenter = worldCenter;
            WorldCorners = worldCorners;
        }
    }
}
