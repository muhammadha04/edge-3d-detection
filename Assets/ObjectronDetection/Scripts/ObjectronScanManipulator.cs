// Spawn and manipulate the scanned chair mesh with Quest controller grab (grip/trigger).

using System.Collections.Generic;
using UnityEngine;

namespace QuestObjectron
{
    public class ObjectronScanManipulator : MonoBehaviour
    {
        private const float SpawnRayMaxDistanceM = 8f;
        private const float TriggerGrabThreshold = 0.45f;
        private const float GripGrabThreshold = 0.45f;

        private static readonly string[] s_resourcePaths =
        {
            "ScanCalibration/LabChair",
            "ScanCalibration/lab-chair",
            "lab-chair",
        };

        [SerializeField] private GameObject m_scanModelPrefab;
        [SerializeField] private string m_resourcesPrefabPath = "ScanCalibration/LabChair";
        [SerializeField] private LayerMask m_raycastMask = ~0;

        private Transform m_scanRoot;
        private bool m_isFrozen;
        private Bounds m_meshBoundsLocal = new(Vector3.zero, Vector3.one);
        private OVRCameraRig m_cameraRig;

#if UNITY_ANDROID && !UNITY_EDITOR
        private Vector3? m_moveGrabOffset;
        private Quaternion? m_rotateGrabOffset;
        private bool m_twoHandScaleActive;
        private float m_twoHandStartDistance;
        private Vector3 m_twoHandStartScale;
        private Vector3 m_twoHandStartPosition;
        private Vector3 m_twoHandStartMidpoint;
#endif

        public bool HasSpawned => m_scanRoot != null;
        public bool IsFrozen => m_isFrozen;
        public Transform ScanRoot => m_scanRoot;

        public void Configure(GameObject scanPrefab, string resourcesPath = null)
        {
            if (scanPrefab != null)
            {
                m_scanModelPrefab = scanPrefab;
            }

            if (!string.IsNullOrEmpty(resourcesPath))
            {
                m_resourcesPrefabPath = resourcesPath;
            }
        }

        public bool PreloadResources()
        {
            return ResolvePrefab() != null;
        }

        public void Clear()
        {
            if (m_scanRoot != null)
            {
                Destroy(m_scanRoot.gameObject);
                m_scanRoot = null;
            }

            ResetGrabState();
            m_isFrozen = false;
        }

        public bool TrySpawnAtDetectionBox(Vector3[] detectionCorners)
        {
            if (HasSpawned || detectionCorners == null || detectionCorners.Length < 9)
            {
                return false;
            }

            var center = detectionCorners[0];
            var rotation = Quaternion.identity;
            ObjectronOrientedBoxFitter.TryFitTransform(detectionCorners, out _, out rotation, out _);

            return TrySpawnAtPlacement(new ObjectronScanMeshPlacement
            {
                Position = center,
                Rotation = rotation,
                LocalScale = Vector3.one,
                IsValid = true,
            });
        }

        /// <summary>Automatic overlay placement using saved calibration (chair detection only).</summary>
        public bool TrySpawnAtCalibratedBox(Vector3[] detectionCorners, Vector3? modelScale = null)
        {
            if (HasSpawned)
            {
                QuestObjectronLogger.Viz("scan_calibration spawn skipped — scan already spawned");
                return false;
            }

            var calibration = ObjectronScanCalibrationDefaults.Get();
            if (calibration == null || !calibration.TryApplyMeshPlacement(detectionCorners, modelScale, out var placement))
            {
                QuestObjectronLogger.Err("scan_calibration spawn failed — no default calibration for detection box");
                return false;
            }

            return TrySpawnAtPlacement(placement);
        }

        private bool TrySpawnAtPlacement(ObjectronScanMeshPlacement placement)
        {
            if (HasSpawned || !placement.IsValid)
            {
                return false;
            }

            var prefab = ResolvePrefab();
            if (prefab == null)
            {
                QuestObjectronLogger.Err("scan_calibration spawn failed — lab-chair prefab missing");
                return false;
            }

            var instance = Instantiate(prefab, placement.Position, placement.Rotation);
            instance.name = "ScanCalibration_Chair";
            instance.transform.localScale = placement.LocalScale;
            EnsureVisibleMaterials(instance);
            m_scanRoot = instance.transform;
            m_meshBoundsLocal = ComputeMeshBoundsLocal(instance);
            m_isFrozen = false;
            ResetGrabState();
            QuestObjectronLogger.Viz(
                $"scan_calibration spawned calibrated pos=({placement.Position.x:F2},{placement.Position.y:F2},{placement.Position.z:F2}) " +
                $"scale=({placement.LocalScale.x:F2},{placement.LocalScale.y:F2},{placement.LocalScale.z:F2})");
            return true;
        }

