// CenterPose chair detection on Quest 3: PCA camera -> Sentis CenterPose -> world-space 3D boxes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using QuestObjectron.CenterPose;
using Unity.InferenceEngine;
using UnityEngine;

namespace QuestObjectron
{
    public class CenterPoseChairDetectionManager : MonoBehaviour
    {
        private const int InferenceEveryNFrames = 2;

        [Header("Meta / Camera")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughImageSource m_imageSource;
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private ObjectronQuestVisuals m_questVisuals;
        [SerializeField] private ObjectronHeadsetHud m_headsetHud;

        [Header("CenterPose")]
        [SerializeField] private ModelAsset m_centerPoseModel;
        [SerializeField] private BackendType m_backend = BackendType.CPU;
        [SerializeField] private float m_scoreThreshold = CenterPoseInferenceEngine.ScoreThreshold;

        [Header("Tuning")]
        [SerializeField] private ObjectronPlacementOptions m_placementOptions = new();

        private CenterPoseInferenceEngine m_engine;
        private CenterPoseCameraReader m_cameraReader;
        private ObjectronWorldPlacement m_worldPlacement;
        private Coroutine m_pipeline;
        private int m_frameId;
        private int m_emptyLogCount;
        private int m_lastLoggedOkCount = -1;
        private float m_lastRotationHintTime;
        private float m_lastResetButtonTime = -999f;
        private const float ResetButtonCooldownSec = 1f;

        public ObjectronPlacementOptions PlacementOptions => m_placementOptions;

        public void WireReferences(
            PassthroughCameraAccess cameraAccess,
            PassthroughImageSource imageSource,
            EnvironmentRayCastSampleManager environmentRaycast,
            ObjectronQuestVisuals questVisuals)
        {
            if (cameraAccess != null) m_cameraAccess = cameraAccess;
            if (imageSource != null) m_imageSource = imageSource;
            if (environmentRaycast != null) m_environmentRaycast = environmentRaycast;
            if (questVisuals != null) m_questVisuals = questVisuals;
        }

        private void Awake()
        {
            if (m_placementOptions == null)
            {
                m_placementOptions = new ObjectronPlacementOptions();
            }

            m_placementOptions.CompensateHeadRoll = true;
            ObjectronPlacementFixSettings.Active = m_placementOptions;
            m_worldPlacement = new ObjectronWorldPlacement(
                m_cameraAccess,
                m_environmentRaycast,
                m_placementOptions);

            if (m_imageSource == null)
            {
                m_imageSource = gameObject.AddComponent<PassthroughImageSource>();
            }

            m_imageSource.Bind(m_cameraAccess);
            if (m_imageSource is PassthroughImageSource passthroughSrc)
            {
                passthroughSrc.ApplyQuest3Defaults();
            }

            SyncPlacementOptionsFromImageSource();
            EnsureDebug();
            QuestObjectronLogger.Boot(
                $"centerpose_chair camera_rotation={m_imageSource.rotation} flip={m_imageSource.isHorizontallyFlipped}");
        }

        private void EnsureDebug()
        {
            if (m_questVisuals == null)
            {
                m_questVisuals = GetComponent<ObjectronQuestVisuals>()
                    ?? FindAnyObjectByType<ObjectronQuestVisuals>();
                if (m_questVisuals == null)
                {
                    m_questVisuals = gameObject.AddComponent<ObjectronQuestVisuals>();
                }
            }

            if (m_headsetHud == null)
            {
                m_headsetHud = GetComponent<ObjectronHeadsetHud>()
                    ?? FindAnyObjectByType<ObjectronHeadsetHud>();
                if (m_headsetHud == null)
                {
                    m_headsetHud = gameObject.AddComponent<ObjectronHeadsetHud>();
                }
            }

            m_questVisuals.Prewarm();
        }

        private IEnumerator Start()
        {
            if (m_centerPoseModel == null)
            {
                QuestObjectronLogger.Err("CenterPose model asset is not assigned — convert chair.onnx to .sentis");
                yield break;
            }

            yield return WaitForPermissions();
            m_cameraReader = new CenterPoseCameraReader();
            m_engine = new CenterPoseInferenceEngine(m_centerPoseModel, m_backend);
            m_pipeline = StartCoroutine(RunPipeline());
        }

        private void Update()
        {
            TryControllerInput();
            MaybeLogRotationHint();
            m_questVisuals?.HoldOrClear();
        }

        private void OnDestroy()
        {
            if (m_pipeline != null)
            {
                StopCoroutine(m_pipeline);
                m_pipeline = null;
            }

            m_engine?.Dispose();
            m_cameraReader?.Dispose();
            m_questVisuals?.ClearAllForSceneExit();
        }

        private void TryControllerInput()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            {
                var now = Time.realtimeSinceStartup;
                if (now - m_lastResetButtonTime >= ResetButtonCooldownSec)
                {
                    m_lastResetButtonTime = now;
                    ResetVisuals();
                }
            }
#endif
        }

        public void ResetVisuals()
        {
            m_questVisuals?.ClearLocalization();
            QuestObjectronLogger.Detect("centerpose_reset — scanning for chairs");
        }

