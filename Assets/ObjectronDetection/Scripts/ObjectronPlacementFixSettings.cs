// Deprecated: use ObjectronPlacementOptions on ObjectronCupDetectionManager. Menu syncs via PlacementOptions.

namespace QuestObjectron
{
    public static class ObjectronPlacementFixSettings
    {
        public static ObjectronPlacementOptions Active { get; set; }

        public static bool UseUnityCameraFrame
        {
            get => Active?.UseUnityCameraFrame ?? true;
            set { if (Active != null) Active.UseUnityCameraFrame = value; }
        }

        public static bool OrientationMaskFallback
        {
            get => Active?.UseMaskWhenBadOrientation ?? true;
            set { if (Active != null) Active.UseMaskWhenBadOrientation = value; }
        }

        public static bool Mirror3DWhenFlipped
        {
            get => Active?.Mirror3DLocalXWhenFlipped ?? false;
            set { if (Active != null) Active.Mirror3DLocalXWhenFlipped = value; }
        }

        public static string Summary => Active?.Summary ?? "placement_options=none";
    }
}
