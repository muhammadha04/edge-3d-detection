// Place lab-chair.obj instances using saved scan calibration defaults.

using System.Collections.Generic;
using UnityEngine;

namespace QuestObjectron
{
    public class ObjectronScanMeshVisuals : MonoBehaviour
    {
        private const int MaxMeshPool = 20;
        private const string PrefabResourcesPath = "ScanCalibration/LabChair";

        private static Transform s_worldRoot;
        private readonly List<Transform> m_pool = new();
        private int m_activeCount;

        public int ActiveCount => m_activeCount;

        public void Prewarm()
        {
            GetOrCreateWorldRoot();
            EnsurePoolSize(MaxMeshPool);
        }

        public void Localize(IReadOnlyList<Vector3[]> detectionBoxes)
        {
            Localize(detectionBoxes, null);
        }

        public void Localize(IReadOnlyList<Vector3[]> detectionBoxes, IReadOnlyList<Vector3> modelScales)
        {
            var calibration = ObjectronScanCalibrationDefaults.Get();
            if (calibration == null)
            {
                ClearAll();
                return;
            }

            GetOrCreateWorldRoot();
            m_activeCount = 0;
            if (detectionBoxes == null || detectionBoxes.Count == 0)
            {
                HideInactive();
                return;
            }

            EnsurePoolSize(Mathf.Min(detectionBoxes.Count, MaxMeshPool));
            for (var i = 0; i < m_pool.Count; i++)
            {
                var instance = m_pool[i];
                if (i >= detectionBoxes.Count)
                {
                    instance.gameObject.SetActive(false);
                    continue;
                }

                var corners = detectionBoxes[i];
                Vector3? modelScale = null;
                if (modelScales != null && i < modelScales.Count)
                {
                    modelScale = modelScales[i];
                }

                if (!calibration.TryApplyMeshPlacement(corners, modelScale, out var placement))
                {
                    instance.gameObject.SetActive(false);
                    continue;
                }

                instance.gameObject.SetActive(true);
                instance.SetPositionAndRotation(placement.Position, placement.Rotation);
                instance.localScale = placement.LocalScale;
                m_activeCount++;
            }

            QuestObjectronLogger.Viz($"scan_mesh localized count={m_activeCount} scale={calibration.GetCalibratedMeshLocalScale()}");
        }

        public void ClearAllForSceneExit()
        {
            ClearAll();
            if (s_worldRoot != null)
            {
                Destroy(s_worldRoot.gameObject);
                s_worldRoot = null;
            }

            m_pool.Clear();
        }

        public static void DestroyPersistentWorldRoot()
        {
            if (s_worldRoot != null)
            {
                Object.Destroy(s_worldRoot.gameObject);
                s_worldRoot = null;
            }

            var existing = GameObject.Find("ObjectronScanMeshRoot");
            if (existing != null)
            {
                Object.Destroy(existing);
            }
        }

        private void ClearAll()
        {
            m_activeCount = 0;
            HideInactive();
        }

        private void HideInactive()
        {
            foreach (var instance in m_pool)
            {
                if (instance != null)
                {
                    instance.gameObject.SetActive(false);
                }
            }
        }

        private void EnsurePoolSize(int count)
        {
            var root = GetOrCreateWorldRoot();
            while (m_pool.Count < count)
            {
                var prefab = Resources.Load<GameObject>(PrefabResourcesPath);
                if (prefab == null)
                {
                    return;
                }

                var instance = Instantiate(prefab, root);
                instance.name = $"ScanMesh_{m_pool.Count}";
                ObjectronScanManipulator.EnsureVisibleMaterials(instance);
                instance.SetActive(false);
                m_pool.Add(instance.transform);
            }
        }

        private static Transform GetOrCreateWorldRoot()
        {
            if (s_worldRoot != null)
            {
                return s_worldRoot;
            }

            var existing = GameObject.Find("ObjectronScanMeshRoot");
            if (existing != null)
            {
                s_worldRoot = existing.transform;
                return s_worldRoot;
            }

            var root = new GameObject("ObjectronScanMeshRoot");
            DontDestroyOnLoad(root);
            s_worldRoot = root.transform;
            return s_worldRoot;
        }
    }
}
