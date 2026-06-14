// Helpers for restarting Meta Passthrough Camera API after scene reload.

using System.Collections;
using Mediapipe.Unity;
using Meta.XR;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronPassthroughCameraHelper
    {
        private const float DefaultTimeoutSec = 12f;

        public static void RebindImageSource(
            PassthroughCameraAccess cameraAccess,
            PassthroughImageSource imageSource)
        {
            if (cameraAccess == null || imageSource == null)
            {
                return;
            }

            imageSource.Bind(cameraAccess);
            if (imageSource is PassthroughImageSource passthroughSrc)
            {
                passthroughSrc.ApplyQuest3Defaults();
            }
        }

        public static IEnumerator WaitUntilCameraPlaying(
            PassthroughCameraAccess cameraAccess,
            float timeoutSec = DefaultTimeoutSec)
        {
            if (cameraAccess == null)
            {
                QuestObjectronLogger.Err("passthrough_camera missing — cannot start Objectron");
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + timeoutSec;
            var toggled = false;
            while (!cameraAccess.IsPlaying)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    if (!toggled)
                    {
                        toggled = true;
                        QuestObjectronLogger.Boot("passthrough_camera retry — toggling PassthroughCameraAccess");
                        cameraAccess.enabled = false;
                        yield return null;
                        cameraAccess.enabled = true;
                        deadline = Time.realtimeSinceStartup + timeoutSec;
                        continue;
                    }

                    QuestObjectronLogger.Err(
                        "passthrough_camera timeout — PCA never reached IsPlaying after scene reload");
                    yield break;
                }

                yield return null;
            }

            QuestObjectronLogger.Pca("passthrough_camera ready after scene load");
        }

        /// <summary>Prefer Bootstrap in the active Objectron scene over DontDestroyOnLoad leftovers.</summary>
        public static Bootstrap FindSceneBootstrap(MonoBehaviour context)
        {
            if (context == null)
            {
                return FindAnyBootstrap();
            }

            var activeScene = context.gameObject.scene;
            Bootstrap sceneLocal = null;
            Bootstrap fallback = null;
            foreach (var bootstrap in Object.FindObjectsByType<Bootstrap>(
                         FindObjectsInactive.Exclude,
                         FindObjectsSortMode.None))
            {
                fallback ??= bootstrap;
                if (bootstrap.gameObject.scene == activeScene)
                {
                    sceneLocal = bootstrap;
                    break;
                }
            }

            return sceneLocal != null ? sceneLocal : fallback ?? FindAnyBootstrap();
        }

        private static Bootstrap FindAnyBootstrap() =>
            Object.FindAnyObjectByType<Bootstrap>();
    }
}
