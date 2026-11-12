// The lab-chair 3D scan, pre-normalised offline (Tools/normalize_scan.py) into the SAME canonical frame as
// UprightBox: bottom-centre pivot at the origin, unit size (x,z ∈ [-0.5, 0.5], y ∈ [0, 1]), backrest toward
// local -Z, seat facing +Z, left/right symmetric about X = 0.
//
// Because the mesh lives in the box's own unit frame, fitting it to a detection needs no saved calibration:
//   localScale = box size (x/z swapped for a 90° turn), localRotation = quarter turns that put the backrest on
//   the side depth says it is (BackrestEstimator).
//
// File format (little-endian): "QDM1", uint vertexCount, uint triangleCount, float3[vertexCount], int3[triangleCount].
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestDetect3D
{
    public static class ScanModel
    {
        public const string ResourceName = "LabChairScan";
        private static Mesh s_mesh;
        private static bool s_tried;

        public static Mesh Get()
        {
            if (s_tried)
            {
                return s_mesh;
            }

            s_tried = true;
            var asset = Resources.Load<TextAsset>(ResourceName);
            if (asset == null)
            {
                Log.Warn($"scan_model missing Resources/{ResourceName}.bytes");
                return null;
            }

            var bytes = asset.bytes;
            if (bytes.Length < 12 || bytes[0] != 'Q' || bytes[1] != 'D' || bytes[2] != 'M' || bytes[3] != '1')
            {
                Log.Warn("scan_model bad header");
                return null;
            }

            var vCount = (int)System.BitConverter.ToUInt32(bytes, 4);
            var tCount = (int)System.BitConverter.ToUInt32(bytes, 8);
            var expected = 12 + vCount * 12 + tCount * 12;
            if (bytes.Length != expected)
            {
                Log.Warn($"scan_model size mismatch {bytes.Length} != {expected}");
                return null;
            }

            var vertices = new Vector3[vCount];
            var colors = new Color32[vCount];
            var o = 12;
            for (var i = 0; i < vCount; i++, o += 12)
            {
                vertices[i] = new Vector3(System.BitConverter.ToSingle(bytes, o), System.BitConverter.ToSingle(bytes, o + 4),
                    System.BitConverter.ToSingle(bytes, o + 8));
                colors[i] = new Color32(255, 255, 255, 255);
            }

            var triangles = new int[tCount * 3];
            for (var i = 0; i < triangles.Length; i++, o += 4)
            {
                triangles[i] = System.BitConverter.ToInt32(bytes, o);
            }

            s_mesh = new Mesh
            {
                name = "QD3D_LabChairScan",
                indexFormat = vCount > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            s_mesh.vertices = vertices;
            s_mesh.colors32 = colors;
            s_mesh.triangles = triangles;
            s_mesh.RecalculateBounds();
            Log.Info($"scan_model loaded verts={vCount} tris={tCount} bounds={s_mesh.bounds.size}");
            return s_mesh;
        }
    }
}