        public bool TrySpawnAtControllerAim()
        {
            if (HasSpawned)
            {
                QuestObjectronLogger.Viz("scan_calibration spawn skipped — scan already spawned");
                return false;
            }

            var controller = GetRightController();
            if (controller == null)
            {
                QuestObjectronLogger.Err("scan_calibration spawn failed — right controller not found");
                return false;
            }

            var spawnPoint = ResolveAimPoint(controller);
            var prefab = ResolvePrefab();
            if (prefab == null)
            {
                QuestObjectronLogger.Err("scan_calibration spawn failed — lab-chair prefab missing");
                return false;
            }

            var instance = Instantiate(prefab, spawnPoint, controller.rotation);
            instance.name = "ScanCalibration_Chair";
            EnsureVisibleMaterials(instance);
            m_scanRoot = instance.transform;
            m_meshBoundsLocal = ComputeMeshBoundsLocal(instance);
            m_isFrozen = false;
            ResetGrabState();
            QuestObjectronLogger.Viz(
                $"scan_calibration spawned pos=({spawnPoint.x:F2},{spawnPoint.y:F2},{spawnPoint.z:F2}) " +
                $"bounds=({m_meshBoundsLocal.size.x:F2},{m_meshBoundsLocal.size.y:F2},{m_meshBoundsLocal.size.z:F2})");
            return true;
        }

        public void UpdateManipulation()
        {
            if (!HasSpawned || m_isFrozen)
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            var leftController = GetLeftController();
            var rightController = GetRightController();
            if (rightController == null)
            {
                return;
            }

            var leftGrip = leftController != null
                && OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch) >= GripGrabThreshold;
            var rightGrip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch) >= GripGrabThreshold;
            var rightTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch) >= TriggerGrabThreshold;
            var twoHandScale = leftGrip && rightGrip && leftController != null;

            if (twoHandScale)
            {
                ApplyTwoHandScale(leftController, rightController);
                m_moveGrabOffset = null;
                m_rotateGrabOffset = null;
                return;
            }

            m_twoHandScaleActive = false;

            if (rightGrip)
            {
                if (!m_moveGrabOffset.HasValue)
                {
                    m_moveGrabOffset = m_scanRoot.position - rightController.position;
                }

                m_scanRoot.position = rightController.position + m_moveGrabOffset.Value;
            }
            else
            {
                m_moveGrabOffset = null;
            }

            if (rightTrigger)
            {
                if (!m_rotateGrabOffset.HasValue)
                {
                    m_rotateGrabOffset = Quaternion.Inverse(rightController.rotation) * m_scanRoot.rotation;
                }

                m_scanRoot.rotation = rightController.rotation * m_rotateGrabOffset.Value;
            }
            else
            {
                m_rotateGrabOffset = null;
            }
#endif
        }

        public bool TryFreeze()
        {
            if (!HasSpawned || m_isFrozen)
            {
                return false;
            }

            m_isFrozen = true;
            ResetGrabState();
            QuestObjectronLogger.Viz(
                $"scan_calibration frozen pos=({m_scanRoot.position.x:F2},{m_scanRoot.position.y:F2},{m_scanRoot.position.z:F2}) " +
                $"rot=({m_scanRoot.rotation.eulerAngles.x:F0},{m_scanRoot.rotation.eulerAngles.y:F0},{m_scanRoot.rotation.eulerAngles.z:F0}) " +
                $"scale=({m_scanRoot.lossyScale.x:F2},{m_scanRoot.lossyScale.y:F2},{m_scanRoot.lossyScale.z:F2})");
            return true;
        }

        public Bounds MeshBoundsLocal => m_meshBoundsLocal;

