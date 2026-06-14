// Place lab-chair.obj instances using saved scan calibration defaults.

using System.Collections.Generic;
using UnityEngine;

namespace QuestObjectron
{
    public class ObjectronScanMeshVisuals : MonoBehaviour
    {
        private const int MaxMeshPool = 20;
        private const float SelectRayMaxDistanceM = 8f;
        private const string PrefabResourcesPath = "ScanCalibration/LabChair";
        private const string SelectionColliderLayerName = "Default";

        private static Transform s_worldRoot;
        private readonly List<Transform> m_pool = new();
        private int m_activeCount;
        private int m_pinnedPoolIndex = -1;

        public int ActiveCount => m_activeCount;
        public int PinnedPoolIndex
        {
            get => m_pinnedPoolIndex;
            set => m_pinnedPoolIndex = value;
        }

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
                    if (i != m_pinnedPoolIndex)
                    {
                        instance.gameObject.SetActive(false);
                    }

                    continue;
                }

                var corners = detectionBoxes[i];
                Vector3? modelScale = null;
                if (modelScales != null && i < modelScales.Count)
                {
                    modelScale = modelScales[i];
                }

                if (!calibration.TryApplyMeshPlacement(
                        corners,
                        modelScale,
                        out var placement,
                        ObjectronScanCalibrationDefaults.IsFromDeviceSave))
                {
                    if (i != m_pinnedPoolIndex)
                    {
                        instance.gameObject.SetActive(false);
                    }

                    continue;
                }

                instance.gameObject.SetActive(true);
                if (i != m_pinnedPoolIndex)
                {
                    instance.SetPositionAndRotation(placement.Position, placement.Rotation);
                    SetWorldLossyScale(instance, placement.LocalScale);
                }

                EnsureSelectionCollider(instance);
                m_activeCount++;
            }

            QuestObjectronLogger.Viz(
                $"scan_mesh localized count={m_activeCount} upright=base+yaw " +
                $"applyUpright={calibration.HasUprightPreset()} pinned={m_pinnedPoolIndex}");
        }

        public bool TryGetActiveTransform(int poolIndex, out Transform meshTransform)
        {
            meshTransform = null;
            if (poolIndex < 0 || poolIndex >= m_pool.Count)
            {
                return false;
            }

            meshTransform = m_pool[poolIndex];
            return meshTransform != null && meshTransform.gameObject.activeSelf;
        }

        public bool TrySelectFromRaycast(Ray ray, out int poolIndex)
        {
            poolIndex = -1;
            var layerMask = LayerMask.GetMask(SelectionColliderLayerName);
            if (layerMask == 0)
            {
                layerMask = ~0;
            }

            if (!Physics.Raycast(ray, out var hit, SelectRayMaxDistanceM, layerMask, QueryTriggerInteraction.Collide))
            {
                return false;
            }

            for (var i = 0; i < m_pool.Count; i++)
            {
                var instance = m_pool[i];
                if (!instance.gameObject.activeSelf)
                {
                    continue;
                }

                if (hit.transform == instance || hit.transform.IsChildOf(instance))
                {
                    poolIndex = i;
                    return true;
                }
            }

            return false;
        }

        public static Bounds ComputeMeshBoundsLocal(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            var centerLocal = root.InverseTransformPoint(bounds.center);
            var extentsWorld = bounds.extents;
            var lossy = root.lossyScale;
            var sizeLocal = new Vector3(
                extentsWorld.x * 2f / Mathf.Max(lossy.x, 0.0001f),
                extentsWorld.y * 2f / Mathf.Max(lossy.y, 0.0001f),
                extentsWorld.z * 2f / Mathf.Max(lossy.z, 0.0001f));
            return new Bounds(centerLocal, sizeLocal);
        }

        private static void EnsureSelectionCollider(Transform instance)
        {
            var collider = instance.GetComponent<BoxCollider>();
            if (collider == null)
            {
                collider = instance.gameObject.AddComponent<BoxCollider>();
            }

            collider.isTrigger = true;
            var bounds = ComputeMeshBoundsLocal(instance);
            collider.center = bounds.center;
            collider.size = bounds.size;
        }

        private static void SetWorldLossyScale(Transform target, Vector3 worldScale)
        {
            var parent = target.parent;
            if (parent == null)
            {
                target.localScale = worldScale;
                return;
            }

            var parentScale = parent.lossyScale;
            target.localScale = new Vector3(
                worldScale.x / Mathf.Max(parentScale.x, 0.0001f),
                worldScale.y / Mathf.Max(parentScale.y, 0.0001f),
                worldScale.z / Mathf.Max(parentScale.z, 0.0001f));
        }

        public void ClearAllForSceneExit()
        {
            m_pinnedPoolIndex = -1;
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
            for (var i = 0; i < m_pool.Count; i++)
            {
                var instance = m_pool[i];
                if (instance != null && i != m_pinnedPoolIndex)
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
