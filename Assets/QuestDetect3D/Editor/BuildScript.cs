#if UNITY_EDITOR
// Batch build entry point:
//   Unity.exe -batchmode -nographics -projectPath <proj> -buildTarget Android
//             -executeMethod QuestDetect3D.EditorTools.BuildScript.BuildAndroid -logFile Builds/build.log
using System;
using System.IO;
using Newtonsoft.Json.Linq;
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
        public const string Version = "2.2.9";
        public const int VersionCode = 14;
        private const string BuildInfoPath = "Assets/QuestDetect3D/Resources/qd3d_buildinfo.json";

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
                PlayerSettings.bundleVersion = Version;
                PlayerSettings.Android.bundleVersionCode = VersionCode;
                EnableSystemKeyboard();
                WriteBuildInfo();
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

        /// <summary>QD3D Annotate types names with the Quest system keyboard (manifest: oculus.software.overlay_keyboard).</summary>
        private static void EnableSystemKeyboard()
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            if (config != null && !config.requiresSystemKeyboard)
            {
                config.requiresSystemKeyboard = true;
                OVRProjectConfig.CommitProjectConfig(config);
                Debug.Log("QuestDetect3D: enabled requiresSystemKeyboard");
            }
        }

        /// <summary>Versions recorded in every session.json (app, Unity, every resolved package incl. MRUK).</summary>
        private static void WriteBuildInfo()
        {
            var packages = new JObject();
            try
            {
                var lockJson = JObject.Parse(File.ReadAllText("Packages/packages-lock.json"));
                foreach (var dep in (JObject)lockJson["dependencies"])
                {
                    packages[dep.Key] = dep.Value["version"];
                }
            }
            catch (Exception e)
            {
                packages["error"] = e.Message;
            }

            var info = new JObject
            {
                ["app_version"] = Version,
                ["version_code"] = VersionCode,
                ["build_utc"] = DateTime.UtcNow.ToString("o"),
                ["unity"] = Application.unityVersion,
                ["mruk"] = packages["com.meta.xr.mrutilitykit"],
                ["meta_core"] = packages["com.meta.xr.sdk.core"],
                ["inference_engine"] = packages["com.unity.ai.inference"],
                ["packages"] = packages,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(BuildInfoPath));
            File.WriteAllText(BuildInfoPath, info.ToString());
            AssetDatabase.ImportAsset(BuildInfoPath);
        }
    }
}
#endif
