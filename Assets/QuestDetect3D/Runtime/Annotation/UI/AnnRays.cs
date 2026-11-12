// Controller rays for the Annotate panels: one Meta Interaction SDK RayInteractor per Touch controller, built from
// code (ray origin = the OVRCameraRig controller anchor, select = index trigger), plus a thin ray + cursor visual
// that only shows while the ray is on a panel.
using System;
using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    /// <summary>Index trigger of one controller as an Interaction SDK selector.</summary>
    public sealed class AnnTriggerSelector : MonoBehaviour, ISelector
    {
        public OVRInput.Controller Controller = OVRInput.Controller.RTouch;
        private bool m_down;

        public event Action WhenSelected = delegate { };
        public event Action WhenUnselected = delegate { };

        private void Update()
        {
            var down = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, Controller);
            if (down == m_down)
            {
                return;
            }

            m_down = down;
            if (down)
            {
                WhenSelected();
            }
            else
            {
                WhenUnselected();
            }
        }

        private void OnDisable()
        {
            if (m_down)
            {
                m_down = false;
                WhenUnselected(); // keep the 1:1 select / unselect contract
            }
        }
    }

    public sealed class AnnRay : MonoBehaviour
    {
        private const float RayHalfThickness = 0.0012f;
        private const float CursorSize = 0.014f;

        private RayInteractor m_interactor;
        private Transform m_line;
        private Transform m_cursor;
        private MeshRenderer m_lineRenderer;
        private MeshRenderer m_cursorRenderer;
        private MaterialPropertyBlock m_block;

        public RayInteractor Interactor => m_interactor;

        /// <summary>True while this ray is over (or pressing) a panel: world input must ignore the trigger then.</summary>
        public bool IsOnPanel => m_interactor != null && m_interactor.isActiveAndEnabled && m_interactor.HasInteractable;

        public static AnnRay Create(Transform controllerAnchor, OVRInput.Controller controller, Material material)
        {
            var go = new GameObject(controller == OVRInput.Controller.RTouch ? "AnnRay Right" : "AnnRay Left");
            go.SetActive(false);
            go.transform.SetParent(controllerAnchor, false);
            var ray = go.AddComponent<AnnRay>();
            var selector = go.AddComponent<AnnTriggerSelector>();
            selector.Controller = controller;
            ray.m_interactor = go.AddComponent<RayInteractor>();
            ray.m_interactor.InjectAllRayInteractor(selector, go.transform);
            ray.m_block = new MaterialPropertyBlock();

            var v = new List<Vector3>();
            var c = new List<Color>();
            var t = new List<int>();
            MeshUtil.AddPrism(v, c, t, Vector3.zero, Vector3.forward, RayHalfThickness, Color.white);
            var lineMesh = new Mesh { name = "AnnRayLine" };
            MeshUtil.Apply(lineMesh, v, c, t);
            // Visuals live in world space (not under the controller) so they can follow the interactor's ray.
            ray.m_lineRenderer = MeshUtil.CreateMeshObject(null, go.name + " Line", lineMesh, material, Vector3.zero);
            ray.m_cursorRenderer = MeshUtil.CreateMeshObject(null, go.name + " Cursor",
                MeshUtil.CreateRect(CursorSize, CursorSize), material, Vector3.zero);
            ray.m_line = ray.m_lineRenderer.transform;
            ray.m_cursor = ray.m_cursorRenderer.transform;
            ray.m_line.gameObject.SetActive(false);
            ray.m_cursor.gameObject.SetActive(false);
            return ray;
        }

        public void SetActive(bool active)
        {
            gameObject.SetActive(active);
            if (!active)
            {
                m_line.gameObject.SetActive(false);
                m_cursor.gameObject.SetActive(false);
            }
        }

        private void LateUpdate()
        {
            var show = IsOnPanel;
            if (m_line.gameObject.activeSelf != show)
            {
                m_line.gameObject.SetActive(show);
                m_cursor.gameObject.SetActive(show);
            }

            if (!show)
            {
                return;
            }

            var origin = m_interactor.Origin;
            var end = m_interactor.End;
            var delta = end - origin;
            var length = delta.magnitude;
            if (length < 1e-4f)
            {
                return;
            }

            var pressing = m_interactor.State == InteractorState.Select;
            var color = pressing ? AnnPanel.Accent : new Color(1f, 1f, 1f, 0.75f);
            m_line.SetPositionAndRotation(origin, Quaternion.LookRotation(delta / length));
            m_line.localScale = new Vector3(1f, 1f, length);
            MeshUtil.SetColor(m_lineRenderer, m_block, color);

            var normal = m_interactor.CollisionInfo.HasValue ? m_interactor.CollisionInfo.Value.Normal : -delta / length;
            m_cursor.SetPositionAndRotation(end + normal * 0.003f, Quaternion.LookRotation(-normal));
            m_cursor.localScale = Vector3.one * (pressing ? 0.7f : 1f);
            MeshUtil.SetColor(m_cursorRenderer, m_block, color);
        }

        private void OnDestroy()
        {
            if (m_line != null)
            {
                Destroy(m_line.gameObject);
            }

            if (m_cursor != null)
            {
                Destroy(m_cursor.gameObject);
            }
        }
    }
}
