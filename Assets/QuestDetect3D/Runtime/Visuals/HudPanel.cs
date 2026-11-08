// Small status panel that lazily follows the head (stays put until you look away).
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class HudPanel : MonoBehaviour
    {
        private const float Distance = 0.85f;
        private const float DropBelowEyes = 0.28f;
        private const float RecenterAngle = 28f;

        private Transform m_head;
        private TextMesh m_text;
        private MeshRenderer m_plate;
        private MaterialPropertyBlock m_block;
        private Vector3 m_targetPos;
        private Quaternion m_targetRot;
        private bool m_placed;

        public static HudPanel Create(Material material, Font font, Transform head)
        {
            var go = new GameObject("HUD");
            var hud = go.AddComponent<HudPanel>();
            hud.m_head = head;
            hud.m_block = new MaterialPropertyBlock();
            hud.m_plate = MeshUtil.CreateMeshObject(go.transform, "Plate", MeshUtil.CreateRect(0.46f, 0.2f), material,
                new Vector3(0f, 0f, 0.004f));
            hud.m_text = MeshUtil.CreateText(go.transform, "Text", font, 0.0155f, Vector3.zero, Color.white);
            hud.m_text.alignment = TextAlignment.Left;
            hud.m_text.anchor = TextAnchor.MiddleCenter;
            MeshUtil.SetColor(hud.m_plate, hud.m_block, new Color(0.03f, 0.04f, 0.06f, 0.7f));
            return hud;
        }

        public void SetText(string text) => m_text.text = text;

        public void SetVisible(bool visible) => gameObject.SetActive(visible);

        private void LateUpdate()
        {
            if (m_head == null)
            {
                return;
            }

            var flat = Vector3.ProjectOnPlane(m_head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
            {
                flat = m_head.forward;
            }

            flat.Normalize();
            var desired = m_head.position + flat * Distance + Vector3.down * DropBelowEyes;
            var toCurrent = (transform.position - m_head.position).normalized;
            var toDesired = (desired - m_head.position).normalized;
            if (!m_placed || Vector3.Angle(toCurrent, toDesired) > RecenterAngle ||
                Vector3.Distance(transform.position, desired) > 0.6f)
            {
                m_targetPos = desired;
                m_placed = true;
            }

            var k = 1f - Mathf.Exp(-5f * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, m_targetPos, k);
            m_targetRot = Quaternion.LookRotation((transform.position - m_head.position).normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, m_targetRot, k);
        }
    }
}
