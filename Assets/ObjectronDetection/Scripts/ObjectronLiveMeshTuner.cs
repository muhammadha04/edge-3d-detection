// Select and tune chair mesh overlays during live detection; save spawns-relative calibration.

using UnityEngine;

namespace QuestObjectron
{
    public class ObjectronLiveMeshTuner : MonoBehaviour
    {
        private const float TriggerGrabThreshold = 0.45f;
        private const float GripGrabThreshold = 0.45f;

        private ObjectronScanMeshVisuals m_meshVisuals;
        private ObjectronChairDetectionManager m_chairManager;
        private OVRCameraRig m_cameraRig;
        private Transform m_target;
        private int m_selectedPoolIndex = -1;
        private ObjectronScanCalibrationRecord.SpawnReference m_spawnReference;
        private Quaternion m_uprightBaseRotation = Quaternion.identity;

#if UNITY_ANDROID && !UNITY_EDITOR
        private Vector3? m_moveGrabOffset;
        private Quaternion? m_rotateGrabOffset;
        private bool m_twoHandScaleActive;
        private float m_twoHandStartDistance;
        private Vector3 m_twoHandStartScale;
        private Vector3 m_twoHandStartPosition;
        private Vector3 m_twoHandStartMidpoint;
#endif

        public bool IsEnabled =>
            ObjectronLaunchSettings.ShowScanMeshOverlay
            && (ObjectronLaunchSettings.EnableLiveMeshTune || ObjectronLaunchSettings.EnableUprightPresetMode);

        public bool IsUprightMode => ObjectronLaunchSettings.EnableUprightPresetMode;

        public int SelectedPoolIndex => m_selectedPoolIndex;
        public bool HasSelection => m_selectedPoolIndex >= 0 && m_target != null;

        private void Awake()
        {
            m_meshVisuals ??= GetComponent<ObjectronScanMeshVisuals>()
                ?? FindAnyObjectByType<ObjectronScanMeshVisuals>();
            m_chairManager ??= GetComponent<ObjectronChairDetectionManager>()
                ?? FindAnyObjectByType<ObjectronChairDetectionManager>();
        }

        public void UpdateFrame()
        {
            if (!IsEnabled)
            {
                if (HasSelection)
                {
                    ClearSelection();
                }

                return;
            }

            TrySelectOnRightB();
            TryDeselectOnRightA();

#if UNITY_ANDROID && !UNITY_EDITOR
            if (HasSelection)
            {
                UpdateManipulation();
            }
#endif
        }

        public bool TrySaveSelection(Pose cameraPose)
        {
            if (!IsEnabled || !HasSelection)
            {
                QuestObjectronLogger.Viz("live_tune save skipped — select a mesh with Right B first");
                return false;
            }

            if (!m_chairManager.TryGetLocalizedChairForPoolIndex(m_selectedPoolIndex, out var chair))
            {
                QuestObjectronLogger.Err("live_tune save failed — no chair data for selected mesh");
                return false;
            }

            ObjectronScanCalibrationRecord.ReadAnnotationModelVectors(
                chair.Annotation,
                chair.Corners,
                out var translation,
                out var scale,
                out var rotationEuler);

            var bounds = ObjectronScanMeshVisuals.ComputeMeshBoundsLocal(m_target);
            ObjectronScanCalibrationRecord record;

            if (IsUprightMode)
            {
                record = ObjectronScanCalibrationRecord.CreateUprightPreset(
                    chair.ObjectId,
                    chair.Corners,
                    translation,
                    scale,
                    rotationEuler,
                    cameraPose,
                    m_target,
                    bounds,
                    m_uprightBaseRotation);
                QuestObjectronLogger.Detect(
                    "live_tune saved upright preset — future chairs use world-up + your correction");
            }
            else
            {
                record = ObjectronScanCalibrationRecord.Create(
                    chair.ObjectId,
                    chair.Corners,
                    translation,
                    scale,
                    rotationEuler,
                    cameraPose,
                    m_target,
                    bounds,
                    m_spawnReference);
            }

            if (record == null)
            {
                QuestObjectronLogger.Err("live_tune save failed — record build failed");
                return false;
            }

            if (!ObjectronScanCalibrationStore.Save(record))
            {
                return false;
            }

            QuestObjectronLogger.Detect(
                $"live_tune saved rotOffset={record.GetRotationOffsetDegrees():F1}° " +
                $"scale={record.GetCalibratedMeshLocalScale()} upright={record.HasUprightPreset()}");
            ClearSelection();
            m_chairManager.RefreshScanMeshVisualsAfterCalibrationSave();
            return true;
        }

        public void TryResetSelectionToUprightBase()
        {
            if (!IsUprightMode || !HasSelection)
            {
                QuestObjectronLogger.Viz("upright reset skipped — enable upright preset and select a mesh (Right B)");
                return;
            }

            if (!m_chairManager.TryGetLocalizedChairForPoolIndex(m_selectedPoolIndex, out var chair))
            {
                return;
            }

            if (!ObjectronScanCalibrationRecord.TryGetUprightSpawnPlacement(chair.Corners, out var upright))
            {
                return;
            }

            m_uprightBaseRotation = upright.Rotation;
            m_target.SetPositionAndRotation(upright.Position, upright.Rotation);
            m_target.localScale = Vector3.one;
            m_spawnReference = new ObjectronScanCalibrationRecord.SpawnReference
            {
                Position = upright.Position,
                Rotation = upright.Rotation,
                LocalScale = Vector3.one,
                IsValid = true,
            };
            ResetGrabState();
            QuestObjectronLogger.Detect("upright reset — chair at world-up + box yaw; rotate with trigger, Left X save");
        }

