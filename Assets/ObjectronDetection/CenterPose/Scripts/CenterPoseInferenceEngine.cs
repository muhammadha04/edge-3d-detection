using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

namespace QuestObjectron.CenterPose
{
    public sealed class CenterPoseInferenceEngine : IDisposable
    {
        public const float ScoreThreshold = 0.3f;
        public const int MaxDetections = 3;

        private readonly Worker m_worker;
        private readonly int m_scoresIndex;
        private readonly int m_bboxesIndex;
        private readonly int m_kpsIndex;
        private readonly int m_objScaleIndex;
        private readonly int m_maxCandidates;

        public CenterPoseInferenceEngine(ModelAsset modelAsset, BackendType backend = BackendType.CPU)
        {
            if (modelAsset == null)
            {
                throw new ArgumentNullException(nameof(modelAsset));
            }

            var model = ModelLoader.Load(modelAsset);
            m_worker = new Worker(model, backend);

            m_scoresIndex = FindOutputIndex(model, "scores");
            m_bboxesIndex = FindOutputIndex(model, "bboxes");
            m_kpsIndex = FindOutputIndex(model, "kps");
            m_objScaleIndex = FindOutputIndex(model, "obj_scale");

            var scoresShape = model.outputs[m_scoresIndex].shape;
            m_maxCandidates = scoresShape.rank >= 2 ? scoresShape.Get(1) : 100;
        }

        public static void Warmup(ModelAsset modelAsset)
        {
            using var engine = new CenterPoseInferenceEngine(modelAsset, BackendType.CPU);
            var zeros = new float[3 * CenterPoseGeometry.InputSize * CenterPoseGeometry.InputSize];
            using var input = engine.CreateInputTensor(zeros);
            engine.m_worker.Schedule(input);
            engine.CompleteAllOutputs();
        }

        public void Schedule(float[] nchwInput)
        {
            using var input = CreateInputTensor(nchwInput);
            m_worker.Schedule(input);
        }

        public void CompleteAllOutputs()
        {
            m_worker.PeekOutput(m_scoresIndex)?.CompleteAllPendingOperations();
            m_worker.PeekOutput(m_bboxesIndex)?.CompleteAllPendingOperations();
            m_worker.PeekOutput(m_kpsIndex)?.CompleteAllPendingOperations();
            m_worker.PeekOutput(m_objScaleIndex)?.CompleteAllPendingOperations();
        }

        public List<CenterPoseDetection> Decode(CenterPosePreprocessMeta meta)
        {
            using var scores = (m_worker.PeekOutput(m_scoresIndex) as Tensor<float>)?.ReadbackAndClone();
            using var bboxes = (m_worker.PeekOutput(m_bboxesIndex) as Tensor<float>)?.ReadbackAndClone();
            using var kps = (m_worker.PeekOutput(m_kpsIndex) as Tensor<float>)?.ReadbackAndClone();
            using var objScale = (m_worker.PeekOutput(m_objScaleIndex) as Tensor<float>)?.ReadbackAndClone();

            if (scores == null || bboxes == null || kps == null || objScale == null)
            {
                return new List<CenterPoseDetection>();
            }

            var candidates = new List<(float score, int idx)>();
            for (var i = 0; i < m_maxCandidates; i++)
            {
                var score = scores[0, i, 0];
                if (score >= ScoreThreshold)
                {
                    candidates.Add((score, i));
                }
            }

            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            var detections = new List<CenterPoseDetection>(Mathf.Min(candidates.Count, MaxDetections));
            for (var c = 0; c < candidates.Count && detections.Count < MaxDetections; c++)
            {
                var idx = candidates[c].idx;
                var score = candidates[c].score;

                var bboxField = new Vector2[2];
                bboxField[0] = new Vector2(bboxes[0, idx, 0], bboxes[0, idx, 1]);
                bboxField[1] = new Vector2(bboxes[0, idx, 2], bboxes[0, idx, 3]);
                _ = CenterPoseGeometry.TransformPreds(bboxField, meta);

                var cornerField = new Vector2[8];
                for (var k = 0; k < 8; k++)
                {
                    cornerField[k] = new Vector2(kps[0, idx, k * 2], kps[0, idx, k * 2 + 1]);
                }

                var cornersPx = CenterPoseGeometry.TransformPreds(cornerField, meta);
                var keypoints = BuildNormalizedKeypoints(cornersPx, meta.Width, meta.Height);
                if (keypoints == null)
                {
                    continue;
                }

                var scale = new Vector3(objScale[0, idx, 0], objScale[0, idx, 1], objScale[0, idx, 2]);
                detections.Add(new CenterPoseDetection(score, keypoints, scale, meta.Width, meta.Height));
            }

            return detections;
        }

        private Tensor<float> CreateInputTensor(float[] nchw)
        {
            var input = new Tensor<float>(new TensorShape(1, 3, CenterPoseGeometry.InputSize, CenterPoseGeometry.InputSize));
            var buffer = input.AsNativeArray();
            for (var i = 0; i < nchw.Length; i++)
            {
                buffer[i] = nchw[i];
            }

            return input;
        }

        private static NormalizedKeypoint2D[] BuildNormalizedKeypoints(Vector2[] cornersPx, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var valid = new List<Vector2>();
            foreach (var p in cornersPx)
            {
                if (p.x > 0f && p.y > 0f)
                {
                    valid.Add(p);
                }
            }

            if (valid.Count < 4)
            {
                return null;
            }

            var center = Vector2.zero;
            foreach (var p in valid)
            {
                center += p;
            }

            center /= valid.Count;

            var keypoints = new NormalizedKeypoint2D[9];
            keypoints[0] = new NormalizedKeypoint2D(0, center.x / width, center.y / height);
            for (var i = 0; i < 8; i++)
            {
                var p = cornersPx[i];
                keypoints[i + 1] = new NormalizedKeypoint2D(
                    i + 1,
                    p.x / width,
                    p.y / height);
            }

            return keypoints;
        }

        private static int FindOutputIndex(Model model, string contains)
        {
            for (var i = 0; i < model.outputs.Count; i++)
            {
                if (model.outputs[i].name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return i;
                }
            }

            throw new InvalidOperationException($"CenterPose model output not found: {contains}");
        }

        public void Dispose()
        {
            CompleteAllOutputs();
            m_worker?.Dispose();
        }
    }
}