#if UNITY_ANDROID && !UNITY_EDITOR
        private void ApplyTwoHandScale(Transform leftController, Transform rightController)
        {
            var leftPos = leftController.position;
            var rightPos = rightController.position;
            var midpoint = (leftPos + rightPos) * 0.5f;
            var distance = Vector3.Distance(leftPos, rightPos);
            if (distance < 0.05f)
            {
                return;
            }

            if (!m_twoHandScaleActive)
            {
                m_twoHandScaleActive = true;
                m_twoHandStartDistance = distance;
                m_twoHandStartScale = m_scanRoot.localScale;
                m_twoHandStartPosition = m_scanRoot.position;
                m_twoHandStartMidpoint = midpoint;
                return;
            }

            var scaleFactor = distance / Mathf.Max(m_twoHandStartDistance, 0.05f);
            m_scanRoot.localScale = m_twoHandStartScale * scaleFactor;
            m_scanRoot.position = m_twoHandStartPosition + (midpoint - m_twoHandStartMidpoint);
        }
#endif

        private void ResetGrabState()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_moveGrabOffset = null;
            m_rotateGrabOffset = null;
            m_twoHandScaleActive = false;
#endif
        }

        private GameObject ResolvePrefab()
        {
            if (m_scanModelPrefab != null)
            {
                return m_scanModelPrefab;
            }

            var paths = BuildResourcePathCandidates();
            foreach (var path in paths)
            {
                var loaded = Resources.Load<GameObject>(path);
                if (loaded != null)
                {
                    m_scanModelPrefab = loaded;
                    QuestObjectronLogger.Boot($"scan_calibration loaded chair from Resources/{path}");
                    return m_scanModelPrefab;
                }
            }

            var folderModels = Resources.LoadAll<GameObject>("ScanCalibration");
            if (folderModels != null && folderModels.Length > 0)
            {
                m_scanModelPrefab = folderModels[0];
                QuestObjectronLogger.Boot($"scan_calibration loaded chair from Resources/ScanCalibration ({folderModels[0].name})");
                return m_scanModelPrefab;
            }

            QuestObjectronLogger.Err(
                "scan_calibration lab-chair missing from build — add Assets/Resources/ScanCalibration/LabChair.obj and rebuild");
            return null;
        }

        private IEnumerable<string> BuildResourcePathCandidates()
        {
            if (!string.IsNullOrEmpty(m_resourcesPrefabPath))
            {
                yield return m_resourcesPrefabPath;
            }

            foreach (var path in s_resourcePaths)
            {
                yield return path;
            }
        }

        public static void EnsureVisibleMaterials(GameObject instance)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Standard");
            if (shader == null)
            {
                return;
            }

            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
            {
                var mats = renderer.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null)
                    {
                        mats[i] = new Material(shader) { color = new Color(0.72f, 0.74f, 0.78f, 1f) };
                    }
                }

                renderer.sharedMaterials = mats;
            }
        }

        private Transform GetLeftController()
        {
            m_cameraRig ??= FindAnyObjectByType<OVRCameraRig>();
            return m_cameraRig != null ? m_cameraRig.leftControllerAnchor : null;
        }

        private Transform GetRightController()
        {
            m_cameraRig ??= FindAnyObjectByType<OVRCameraRig>();
            return m_cameraRig != null ? m_cameraRig.rightControllerAnchor : null;
        }

        private Vector3 ResolveAimPoint(Transform controller)
        {
            var origin = controller.position;
            var direction = controller.forward;
            if (Physics.Raycast(origin, direction, out var hit, SpawnRayMaxDistanceM, m_raycastMask, QueryTriggerInteraction.Ignore))
            {
                return hit.point;
            }

            return origin + direction * 1.5f;
        }

        private static Bounds ComputeMeshBoundsLocal(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }

            var bounds = renderers[0].bounds;
            var centerLocal = root.transform.InverseTransformPoint(bounds.center);
            var extentsWorld = bounds.extents;
            var sizeLocal = new Vector3(
                extentsWorld.x * 2f / Mathf.Max(root.transform.lossyScale.x, 0.0001f),
                extentsWorld.y * 2f / Mathf.Max(root.transform.lossyScale.y, 0.0001f),
                extentsWorld.z * 2f / Mathf.Max(root.transform.lossyScale.z, 0.0001f));
            return new Bounds(centerLocal, sizeLocal);
        }
    }
}
