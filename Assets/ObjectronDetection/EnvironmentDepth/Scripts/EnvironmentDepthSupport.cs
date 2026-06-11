// Diagnostics for Meta Environment Depth API (Quest 3 / 3S).
using System.Linq;
using System.Text;
using Meta.XR.EnvironmentDepth;
using UnityEngine;

namespace QuestObjectron
{
    public static class EnvironmentDepthSupport
    {
        public static bool IsMetaOpenXrPackagePresent()
        {
            return System.AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name.Contains("Unity.XR.MetaOpenXR"));
        }

        public static string BuildUnsupportedReason()
        {
            var sb = new StringBuilder();
            sb.Append("Depth API needs Meta OpenXR plugin. ");

            if (!IsMetaOpenXrPackagePresent())
            {
                sb.Append("Missing package com.unity.xr.meta-openxr. ");
                sb.Append("After Unity resolves packages: Project Settings > XR Plug-in Management > Android > enable OpenXR + Meta Quest feature group. ");
                sb.Append("Then Meta > Tools > Project Setup Tool and fix Depth items. ");
            }
            else
            {
                sb.Append("Package found but runtime not ready — run Meta Project Setup Tool (Vulkan, Multiview, Scene permission). ");
            }

            if (!EnvironmentDepthManager.IsSupported)
            {
                sb.Append("[EnvironmentDepthManager.IsSupported=false]");
            }

            return sb.ToString();
        }

        public static void LogBootDiagnostics()
        {
            var pre = Shader.GetGlobalTexture("_PreprocessedEnvironmentDepthTexture");
            var raw = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
            QuestObjectronLogger.Boot(
                $"depth_diag supported={EnvironmentDepthManager.IsSupported} meta_openxr_pkg={IsMetaOpenXrPackagePresent()} " +
                $"pre_tex={(pre != null)} raw_tex={(raw != null)} unity={Application.unityVersion}");
        }
    }
}
