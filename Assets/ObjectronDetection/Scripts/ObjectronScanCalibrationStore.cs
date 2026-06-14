// Persist scan calibration records to device storage and logcat.

using System.IO;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronScanCalibrationStore
    {
        public const string FileName = "scan_calibration_chair.json";

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        public static bool Save(ObjectronScanCalibrationRecord record)
        {
            if (record == null)
            {
                return false;
            }

            var json = record.ToDebugString();
            File.WriteAllText(FilePath, json);
            ObjectronScanCalibrationDefaults.ClearCache();
            QuestObjectronLogger.Detect($"scan_calibration saved path={FilePath}");
            QuestObjectronLogger.Dbg($"SCAN_CALIBRATION_JSON_BEGIN\n{json}\nSCAN_CALIBRATION_JSON_END");
            return true;
        }

        public static ObjectronScanCalibrationRecord LoadLatest()
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = File.ReadAllText(FilePath);
            return JsonUtility.FromJson<ObjectronScanCalibrationRecord>(json);
        }
    }
}
