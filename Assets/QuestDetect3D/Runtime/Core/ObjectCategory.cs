// Object categories the detectors can produce, with physical priors used by the box fitter and confidence.
//
// Objectron (MediaPipe) only knows Chair / Cup / Sneaker / Camera. The YOLO + depth pipeline maps the COCO
// classes that matter for indoor mobility (furniture and floor obstacles) onto the categories below; anything
// else YOLO detects is ignored. Categories are grouped (Seating, Table, ...) so that a chair that YOLO calls a
// "bench" in one frame still lands on the same track; the track label is then a vote over all its frames.
using UnityEngine;

namespace QuestDetect3D
{
    public enum ObjectCategory
    {
        Chair,
        Cup,
        Sneaker,
        Camera,
        Sofa,
        Bench,
        Table,
        Bed,
        Toilet,
        Plant,
        Screen,
        Fridge,
        Luggage,
    }

    public enum SupportSurface
    {
        /// <summary>Object stands on the room floor (chairs, shoes).</summary>
        Floor,
        /// <summary>Object stands on any horizontal surface (table, desk, shelf).</summary>
        AnyHorizontal,
    }

    /// <summary>Association group: tracks only absorb detections of the same group.</summary>
    public enum CategoryGroup
    {
        Seating,
        Table,
        Bed,
        Small,
        Fixture,
        Plant,
        Screen,
        Luggage,
    }

    public readonly struct CategoryInfo
    {
        public readonly ObjectCategory Category;
        public readonly string DisplayName;
        public readonly SupportSurface Support;
        /// <summary>Plausible range (m) of the shorter footprint side.</summary>
        public readonly Vector2 ShortSide;
        /// <summary>Plausible range (m) of the longer footprint side.</summary>
        public readonly Vector2 LongSide;
        /// <summary>Plausible range (m) of the height.</summary>
        public readonly Vector2 Height;
        /// <summary>Front/back are visually ambiguous (e.g. cups) → yaw averaged modulo 180° (Objectron path).</summary>
        public readonly bool YawSymmetric180;
        public readonly float TypicalHeight;
        public readonly CategoryGroup Group;

        public CategoryInfo(
            ObjectCategory category,
            string displayName,
            SupportSurface support,
            Vector2 shortSide,
            Vector2 longSide,
            Vector2 height,
            bool yawSymmetric180,
            CategoryGroup group)
        {
            Category = category;
            DisplayName = displayName;
            Support = support;
            ShortSide = shortSide;
            LongSide = longSide;
            Height = height;
            YawSymmetric180 = yawSymmetric180;
            TypicalHeight = 0.5f * (height.x + height.y);
            Group = group;
        }

        /// <summary>Typical footprint (short, long) used to complete the unseen back of an object.</summary>
        public float TypicalShort => 0.5f * (ShortSide.x + ShortSide.y);
        public float TypicalLong => 0.5f * (LongSide.x + LongSide.y);
    }

    public static class Categories
    {
        /// <summary>Categories the Objectron (MediaPipe) path can run.</summary>
        public static readonly ObjectCategory[] Cycle =
        {
            ObjectCategory.Chair,
            ObjectCategory.Cup,
            ObjectCategory.Sneaker,
            ObjectCategory.Camera,
        };

