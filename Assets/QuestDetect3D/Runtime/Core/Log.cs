// Single log tag for the whole app. Lines go to logcat (adb logcat -s Unity | findstr QD3D) AND to
// <persistentDataPath>/qd3d_log.txt on the headset, so sessions run without a USB cable can be pulled later
// (PullLog.bat).
using System;
using System.IO;
using UnityEngine;

namespace QuestDetect3D
{
    public static class Log
    {
        private const string Tag = "[QD3D]";
        private static StreamWriter s_file;
        private static bool s_fileFailed;
        private static float s_lastFlush;

        public static string FilePath => Path.Combine(Application.persistentDataPath, "qd3d_log.txt");

        public static void Info(string message) => Write("I", message);
        public static void Warn(string message) => Write("W", message);
        public static void Error(string message) => Write("E", message);

        private static void Write(string level, string message)
        {
            switch (level)
            {
                case "E":
                    Debug.LogError($"{Tag} {message}");
                    break;
                case "W":
                    Debug.LogWarning($"{Tag} {message}");
                    break;
                default:
                    Debug.Log($"{Tag} {message}");
                    break;
            }

            AppendToFile(level, message);
        }

        private static void AppendToFile(string level, string message)
        {
            if (s_fileFailed)
            {
                return;
            }

            try
            {
                if (s_file == null)
                {
                    var path = FilePath;
                    // Keep the previous session for comparison.
                    if (File.Exists(path))
                    {
                        File.Copy(path, path + ".prev", true);
                    }

                    s_file = new StreamWriter(path, false) { AutoFlush = false };
                    s_file.WriteLine($"# QuestDetect3D log {DateTime.Now:yyyy-MM-dd HH:mm:ss} v{Application.version}");
                }

                s_file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {message}");
                var now = Time.realtimeSinceStartup;
                if (level != "I" || now - s_lastFlush > 2f)
                {
                    s_file.Flush();
                    s_lastFlush = now;
                }
            }
            catch (Exception)
            {
                s_fileFailed = true;
            }
        }

        public static void Flush()
        {
            try
            {
                s_file?.Flush();
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }
}
