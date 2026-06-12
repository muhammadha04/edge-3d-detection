// Quest 3 face-button helpers (LTouch = X/Y, RTouch = A/B).

namespace QuestObjectron
{
    public static class ObjectronQuestControllerButtons
    {
        public static bool RightBPressed()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
#else
            return false;
#endif
        }

        public static bool RightAPressed()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
#else
            return false;
#endif
        }

        public static bool LeftYPressed()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch);
#else
            return false;
#endif
        }

        public static bool LeftXPressed()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch);
#else
            return false;
#endif
        }

        public static bool RightTriggerPressed()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
#else
            return false;
#endif
        }
    }
}