        public static CategoryInfo Get(ObjectCategory category)
        {
            switch (category)
            {
                case ObjectCategory.Cup:
                    return new CategoryInfo(category, "Cup", SupportSurface.AnyHorizontal,
                        new Vector2(0.05f, 0.13f), new Vector2(0.06f, 0.18f), new Vector2(0.06f, 0.20f), true,
                        CategoryGroup.Small);
                case ObjectCategory.Sneaker:
                    return new CategoryInfo(category, "Shoe", SupportSurface.Floor,
                        new Vector2(0.07f, 0.16f), new Vector2(0.20f, 0.36f), new Vector2(0.06f, 0.20f), false,
                        CategoryGroup.Small);
                case ObjectCategory.Camera:
                    return new CategoryInfo(category, "Camera", SupportSurface.AnyHorizontal,
                        new Vector2(0.04f, 0.16f), new Vector2(0.07f, 0.26f), new Vector2(0.05f, 0.20f), false,
                        CategoryGroup.Small);
                case ObjectCategory.Sofa:
                    return new CategoryInfo(category, "Sofa", SupportSurface.Floor,
                        new Vector2(0.70f, 1.10f), new Vector2(1.20f, 3.00f), new Vector2(0.65f, 1.10f), false,
                        CategoryGroup.Seating);
                case ObjectCategory.Bench:
                    return new CategoryInfo(category, "Bench", SupportSurface.Floor,
                        new Vector2(0.30f, 0.80f), new Vector2(0.80f, 2.20f), new Vector2(0.35f, 1.10f), false,
                        CategoryGroup.Seating);
                case ObjectCategory.Table:
                    return new CategoryInfo(category, "Table", SupportSurface.Floor,
                        new Vector2(0.45f, 1.20f), new Vector2(0.60f, 2.60f), new Vector2(0.40f, 1.10f), false,
                        CategoryGroup.Table);
                case ObjectCategory.Bed:
                    return new CategoryInfo(category, "Bed", SupportSurface.Floor,
                        new Vector2(0.80f, 2.00f), new Vector2(1.80f, 2.40f), new Vector2(0.35f, 1.30f), false,
                        CategoryGroup.Bed);
                case ObjectCategory.Toilet:
                    return new CategoryInfo(category, "Toilet", SupportSurface.Floor,
                        new Vector2(0.35f, 0.50f), new Vector2(0.50f, 0.80f), new Vector2(0.60f, 0.90f), false,
                        CategoryGroup.Fixture);
                case ObjectCategory.Plant:
                    return new CategoryInfo(category, "Plant", SupportSurface.AnyHorizontal,
                        new Vector2(0.15f, 0.90f), new Vector2(0.15f, 1.20f), new Vector2(0.20f, 2.00f), false,
                        CategoryGroup.Plant);
                case ObjectCategory.Screen:
                    return new CategoryInfo(category, "Screen", SupportSurface.AnyHorizontal,
                        new Vector2(0.03f, 0.40f), new Vector2(0.30f, 1.80f), new Vector2(0.20f, 1.20f), false,
                        CategoryGroup.Screen);
                case ObjectCategory.Fridge:
                    return new CategoryInfo(category, "Fridge", SupportSurface.Floor,
                        new Vector2(0.50f, 0.90f), new Vector2(0.55f, 1.00f), new Vector2(0.80f, 2.10f), false,
                        CategoryGroup.Fixture);
                case ObjectCategory.Luggage:
                    return new CategoryInfo(category, "Bag", SupportSurface.AnyHorizontal,
                        new Vector2(0.10f, 0.45f), new Vector2(0.25f, 0.90f), new Vector2(0.20f, 0.90f), false,
                        CategoryGroup.Luggage);
                case ObjectCategory.Chair:
                default:
                    return new CategoryInfo(ObjectCategory.Chair, "Chair", SupportSurface.Floor,
                        new Vector2(0.33f, 0.75f), new Vector2(0.38f, 0.90f), new Vector2(0.62f, 1.25f), false,
                        CategoryGroup.Seating);
            }
        }

        public static ObjectCategory Next(ObjectCategory current)
        {
            for (var i = 0; i < Cycle.Length; i++)
            {
                if (Cycle[i] == current)
                {
                    return Cycle[(i + 1) % Cycle.Length];
                }
            }

            return Cycle[0];
        }

        /// <summary>
        /// COCO / darknet label → category for the YOLO path. Returns false for classes we do not track
        /// (people and animals move; vehicles, food, etc. are irrelevant indoors).
        /// </summary>
        public static bool TryFromCocoLabel(string label, out ObjectCategory category)
        {
            switch (label.Trim().ToLowerInvariant())
            {
                case "chair": category = ObjectCategory.Chair; return true;
                case "sofa":
                case "couch": category = ObjectCategory.Sofa; return true;
                case "bench": category = ObjectCategory.Bench; return true;
                case "diningtable":
                case "dining table": category = ObjectCategory.Table; return true;
                case "bed": category = ObjectCategory.Bed; return true;
                case "toilet": category = ObjectCategory.Toilet; return true;
                case "pottedplant":
                case "potted plant": category = ObjectCategory.Plant; return true;
                case "tvmonitor":
                case "tv": category = ObjectCategory.Screen; return true;
                case "refrigerator": category = ObjectCategory.Fridge; return true;
                case "suitcase":
                case "backpack": category = ObjectCategory.Luggage; return true;
                default: category = ObjectCategory.Chair; return false;
            }
        }
    }
}
