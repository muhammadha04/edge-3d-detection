// Quest 3 passthrough Objectron pipeline logging (logcat tag: QuestObj3D).

using System;
using UnityEngine;

namespace QuestObjectron
{
    public static class QuestObjectronLogger
    {
        public const string Tag = "QuestObj3D";

        public static void Boot(string message) => Log("BOOT", message);
        public static void Perm(string message) => Log("PERM", message);
        public static void Pca(string message) => Log("PCA", message);
        public static void Frame(string message) => Log("FRAME", message);
        public static void Detect(string message) => Log("DETECT", message);
        public static void Pose(string message) => Log("POSE", message);
        public static void World(string message) => Log("WORLD", message);
        public static void Viz(string message) => Log("VIZ", message);
        public static void Dbg(string message) => Log("DBG", message);
        /// <summary>Search logcat with: PLACEMENT_DEBUG</summary>
        public static void PlacementDebug(string message) => Log("PLACEMENT_DEBUG", message);
        /// <summary>Search logcat with: BOX_PROJ_DEBUG</summary>
        public static void BoxProjDebug(string message) => Log("BOX_PROJ_DEBUG", message);
        public static void LogHud(string message) => Log("HUD", message);
        /// <summary>Debug session NDJSON (filter logcat: AGENT_DEBUG).</summary>
        public static void AgentDebug(string message) => Log("AGENT_DEBUG", message);
        /// <summary>Box debug wizard (filter logcat: BOX_DEBUG_STEP or step).</summary>
        public static void BoxDebugStep(string message) => Log("BOX_DEBUG_STEP", message);
        public static void Err(string message) => Log("ERR", message, LogType.Error);
        public static void Err(string message, Exception ex) => Log("ERR", $"{message} | {ex}", LogType.Error);

        private static void Log(string step, string message, LogType type = LogType.Log)
        {
            var line = $"[{step}] {message}";
            UnityEngine.Debug.unityLogger.Log(type, Tag, line);
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var logClass = new AndroidJavaClass("android.util.Log");
                var priority = type == LogType.Error ? 6 : type == LogType.Warning ? 5 : 4;
                logClass.CallStatic<int>("println", priority, Tag, line);
            }
            catch (Exception)
            {
                // Editor / missing JNI — Unity log is enough.
            }
#endif
        }
    }
}
