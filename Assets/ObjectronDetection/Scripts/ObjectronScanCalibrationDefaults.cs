// Load default lab-chair calibration (device save overrides bundled Resources JSON).

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronScanCalibrationDefaults
    {
        private const string ResourcesPath = "ScanCalibration/default_chair_calibration";

        private static ObjectronScanCalibrationRecord s_cached;
        private static bool s_fromDeviceSave;

        /// <summary>True when calibration was loaded from a user save on device (Left X in scan calibration).</summary>
        public static bool IsFromDeviceSave => s_fromDeviceSave;

        public static ObjectronScanCalibrationRecord Get()
        {
            if (s_cached != null)
            {
                return s_cached;
            }

            s_fromDeviceSave = false;
            var deviceRecord = ObjectronScanCalibrationStore.LoadLatest();
            if (deviceRecord != null && deviceRecord.HasRelativeTransform())
            {
                s_cached = deviceRecord;
                s_fromDeviceSave = true;
                var scaleFactor = deviceRecord.GetMeshScaleFactor();
                var rotDeg = deviceRecord.GetRotationOffsetDegrees();
                QuestObjectronLogger.Boot(
                    $"scan_calibration device save scaleFactor={scaleFactor:F3} rotOffset={rotDeg:F1}° " +
                    $"spawnRelativeRotation={deviceRecord.spawnRelativeRotation} " +
                    $"applyRotInLive={deviceRecord.ShouldApplySavedRotation()}");
                return s_cached;
            }

            var bundled = Resources.Load<TextAsset>(ResourcesPath);
            if (bundled != null)
            {
                s_cached = JsonUtility.FromJson<ObjectronScanCalibrationRecord>(bundled.text);
                if (s_cached != null && s_cached.HasRelativeTransform())
                {
                    QuestObjectronLogger.Boot($"scan_calibration using bundled defaults ({ResourcesPath})");
                    return s_cached;
                }
            }

            QuestObjectronLogger.Err($"scan_calibration defaults missing — add {ResourcesPath}.json to Resources");
            return null;
        }

        public static void ClearCache()
        {
            s_cached = null;
            s_fromDeviceSave = false;
        }
    }
}
