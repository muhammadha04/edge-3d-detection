#if UNITY_EDITOR
// Batch build entry point:
//   Unity.exe -batchmode -nographics -projectPath <proj> -buildTarget Android
//             -executeMethod QuestDetect3D.EditorTools.BuildScript.BuildAndroid -logFile Builds/build.log
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace QuestDetect3D.EditorTools
{
    public static class BuildScript
    {
        public const string PackageName = "com.amplilab.questdetect3d";
        public const string ApkPath = "Builds/QuestDetect3D.apk";

        [MenuItem("QuestDetect3D/Build APK")]
        public static void BuildMenu() => Build(exitWhenDone: false);

        public static void BuildAndroid() => Build(exitWhenDone: true);

        private static void Build(bool exitWhenDone)
        {
            var ok = false;
            try
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                }

                YoloModelExport.EnsureExported(); // FP16 .sentis from the ONNX (uint8 loses too much confidence)
                SelfTest.Run(); // synthetic-room 3D tests + YOLO variants on photos; writes Builds/selftest.txt (never blocks the build)
                SceneBuilder.BuildScene();

                PlayerSettings.productName = "QuestDetect3D";
                PlayerSettings.companyName = "AmpliLab";
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageName);
                PlayerSettings.bundleVersion = "2.2.0";
                PlayerSettings.Android.bundleVersionCode = 5;
                EditorUserBuildSettings.development = false;
                AssetDatabase.SaveAssets();

                Directory.CreateDirectory("Builds");
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { SceneBuilder.ScenePath },
                    locationPathName = ApkPath,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = BuildOptions.None,
                };

                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;
                Debug.Log($"QuestDetect3D BUILD {summary.result} errors={summary.totalErrors} " +
                          $"size={summary.totalSize / (1024 * 1024)}MB time={summary.totalTime} path={summary.outputPath}");
                ok = summary.result == BuildResult.Succeeded;
            }
            catch (Exception e)
            {
                Debug.LogError($"QuestDetect3D BUILD EXCEPTION {e}");
            }

            File.WriteAllText("Builds/status.txt", ok ? "BUILD_OK" : "BUILD_FAILED");
            if (exitWhenDone)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }
    }
}
#endif
