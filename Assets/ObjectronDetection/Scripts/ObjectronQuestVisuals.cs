// World-space 3D cup box: 12 green edge lines only.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestObjectron
{
    public class ObjectronQuestVisuals : MonoBehaviour
    {
        private const int MaxBoxes = 3;
        private const int DefaultLayer = 0;
        private const int VisualsVersion = 5;
        private const int TableCornerDots = 4;

        private static readonly int[][] s_boxEdges =
        {
            new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 },
            new[] { 0, 2 }, new[] { 1, 3 }, new[] { 4, 6 }, new[] { 5, 7 },
            new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 },
        };

        private static Transform s_worldRoot;
        private readonly List<BoxVisual> m_pool = new();
        private List<Vector3[]> m_heldBoxes;
        private List<Vector3[]> m_localizedBoxes;
        private float m_heldUntil;
        private bool m_prewarmed;
        private string m_lastVizLogKey = "";
        private int m_activeCount;

        public bool IsHolding => m_heldBoxes != null && Time.realtimeSinceStartup < m_heldUntil;
        public bool IsLocalized => m_localizedBoxes != null && m_localizedBoxes.Count > 0;
        public int ActiveCount => m_activeCount;

        public static Transform VisualsRoot => GetOrCreateWorldRoot();

        public void Prewarm()
        {
            GetOrCreateWorldRoot();
            if (m_prewarmed && m_pool.Count > 0)
            {
                return;
            }

            m_pool.Clear();
            m_prewarmed = false;
            for (var i = 0; i < MaxBoxes; i++)
            {
                var visual = GetOrCreate(i);
                visual.Root.SetActive(false);
            }

            m_prewarmed = true;
            QuestObjectronLogger.Viz($"quest_visuals_prewarm={m_pool.Count} wire_only");
        }

        private void Awake()
        {
            Prewarm();
        }

        public void Show(IReadOnlyList<Vector3[]> worldBoxes)
        {
            if (IsLocalized || worldBoxes == null || worldBoxes.Count == 0)
            {
                return;
            }

            m_heldBoxes = CloneBoxes(worldBoxes);
            m_heldUntil = Time.realtimeSinceStartup + m_holdSeconds;
            Apply(m_heldBoxes);
        }

        /// <summary>Pin 3D wireframes in world space (environment-localized, does not follow the camera).</summary>
        public void Localize(IReadOnlyList<Vector3[]> worldBoxes)
        {
            if (worldBoxes == null || worldBoxes.Count == 0)
            {
                QuestObjectronLogger.Viz("box_localize_skipped no_corners");
                return;
            }

            m_localizedBoxes = CloneBoxes(worldBoxes);
            m_heldBoxes = null;
            Apply(m_localizedBoxes);
            QuestObjectronLogger.Viz($"box_localized corners={m_localizedBoxes.Count}");
        }

        public void ClearLocalization(bool silent = false)
        {
            var hadContent = IsLocalized || m_activeCount > 0;
            m_localizedBoxes = null;
            m_heldBoxes = null;
            m_lastVizLogKey = "";
            m_activeCount = 0;
            HideAll();
            if (!silent && hadContent)
            {
                QuestObjectronLogger.Viz("box_localize_cleared");
            }
        }

        public void ClearAllForSceneExit()
        {
            ClearLocalization(silent: true);
            m_prewarmed = false;
            m_pool.Clear();
        }

        public static void DestroyPersistentWorldRoot()
        {
            if (s_worldRoot != null)
            {
                Object.Destroy(s_worldRoot.gameObject);
                s_worldRoot = null;
            }

            var existing = GameObject.Find("ObjectronVisualsRoot");
            if (existing != null)
            {
                Object.Destroy(existing);
            }
        }

        public void HoldOrClear()
        {
            if (IsLocalized)
            {
                Apply(m_localizedBoxes);
                return;
            }

            if (m_heldBoxes != null && Time.realtimeSinceStartup < m_heldUntil)
            {
                Apply(m_heldBoxes);
                return;
            }

            m_heldBoxes = null;
            m_lastVizLogKey = "";
            m_activeCount = 0;
            HideAll();
        }

        private static List<Vector3[]> CloneBoxes(IReadOnlyList<Vector3[]> source)
        {
            var copy = new List<Vector3[]>(source.Count);
            foreach (var box in source)
            {
                copy.Add(box != null ? (Vector3[])box.Clone() : null);
            }

            return copy;
        }

        private void Apply(IReadOnlyList<Vector3[]> worldBoxes)
        {
            if (!m_prewarmed)
            {
                Prewarm();
            }

            var shown = 0;
            for (var i = 0; i < worldBoxes.Count && i < MaxBoxes; i++)
            {
                var corners = worldBoxes[i];
                if (corners == null || corners.Length < 9)
                {
                    continue;
                }

                if (!ObjectronBoxValidation.TryGetExtentMeters(corners, out _))
                {
                    continue;
                }

                var visual = GetOrCreate(i);
                visual.Root.SetActive(true);
                visual.Wireframe.SetActive(true);
                UpdateWireframe(visual, corners);
                UpdateTableDots(visual, corners);
                shown++;
            }

            for (var i = shown; i < m_pool.Count; i++)
            {
                if (m_pool[i].Root != null)
                {
                    m_pool[i].Root.SetActive(false);
                }
            }

            m_activeCount = shown;
            if (shown > 0 && worldBoxes[0] != null)
            {
                LogVizIfChanged(shown, worldBoxes[0]);
            }
        }

        private void LogVizIfChanged(int shown, Vector3[] corners)
        {
            ObjectronBoxValidation.TryGetExtentMeters(corners, out var extent);
            var center = corners[0];
            var hmd = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var dist = Vector3.Distance(hmd, center);
            var logKey = $"{shown}|{extent:F3}|{center:F2}";
            if (logKey == m_lastVizLogKey)
            {
                return;
            }

            m_lastVizLogKey = logKey;
            QuestObjectronLogger.Viz(
                $"quest_3d={shown} wire_only extent={extent:F3}m center={center:F2} dist={dist:F2}m");
        }

        private static void UpdateWireframe(BoxVisual visual, Vector3[] corners)
        {
            for (var e = 0; e < s_boxEdges.Length; e++)
            {
                var lr = visual.WireEdges[e];
                var a = corners[s_boxEdges[e][0] + 1];
                var b = corners[s_boxEdges[e][1] + 1];
                lr.SetPosition(0, a);
                lr.SetPosition(1, b);
            }
        }

        private void UpdateTableDots(BoxVisual visual, Vector3[] corners)
        {
            if (visual.TableDots == null)
            {
                return;
            }

            for (var i = 0; i < visual.TableDots.Length; i++)
            {
                visual.TableDots[i].gameObject.SetActive(false);
            }

            if (!ObjectronBoxGeometry.TryGetTableCornerIndices(corners, out var tableIndices))
            {
                return;
            }

            for (var i = 0; i < tableIndices.Length && i < visual.TableDots.Length; i++)
            {
                var dot = visual.TableDots[i];
                dot.gameObject.SetActive(true);
                dot.position = corners[tableIndices[i]];
            }
        }

        private BoxVisual GetOrCreate(int index)
        {
            while (m_pool.Count <= index)
            {
                m_pool.Add(CreateVisual());
            }

            return m_pool[index];
        }

        private BoxVisual CreateVisual()
        {
            var root = new GameObject("ObjectronQuestBox");
            root.layer = DefaultLayer;
            root.transform.SetParent(GetOrCreateWorldRoot(), false);

            var wireRoot = new GameObject("Wireframe");
            wireRoot.layer = DefaultLayer;
            wireRoot.transform.SetParent(root.transform, false);

            var wireEdges = new LineRenderer[s_boxEdges.Length];
            for (var e = 0; e < s_boxEdges.Length; e++)
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

            var tableDots = new Transform[TableCornerDots];
            for (var d = 0; d < TableCornerDots; d++)
            {
                var dotGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                dotGo.name = $"TableDot_{d}";
                dotGo.layer = DefaultLayer;
                dotGo.transform.SetParent(root.transform, false);
                dotGo.transform.localScale = Vector3.one * m_tableDotDiameterM;
                var col = dotGo.GetComponent<Collider>();
                if (col != null)
                {
                    Object.Destroy(col);
                }

                var r = dotGo.GetComponent<Renderer>();
                if (r != null)
                {
                    r.sharedMaterial = ObjectronMrVisibleMaterial.CreateUnlit(m_tableDotColor);
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    r.receiveShadows = false;
                }

                dotGo.SetActive(false);
                tableDots[d] = dotGo.transform;
            }

            root.SetActive(false);
            wireRoot.SetActive(false);

            return new BoxVisual
            {
                Root = root,
                Wireframe = wireRoot,
                WireEdges = wireEdges,
                TableDots = tableDots,
            };
        }

        private static Transform GetOrCreateWorldRoot()
        {
            if (s_worldRoot != null)
            {
                var marker = s_worldRoot.GetComponent<ObjectronVisualsVersionMarker>();
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

            var existing = GameObject.Find("ObjectronVisualsRoot");
            if (existing != null)
            {
                var marker = existing.GetComponent<ObjectronVisualsVersionMarker>();
                if (marker != null && marker.Version == VisualsVersion)
                {
                    s_worldRoot = existing.transform;
                    return s_worldRoot;
                }

                Object.Destroy(existing);
            }

            var go = new GameObject("ObjectronVisualsRoot");
            go.layer = DefaultLayer;
            go.AddComponent<ObjectronVisualsVersionMarker>().Version = VisualsVersion;
            DontDestroyOnLoad(go);
            s_worldRoot = go.transform;
            return s_worldRoot;
        }

        private sealed class ObjectronVisualsVersionMarker : MonoBehaviour
        {
            public int Version;
        }

        private void HideAll()
        {
            foreach (var v in m_pool)
            {
                if (v.Root != null)
                {
                    v.Root.SetActive(false);
                }
            }
        }

        [SerializeField] private float m_holdSeconds = 1.5f;
        [SerializeField] private float m_wireframeWidth = 0.004f;
        [SerializeField] private Color m_wireframeColor = new(0.15f, 1f, 0.35f, 1f);
        [SerializeField] private float m_tableDotDiameterM = 0.018f;
        [SerializeField] private Color m_tableDotColor = new(0.2f, 0.45f, 1f, 1f);

        private sealed class BoxVisual
        {
            public GameObject Root;
            public GameObject Wireframe;
            public LineRenderer[] WireEdges;
            public Transform[] TableDots;
        }
    }
}
