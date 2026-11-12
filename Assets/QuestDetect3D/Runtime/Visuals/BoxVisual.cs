// World-anchored 3D highlight for one tracked object: thin edges + corner brackets + faint faces,
// and a billboard label with category, confidence %, metric size and positional uncertainty.
// In scan mode the lab-chair scan (ScanModel, canonical unit frame) is drawn inside the box, scaled to the
// box size and turned so its backrest sits on the side measured by BackrestEstimator.
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class BoxVisual : MonoBehaviour
    {
        private const float EdgeHalfThickness = 0.0025f;
        private const float BracketHalfThickness = 0.0055f;

        private Material m_material;
        private Transform m_head;
        private MeshRenderer m_edges;
        private MeshRenderer m_faces;
        private Mesh m_edgeMesh;
        private Mesh m_faceMesh;
        private Transform m_label;
        private TextMesh m_title;
        private TextMesh m_detail;
        private MeshRenderer m_plate;
        private MeshRenderer m_barBack;
        private MeshRenderer m_barFill;
        private MaterialPropertyBlock m_block;
        private Transform m_content;
        private MeshRenderer m_scan;
        private bool m_scanMode;
        private bool m_scanAligned;
        private int m_scanQuarterTurns;
        private float m_scanFade;
        private bool m_shown = true;

        private Vector3 m_builtSize = new Vector3(-1f, -1f, -1f);
        private Vector3 m_targetPosition;
        private Quaternion m_targetRotation = Quaternion.identity;
        private Vector3 m_targetSize;
        private Vector3 m_shownSize;
        private bool m_hasTarget;
        private float m_targetConfidence;
        private float m_shownConfidence;
        private bool m_confirmed;
        private float m_flashUntil;
        private float m_createdTime;

        // One-Euro filters run on each NEW estimate (≈10 Hz); the display then eases toward the filtered value.
        private readonly OneEuroFilter3 m_positionFilter = new OneEuroFilter3(0.6f, 2.0f);
        private readonly OneEuroFilter m_yawFilter = new OneEuroFilter(0.4f, 0.03f);
        private readonly OneEuroFilter3 m_sizeFilter = new OneEuroFilter3(0.3f, 0.5f);
        private Vector3 m_lastRawPosition = new Vector3(float.NaN, 0f, 0f);
        private float m_lastRawYaw = float.NaN;
        private Vector3 m_lastRawSize;
        private Vector3 m_filteredPosition;
        private float m_filteredYawDeg;
        private Vector3 m_filteredSize;
        private float m_nextTextTime;

        public int TrackId { get; private set; }

        public static BoxVisual Create(Transform parent, int trackId, Material material, Font font, Transform head)
        {
            var go = new GameObject($"Object_{trackId}");
            go.transform.SetParent(parent, false);
            var visual = go.AddComponent<BoxVisual>();
            visual.Init(trackId, material, font, head);
            return visual;
        }

        private void Init(int trackId, Material material, Font font, Transform head)
        {
            TrackId = trackId;
            m_material = material;
            m_head = head;
            m_block = new MaterialPropertyBlock();
            m_createdTime = Time.time;

            m_edgeMesh = new Mesh { name = "QD3D_Edges" };
            m_faceMesh = new Mesh { name = "QD3D_Faces" };
            m_content = new GameObject("Content").transform;
            m_content.SetParent(transform, false);
            m_edges = MeshUtil.CreateMeshObject(m_content, "Edges", m_edgeMesh, material, Vector3.zero);
            m_faces = MeshUtil.CreateMeshObject(m_content, "Faces", m_faceMesh, material, Vector3.zero);
            var scanMesh = ScanModel.Get();
            if (scanMesh != null)
            {
                m_scan = MeshUtil.CreateMeshObject(m_content, "Scan", scanMesh, material, Vector3.zero);
                m_scan.enabled = false;
            }

            m_label = new GameObject("Label").transform;
            m_label.SetParent(m_content, false);
            m_plate = MeshUtil.CreateMeshObject(m_label, "Plate", MeshUtil.CreateRect(0.26f, 0.095f), material,
                new Vector3(0f, 0f, 0.003f));
            m_barBack = MeshUtil.CreateMeshObject(m_label, "BarBack", MeshUtil.CreateRect(0.21f, 0.008f), material,
                new Vector3(0f, -0.033f, 0.0015f));
            m_barFill = MeshUtil.CreateMeshObject(m_label, "BarFill", MeshUtil.CreateRect(1f, 0.008f, 0.5f), material,
                new Vector3(-0.105f, -0.033f, 0.001f));
            m_title = MeshUtil.CreateText(m_label, "Title", font, 0.034f, new Vector3(0f, 0.018f, 0f), Color.white);
            m_detail = MeshUtil.CreateText(m_label, "Detail", font, 0.022f, new Vector3(0f, -0.012f, 0f),
                new Color(0.86f, 0.9f, 0.95f));
        }

        /// <summary>Hide/show everything (used for the minimum-confidence threshold). Tracking continues.</summary>
        public void SetShown(bool shown)
        {
            if (shown == m_shown)
            {
                return;
            }

            m_shown = shown;
            m_content.gameObject.SetActive(shown);
        }

        public bool Shown => m_shown;

        /// <summary>Scan mode on/off and the backrest side (quarter turns of the canonical scan; -1 = unknown).</summary>
        public void SetScan(bool enabled, int quarterTurns)
        {
            m_scanMode = enabled && m_scan != null;
            var aligned = quarterTurns >= 0;
            if (aligned && (!m_scanAligned || quarterTurns != m_scanQuarterTurns))
            {
                m_scanFade = 0f; // fade in on first alignment and when the side flips
            }

            m_scanAligned = aligned;
            if (aligned)
            {
                m_scanQuarterTurns = quarterTurns & 3;
            }
        }

        public void Apply(TrackedObject track, in CategoryInfo info)
        {
            var box = track.Estimate;
            var now = Time.time;
            var yawDeg = box.Yaw * Mathf.Rad2Deg;
            m_targetConfidence = track.Confidence;
            if (!m_hasTarget)
            {
                m_positionFilter.Reset(box.BottomCenter, now);
                m_yawFilter.Reset(yawDeg, now);
                m_sizeFilter.Reset(box.Size, now);
                m_filteredPosition = box.BottomCenter;
                m_filteredYawDeg = yawDeg;
                m_filteredSize = box.Size;
                transform.SetPositionAndRotation(box.BottomCenter, Quaternion.Euler(0f, yawDeg, 0f));
                m_shownSize = box.Size;
                m_shownConfidence = m_targetConfidence;
                m_hasTarget = true;
            }
            else if (box.BottomCenter != m_lastRawPosition || box.Yaw != m_lastRawYaw || box.Size != m_lastRawSize)
            {
                if (track.Relocated)
                {
                    // The object really moved: snap instead of crawling.
                    m_positionFilter.Reset(box.BottomCenter, now);
                    m_yawFilter.Reset(yawDeg, now);
                    m_sizeFilter.Reset(box.Size, now);
                    track.Relocated = false;
                }

                m_filteredPosition = m_positionFilter.Filter(box.BottomCenter, now);
                // Unwrap yaw next to the filtered value so 179° → -179° doesn't spin the box.
                var unwrapped = m_yawFilter.Value + Mathf.DeltaAngle(m_yawFilter.Value, yawDeg);
                m_filteredYawDeg = m_yawFilter.Filter(unwrapped, now);
                m_filteredSize = m_sizeFilter.Filter(box.Size, now);
            }

            m_lastRawPosition = box.BottomCenter;
            m_lastRawYaw = box.Yaw;
            m_lastRawSize = box.Size;
            m_targetPosition = m_filteredPosition;
            m_targetRotation = Quaternion.Euler(0f, m_filteredYawDeg, 0f);
            m_targetSize = m_filteredSize;

            var confirmed = track.State == TrackState.Confirmed;
            if (confirmed && !m_confirmed)
            {
                m_flashUntil = Time.time + 0.6f;
            }

            m_confirmed = confirmed;
            if (now < m_nextTextTime)
            {
                return;
            }

            m_nextTextTime = now + 0.25f;
            var percent = Mathf.RoundToInt(track.Confidence * 100f);
            if (confirmed)
            {
                m_title.text = $"<b>{info.DisplayName}</b>  {percent}%";
                m_detail.text = m_scanMode && !m_scanAligned
                    ? "aligning scan… look at the backrest"
                    : $"{m_filteredSize.x * 100f:F0} × {m_filteredSize.z * 100f:F0} × {m_filteredSize.y * 100f:F0} cm   ±{Mathf.Max(1f, track.UncertaintyCm):F0} cm";
            }
            else
            {
                m_title.text = $"<b>{info.DisplayName}?</b>  {percent}%";
                m_detail.text = "keep looking…";
            }
        }

        private void LateUpdate()
        {
            if (!m_hasTarget)
            {
                return;
            }

            var dt = Time.deltaTime;
            var k = 1f - Mathf.Exp(-14f * dt);
            transform.position = Vector3.Lerp(transform.position, m_targetPosition, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, m_targetRotation, k);
            m_shownSize = Vector3.Lerp(m_shownSize, m_targetSize, k);
            m_shownConfidence = Mathf.Lerp(m_shownConfidence, m_targetConfidence, 1f - Mathf.Exp(-4f * dt));

            if (Mathf.Abs(m_shownSize.x - m_builtSize.x) > 0.006f || Mathf.Abs(m_shownSize.y - m_builtSize.y) > 0.006f ||
                Mathf.Abs(m_shownSize.z - m_builtSize.z) > 0.006f)
            {
                RebuildMeshes(m_shownSize);
            }

            UpdateScan(dt);
            UpdateColors();
            UpdateLabel();
        }

        private void UpdateScan(float dt)
        {
            if (m_scan == null)
            {
                return;
            }

            var show = m_scanMode && m_scanAligned;
            if (m_scan.enabled != show)
            {
                m_scan.enabled = show;
            }

            if (!show)
            {
                return;
            }

            m_scanFade = Mathf.MoveTowards(m_scanFade, 1f, dt / 0.5f);
            var odd = (m_scanQuarterTurns & 1) != 0;
            var t = m_scan.transform;
            t.localRotation = Quaternion.Euler(0f, 90f * m_scanQuarterTurns, 0f);
            // Scale is applied in the mesh frame before rotation → x/z swap for a 90° turn.
            t.localScale = odd
                ? new Vector3(m_shownSize.z, m_shownSize.y, m_shownSize.x)
                : m_shownSize;
        }

        private void UpdateColors()
        {
            var baseColor = MeshUtil.ConfidenceColor(m_shownConfidence);
            float alpha;
            if (m_confirmed)
            {
                alpha = 1f;
                if (Time.time < m_flashUntil)
                {
                    var f = (m_flashUntil - Time.time) / 0.6f;
                    baseColor = Color.Lerp(baseColor, Color.white, 0.6f * f);
                }
            }
            else
            {
                // Tentative: soft pulse so it reads as "still thinking".
                alpha = 0.45f + 0.2f * Mathf.Sin((Time.time - m_createdTime) * 5f);
            }

            var scanVisible = m_scan != null && m_scan.enabled;
            var edgeColor = baseColor;
            edgeColor.a = scanVisible ? 0.3f * alpha : alpha;
            MeshUtil.SetColor(m_edges, m_block, edgeColor);
            var faceColor = baseColor;
            faceColor.a = scanVisible ? 0f : alpha;
            MeshUtil.SetColor(m_faces, m_block, faceColor);
            if (scanVisible)
            {
                var cyan = new Color(0.3f, 0.9f, 1f, 0.32f * m_scanFade * (m_confirmed ? 1f : 0.6f));
                MeshUtil.SetColor(m_scan, m_block, cyan);
            }

            MeshUtil.SetColor(m_plate, m_block, new Color(0.04f, 0.05f, 0.07f, 0.72f));
            MeshUtil.SetColor(m_barBack, m_block, new Color(1f, 1f, 1f, 0.16f));
            var bar = baseColor;
            bar.a = 0.95f;
            MeshUtil.SetColor(m_barFill, m_block, bar);
            m_barFill.transform.localScale = new Vector3(Mathf.Max(0.002f, 0.21f * m_shownConfidence), 1f, 1f);
        }

        private void UpdateLabel()
        {
            var top = transform.position + Vector3.up * (m_shownSize.y + 0.075f);
            if (m_head == null)
            {
                m_label.position = top;
                return;
            }

            var toLabel = top - m_head.position;
            var distance = toLabel.magnitude;
            var scale = Mathf.Clamp(distance / 1.3f, 0.75f, 2.4f);
            m_label.position = top + Vector3.up * (0.02f * (scale - 1f));
            m_label.rotation = Quaternion.LookRotation(toLabel.normalized, Vector3.up);
            m_label.localScale = Vector3.one * scale;
        }

        private void RebuildMeshes(Vector3 size)
        {
            m_builtSize = size;
            var corners = new Vector3[8];
            for (var i = 0; i < 8; i++)
            {
                corners[i] = new Vector3(
                    (i & 1) != 0 ? 0.5f * size.x : -0.5f * size.x,
                    (i & 2) != 0 ? size.y : 0f,
                    (i & 4) != 0 ? 0.5f * size.z : -0.5f * size.z);
            }

            var v = new List<Vector3>(400);
            var c = new List<Color>(400);
            var t = new List<int>(600);
            var edgeTint = new Color(1f, 1f, 1f, 0.55f);
            var bracketTint = Color.white;
            var bracketLen = Mathf.Clamp(0.22f * Mathf.Min(size.x, Mathf.Min(size.y, size.z)), 0.012f, 0.12f);
            for (var a = 0; a < 8; a++)
            {
                for (var bit = 1; bit <= 4; bit <<= 1)
                {
                    var b = a ^ bit;
                    if (b < a)
                    {
                        continue;
                    }

                    MeshUtil.AddPrism(v, c, t, corners[a], corners[b], EdgeHalfThickness, edgeTint);
                }

                // Corner brackets: short, bolder segments along the three edges leaving each corner.
                for (var bit = 1; bit <= 4; bit <<= 1)
                {
                    var b = a ^ bit;
                    var dir = (corners[b] - corners[a]).normalized;
                    MeshUtil.AddPrism(v, c, t, corners[a], corners[a] + dir * bracketLen, BracketHalfThickness,
                        bracketTint);
                }
            }

            MeshUtil.Apply(m_edgeMesh, v, c, t);

            v.Clear();
            c.Clear();
            t.Clear();
            var face = new Color(1f, 1f, 1f, 0.06f);
            var footprint = new Color(1f, 1f, 1f, 0.18f);
            // Bottom (footprint, slightly lifted to avoid z-fighting with the real floor).
            var lift = new Vector3(0f, 0.003f, 0f);
            MeshUtil.AddQuad(v, c, t, corners[0] + lift, corners[1] + lift, corners[5] + lift, corners[4] + lift, footprint);
            MeshUtil.AddQuad(v, c, t, corners[2], corners[3], corners[7], corners[6], face); // top
            MeshUtil.AddQuad(v, c, t, corners[0], corners[1], corners[3], corners[2], face); // back (-z)
            MeshUtil.AddQuad(v, c, t, corners[4], corners[5], corners[7], corners[6], face); // front (+z)
            MeshUtil.AddQuad(v, c, t, corners[0], corners[4], corners[6], corners[2], face); // left
            MeshUtil.AddQuad(v, c, t, corners[1], corners[5], corners[7], corners[3], face); // right
            MeshUtil.Apply(m_faceMesh, v, c, t);
        }

        private void OnDestroy()
        {
            if (m_edgeMesh != null)
            {
                Destroy(m_edgeMesh);
            }

            if (m_faceMesh != null)
            {
                Destroy(m_faceMesh);
            }
        }
    }
}
