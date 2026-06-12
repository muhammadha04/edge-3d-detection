// Tear down Objectron detection when leaving chair/box scenes for the start menu.

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronSessionCleanup
    {
        /// <summary>Clear persistent VR visuals before entering an Objectron scene (idempotent).</summary>
        public static void BeginFreshSession()
        {
            ObjectronQuestVisuals.DestroyPersistentWorldRoot();
            ObjectronLabeledBoxVisuals.DestroyPersistentWorldRoot();
            ObjectronTablePlaneSnap.ClearAllSmoothedTableY();
            ObjectronFloorPlaneSnap.ClearAllSmoothedFloorY();
        }

        /// <summary>Stop inference and remove all Objectron state when returning to the main menu.</summary>
        public static void LeaveObjectronScene()
        {
            var chairManagers = Object.FindObjectsByType<ObjectronChairDetectionManager>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var manager in chairManagers)
            {
                manager.ShutdownForSceneExit();
            }

            var boxDebugManagers = Object.FindObjectsByType<ObjectronBoxDebugManager>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var manager in boxDebugManagers)
            {
                manager.ShutdownForSceneExit();
            }

            var questVisuals = Object.FindObjectsByType<ObjectronQuestVisuals>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var visuals in questVisuals)
            {
                visuals.ClearAllForSceneExit();
            }

            var labeledVisuals = Object.FindObjectsByType<ObjectronLabeledBoxVisuals>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var visuals in labeledVisuals)
            {
                visuals.ClearAllForSceneExit();
            }

            var debugOverlays = Object.FindObjectsByType<ObjectronDetectionDebug>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var debug in debugOverlays)
            {
                debug.Clear();
            }

            var bboxDrawers = Object.FindObjectsByType<OrientedBBoxDrawer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var drawer in bboxDrawers)
            {
                drawer.SetDetections(null);
            }

            BeginFreshSession();
            QuestObjectronLogger.Boot("objectron_session_cleanup complete");
        }
    }
}
