// In-headset start menu (controllers): choose the display mode and the minimum confidence to show a box.
//   Thumbstick up/down   select row
//   Thumbstick left/right change value
//   A / right trigger    start a new session (clears all objects)
using System;
using System.Text;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class SessionMenu : MonoBehaviour
    {
        private const float Distance = 0.9f;
        private const float RecenterAngle = 35f;
        private const float StickThreshold = 0.6f;
        private const float RepeatDelay = 0.35f;
        private const float RepeatRate = 0.12f;
        private const int RowCount = 3;

        private Transform m_head;
        private TextMesh m_title;
        private TextMesh m_body;
        private MeshRenderer m_plate;
        private MaterialPropertyBlock m_block;
        private AppSettings m_settings;
        private readonly StringBuilder m_sb = new StringBuilder(512);

        private int m_row;
        private Vector2Int m_heldDir;
        private float m_nextRepeat;
        private bool m_placed;
        private Vector3 m_targetPos;
        private float m_openedTime;

        public event Action Started;

        public bool IsOpen => gameObject.activeSelf;

        public static SessionMenu Create(Material material, Font font, Transform head, AppSettings settings)
        {
            var go = new GameObject("SessionMenu");
            var menu = go.AddComponent<SessionMenu>();
            menu.m_head = head;
            menu.m_settings = settings;
            menu.m_block = new MaterialPropertyBlock();
            menu.m_plate = MeshUtil.CreateMeshObject(go.transform, "Plate", MeshUtil.CreateRect(0.62f, 0.36f), material,
                new Vector3(0f, 0f, 0.004f));
            MeshUtil.SetColor(menu.m_plate, menu.m_block, new Color(0.03f, 0.04f, 0.06f, 0.86f));
            menu.m_title = MeshUtil.CreateText(go.transform, "Title", font, 0.03f, new Vector3(0f, 0.135f, 0f),
                Color.white);
            menu.m_body = MeshUtil.CreateText(go.transform, "Body", font, 0.019f, new Vector3(-0.27f, -0.02f, 0f),
                Color.white, TextAnchor.MiddleLeft);
            menu.m_body.alignment = TextAlignment.Left;
            menu.m_title.text = "<b>QuestDetect3D</b>  ·  chair detection";
            go.SetActive(false);
            return menu;
        }

        public void Open()
        {
            gameObject.SetActive(true);
            m_placed = false;
            m_row = RowCount - 1; // cursor on "Start"
            m_openedTime = Time.time;
            m_heldDir = Vector2Int.zero;
            Refresh();
        }

        public void Close() => gameObject.SetActive(false);

        private void Update()
        {
            // Ignore the button press that opened the menu.
            if (Time.time - m_openedTime < 0.3f)
            {
                return;
            }

            var dir = ReadStick();
            if (dir != Vector2Int.zero)
            {
                var fire = dir != m_heldDir || Time.time >= m_nextRepeat;
                if (fire)
                {
                    m_nextRepeat = Time.time + (dir != m_heldDir ? RepeatDelay : RepeatRate);
                    Navigate(dir);
                }
            }

            m_heldDir = dir;

            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            {
                if (m_row == 0)
                {
                    ToggleMode();
                }
                else
                {
                    m_settings.Save();
                    Started?.Invoke();
                }
            }
        }

        private void Navigate(Vector2Int dir)
        {
            if (dir.y != 0)
            {
                m_row = (m_row - dir.y + RowCount) % RowCount;
            }
            else if (dir.x != 0)
            {
                if (m_row == 0)
                {
                    ToggleMode();
                }
                else if (m_row == 1)
                {
                    m_settings.StepMinConfidence(dir.x);
                }
            }

            Refresh();
        }

        private void ToggleMode()
        {
            m_settings.Mode = m_settings.Mode == DisplayMode.Box ? DisplayMode.ScanAndKeypoints : DisplayMode.Box;
            Refresh();
        }

        private static Vector2Int ReadStick()
        {
            var a = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            var b = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            var v = a.sqrMagnitude >= b.sqrMagnitude ? a : b;
            if (v.magnitude < StickThreshold)
            {
                return Vector2Int.zero;
            }

            return Mathf.Abs(v.x) > Mathf.Abs(v.y)
                ? new Vector2Int(v.x > 0f ? 1 : -1, 0)
                : new Vector2Int(0, v.y > 0f ? 1 : -1);
        }

        private void Refresh()
        {
            const string sel = "<color=#4FE3FF>> ";
            const string off = "<color=#C8D0DA>   ";
            m_sb.Clear();
            var modeName = m_settings.ShowScan ? "3D scan + cyan keypoints" : "Box";
            m_sb.Append(m_row == 0 ? sel : off).Append("Display:  <b>< ").Append(modeName).Append(" ></b></color>\n");
            m_sb.Append("<size=44><color=#8A96A3>      ")
                .Append(m_settings.ShowScan
                    ? "lab-chair scan fitted into each box, oriented by depth"
                    : "clean 3D box with size and confidence")
                .Append("</color></size>\n\n");
            var pct = Mathf.RoundToInt(m_settings.MinConfidence * 100f);
            m_sb.Append(m_row == 1 ? sel : off).Append("Show boxes from:  <b>< ").Append(pct).Append("% ></b></color>\n");
            m_sb.Append("<size=44><color=#8A96A3>      ")
                .Append(pct >= 55
                    ? "only confirmed chairs (confirmation happens at 55%)"
                    : "also shows unconfirmed candidates")
                .Append("</color></size>\n\n");
            m_sb.Append(m_row == 2 ? sel : off).Append("<b>Start</b></color>\n\n");
            m_sb.Append("<size=42><color=#8A96A3>Stick: select / change    A: start    In session, A: this menu</color></size>");
            m_body.text = m_sb.ToString();
        }

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
            var desired = m_head.position + flat * Distance + Vector3.down * 0.08f;
            var toCurrent = (transform.position - m_head.position).normalized;
            if (!m_placed || Vector3.Angle(toCurrent, (desired - m_head.position).normalized) > RecenterAngle)
            {
                m_targetPos = desired;
                if (!m_placed)
                {
                    transform.position = desired;
                }

                m_placed = true;
            }

            var k = 1f - Mathf.Exp(-5f * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, m_targetPos, k);
            transform.rotation = Quaternion.LookRotation((transform.position - m_head.position).normalized, Vector3.up);
        }
    }
}
