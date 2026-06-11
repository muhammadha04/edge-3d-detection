#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace QuestObjectron.Editor
{
    public static class EnvironmentDepthProjectSetup
    {
        private const string MetaOpenXrPackage = "com.unity.xr.meta-openxr";

        [MenuItem("QuestObjectron/Depth API/Print support diagnostics")]
        public static void PrintDiagnostics()
        {
            var hasPackage = System.AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name.Contains("Unity.XR.MetaOpenXR"));

            Debug.Log(
                "QuestObjectron Depth API checklist:\n" +
                $"  1. Package {MetaOpenXrPackage} in manifest: {(hasPackage ? "RESOLVED in editor" : "NOT loaded — open Package Manager or reimport")}\n" +
                "  2. Project Settings > XR Plug-in Management > Android: OpenXR ON, Meta Quest feature group ON\n" +
                "  3. Project Settings > OpenXR > Meta Quest: enable Environment Depth / occlusion features\n" +
                "  4. Meta > Tools > Project Setup Tool: fix all Depth API issues (Vulkan, Multiview, Scene)\n" +
                "  5. OVRManager: Scene permission on startup (depth scene enables this)\n" +
                "  6. Rebuild APK to Quest 3 / 3S only (Quest 2 = unsupported)\n" +
                "See: https://developers.meta.com/horizon/documentation/unity/unity-depthapi-occlusions-get-started/");
        }

        [MenuItem("QuestObjectron/Depth API/Open XR Plug-in Management")]
        public static void OpenXrSettings()
        {
            SettingsService.OpenProjectSettings("Project/XR Plug-in Management");
        }

        [MenuItem("QuestObjectron/Depth API/Open Meta Project Setup Tool")]
        public static void OpenMetaProjectSetupTool()
        {
            EditorApplication.ExecuteMenuItem("Meta/Tools/Project Setup Tool");
        }
    }
}
#endif