        private void TrySelectOnRightB()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!ObjectronQuestControllerButtons.RightBPressed())
            {
                return;
            }

            var controller = GetRightController();
            if (controller == null)
            {
                return;
            }

            var ray = new Ray(controller.position, controller.forward);
            if (!m_meshVisuals.TrySelectFromRaycast(ray, out var poolIndex))
            {
                QuestObjectronLogger.Viz("live_tune select — point Right B at a chair mesh");
                return;
            }

            SelectPoolIndex(poolIndex);
#endif
        }

        private void TryDeselectOnRightA()
        {
            if (ObjectronQuestControllerButtons.RightAPressed())
            {
                ClearSelection();
                QuestObjectronLogger.Viz("live_tune selection cleared (Right A)");
            }
        }

        private void SelectPoolIndex(int poolIndex)
        {
            if (!m_meshVisuals.TryGetActiveTransform(poolIndex, out var meshTransform))
            {
                return;
            }

            if (!m_chairManager.TryGetLocalizedChairForPoolIndex(poolIndex, out var chair))
            {
                return;
            }

            ObjectronScanMeshPlacement baselinePlacement;
            if (IsUprightMode)
            {
                if (!ObjectronScanCalibrationRecord.TryGetUprightSpawnPlacement(chair.Corners, out baselinePlacement))
                {
                    return;
                }

                m_uprightBaseRotation = baselinePlacement.Rotation;
                meshTransform.SetPositionAndRotation(baselinePlacement.Position, baselinePlacement.Rotation);
                meshTransform.localScale = Vector3.one;
                ObjectronScanMeshUpright.ApplyUprightHintRotation(
                    meshTransform,
                    ObjectronScanMeshVisuals.ComputeMeshBoundsLocal(meshTransform));
            }
            else
            {
                var calibration = ObjectronScanCalibrationDefaults.Get();
                Vector3? modelScale = null;
                if (chair.Annotation?.Scale != null && chair.Annotation.Scale.Count >= 3)
                {
                    modelScale = new Vector3(
                        chair.Annotation.Scale[0],
                        chair.Annotation.Scale[1],
                        chair.Annotation.Scale[2]);
                }

                if (calibration == null
                    || !calibration.TryApplyMeshPlacement(
                        chair.Corners,
                        modelScale,
                        out baselinePlacement,
                        ObjectronScanCalibrationDefaults.IsFromDeviceSave))
                {
                    return;
                }
            }

            m_selectedPoolIndex = poolIndex;
            m_target = meshTransform;
            m_meshVisuals.PinnedPoolIndex = poolIndex;
            m_spawnReference = new ObjectronScanCalibrationRecord.SpawnReference
            {
                Position = baselinePlacement.Position,
                Rotation = baselinePlacement.Rotation,
                LocalScale = baselinePlacement.LocalScale,
                IsValid = true,
            };
            ResetGrabState();

            if (IsUprightMode)
            {
                QuestObjectronLogger.Detect(
                    $"upright_tune selected #{poolIndex} id={chair.ObjectId} — " +
                    "Left Y reset upright, trigger rotate, Left X save preset");
            }
            else
            {
                QuestObjectronLogger.Detect(
                    $"live_tune selected mesh #{poolIndex} id={chair.ObjectId} — grip/trigger/both-scale, Left X save");
            }
        }

        public void ClearSelection()
        {
            m_selectedPoolIndex = -1;
            m_target = null;
            m_spawnReference = default;
            m_uprightBaseRotation = Quaternion.identity;
            if (m_meshVisuals != null)
            {
                m_meshVisuals.PinnedPoolIndex = -1;
            }

            ResetGrabState();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void UpdateManipulation()
        {
            if (!HasSelection)
            {
                return;
            }

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

            if (IsUprightMode)
            {
                if (rightTrigger)
                {
                    m_rotateGrabOffset ??= Quaternion.Inverse(rightController.rotation) * m_target.rotation;
                    m_target.rotation = rightController.rotation * m_rotateGrabOffset.Value;
                }
                else
                {
                    m_rotateGrabOffset = null;
                }

                if (twoHandScale)
                {
                    ApplyTwoHandScale(leftController, rightController);
                }

                return;
            }

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
                m_moveGrabOffset ??= m_target.position - rightController.position;
                m_target.position = rightController.position + m_moveGrabOffset.Value;
            }
            else
            {
                m_moveGrabOffset = null;
            }

            if (rightTrigger)
            {
                m_rotateGrabOffset ??= Quaternion.Inverse(rightController.rotation) * m_target.rotation;
                m_target.rotation = rightController.rotation * m_rotateGrabOffset.Value;
            }
            else
            {
                m_rotateGrabOffset = null;
            }
        }

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
                m_twoHandStartScale = m_target.localScale;
                m_twoHandStartPosition = m_target.position;
                m_twoHandStartMidpoint = midpoint;
                return;
            }

            var scaleFactor = distance / Mathf.Max(m_twoHandStartDistance, 0.05f);
            m_target.localScale = m_twoHandStartScale * scaleFactor;
            if (m_spawnReference.IsValid)
            {
                m_target.position = m_spawnReference.Position;
            }
            else
            {
                m_target.position = m_twoHandStartPosition + (midpoint - m_twoHandStartMidpoint);
            }
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
    }
}