        private void MaybeLogRotationHint()
        {
            if (m_imageSource == null || m_emptyLogCount == 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (now - m_lastRotationHintTime < 15f)
            {
                return;
            }

            m_lastRotationHintTime = now;
            QuestObjectronLogger.Boot(
                $"controls: Y (left)=reset | rotation={m_imageSource.rotation} flip={m_imageSource.isHorizontallyFlipped}");
        }

        private void SyncPlacementOptionsFromImageSource()
        {
            if (m_imageSource == null)
            {
                return;
            }

            m_placementOptions.MirrorInferenceHorizontal = m_imageSource.isHorizontallyFlipped;
            m_worldPlacement?.SetMirrorHorizontal(m_placementOptions.MirrorInferenceHorizontal);
        }

        private IEnumerator WaitForPermissions()
        {
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
            {
                yield return null;
            }

            var sceneOk = m_environmentRaycast == null || m_environmentRaycast.HasScenePermission();
            QuestObjectronLogger.Perm($"camera=granted spatial={(sceneOk ? "granted" : "pending")}");
        }

        private IEnumerator RunPipeline()
        {
            while (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                yield return null;
            }

            yield return m_imageSource.Play();
            if (!m_imageSource.isPrepared)
            {
                QuestObjectronLogger.Err("Passthrough image source failed to start");
                yield break;
            }

            QuestObjectronLogger.Boot($"centerpose_backend={m_backend} score_thresh={m_scoreThreshold:F2}");
            var waitEndOfFrame = new WaitForEndOfFrame();

            while (true)
            {
                if (!m_cameraAccess.IsPlaying)
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                if (!IsHeadPoseReliable())
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                m_frameId++;
                if (m_frameId % InferenceEveryNFrames != 0)
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                var t0 = Time.realtimeSinceStartup;
                var cameraPose = m_cameraAccess.GetCameraPose();
                SyncPlacementOptionsFromImageSource();

                var tex = m_imageSource.GetCurrentTexture();
                if (!m_cameraReader.TryReadOriented(
                        tex,
                        m_imageSource.rotation,
                        m_imageSource.isHorizontallyFlipped,
                        out var pixels,
                        out var width,
                        out var height))
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                var meta = CenterPoseGeometry.BuildMeta(width, height);
                var tensor = CenterPoseGeometry.BuildInputTensor(pixels, width, height, meta);
                m_engine.Schedule(tensor);
                m_engine.CompleteAllOutputs();
                var detections = m_engine.Decode(meta);
                ProcessDetections(detections, cameraPose, t0);
                yield return waitEndOfFrame;
            }
        }

        private void ProcessDetections(List<CenterPoseDetection> detections, Pose cameraPose, float startTime)
        {
            if (detections == null || detections.Count == 0)
            {
                m_emptyLogCount++;
                if (m_emptyLogCount == 1 || m_emptyLogCount % 60 == 0)
                {
                    QuestObjectronLogger.Detect("empty");
                }

                m_questVisuals?.HoldOrClear();
                UpdateHud("empty", null, 0f, 0f, -1, PlacementMethod.None);
                return;
            }

            m_emptyLogCount = 0;
            var ms = (Time.realtimeSinceStartup - startTime) * 1000f;
            if (detections.Count != m_lastLoggedOkCount)
            {
                m_lastLoggedOkCount = detections.Count;
                QuestObjectronLogger.Detect($"centerpose_ok count={detections.Count} ms={ms:F0}");
            }

            var placements = m_worldPlacement.PlaceCenterPoseDetections(detections, cameraPose);
            var worldBoxes = new List<Vector3[]>();
            foreach (var placement in placements)
            {
                if (placement.Corners != null)
                {
                    worldBoxes.Add(placement.Corners);
                }
            }

            if (worldBoxes.Count > 0)
            {
                m_questVisuals.Show(worldBoxes);
                if (m_imageSource is PassthroughImageSource passthroughSrc)
                {
                    passthroughSrc.LogRotationWith2dHits(worldBoxes.Count);
                }

                ObjectronBoxValidation.TryGetExtentMeters(worldBoxes[0], out var extent);
                var hmd = Camera.main != null ? Camera.main.transform.position : cameraPose.position;
                UpdateHud("ok", worldBoxes[0][0], extent, Vector3.Distance(hmd, worldBoxes[0][0]), 0, placements[0].Method);
            }
            else
            {
                m_questVisuals.HoldOrClear();
                UpdateHud("ok_no_placement", null, 0f, 0f, 0, PlacementMethod.None);
            }
        }

        private void UpdateHud(
            string detectState,
            Vector3? worldCenter,
            float extent,
            float distHmd,
            int objectId,
            PlacementMethod placementMethod)
        {
            if (m_headsetHud == null)
            {
                return;
            }

            var rotLabel = m_imageSource != null
                ? $"{m_imageSource.rotation} flip={m_imageSource.isHorizontallyFlipped}"
                : "n/a";
            var pcaRes = "n/a";
            if (m_cameraAccess != null && m_cameraAccess.IsPlaying)
            {
                var res = m_cameraAccess.CurrentResolution;
                pcaRes = $"{Mathf.RoundToInt(res.x)}x{Mathf.RoundToInt(res.y)}";
            }

            m_headsetHud.Apply(new ObjectronHudSnapshot(
                detectState,
                rotLabel,
                worldCenter,
                extent,
                distHmd,
                m_questVisuals != null ? m_questVisuals.ActiveCount : 0,
                objectId,
                placementMethod.ToString(),
                m_frameId,
                pcaRes,
                "CenterPose chair — point at chairs; Y=reset",
                false,
                false,
                false));
        }

        private static bool IsHeadPoseReliable()
        {
            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);

            return ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess();
        }
    }
}
