// The one definition of a ground-truth box. Shown in the UI help and written into every annotation file and
// session.json, so the data always carries the rule it was made under.
namespace QuestDetect3D.Annotation
{
    public static class GroundTruth
    {
        public const int DefinitionVersion = 1;

        public const string Definition =
            "The tightest UPRIGHT box that sits on the floor, is aligned with the object's front, and contains the " +
            "whole object. Armrests, casters and base tips count. Corners usually float in the air, so EXTREMES are " +
            "marked, not corners.";

        /// <summary>How boxes are stored (matches UprightBox).</summary>
        public const string BoxConvention =
            "Room frame: Unity convention, LEFT-handed (x right, y up, z forward), gravity-aligned, metres. " +
            "bottom_center = centre of the bottom face; yaw_deg = heading of the box local +Z (object front) around +Y, " +
            "0 = room +Z, positive towards room +X; size = [width (local X), height (Y), depth (local Z)].";

        public const string RoomFrameDefinition =
            "Origin = centre of the room tape cross on the floor. +Y = up (gravity). +Z = from the cross centre towards " +
            "the second touched point on one tape arm, projected on the floor. +X = right of +Z (Unity left-handed).";
    }
}
