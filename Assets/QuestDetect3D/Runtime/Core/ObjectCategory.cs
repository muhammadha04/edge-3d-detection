// Object categories the current detector can produce, with physical priors used for confidence.
using UnityEngine;

namespace QuestDetect3D
{
    public enum ObjectCategory
    {
        Chair,
        Cup,
        Sneaker,
        Camera,
    }

    public enum SupportSurface
    {
        /// <summary>Object stands on the room floor (chairs, shoes).</summary>
        Floor,
        /// <summary>Object stands on any horizontal surface (table, desk, shelf).</summary>
        AnyHorizontal,
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
        /// <summary>Front/back are visually ambiguous (e.g. cups) → yaw averaged modulo 180°.</summary>
        public readonly bool YawSymmetric180;
        public readonly float TypicalHeight;

        public CategoryInfo(
            ObjectCategory category,
            string displayName,
            SupportSurface support,
            Vector2 shortSide,
            Vector2 longSide,
            Vector2 height,
            bool yawSymmetric180)
        {
            Category = category;
            DisplayName = displayName;
            Support = support;
            ShortSide = shortSide;
            LongSide = longSide;
            Height = height;
            YawSymmetric180 = yawSymmetric180;
            TypicalHeight = 0.5f * (height.x + height.y);
        }
    }

    public static class Categories
    {
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
                        new Vector2(0.05f, 0.13f), new Vector2(0.06f, 0.18f), new Vector2(0.06f, 0.20f), true);
                case ObjectCategory.Sneaker:
                    return new CategoryInfo(category, "Shoe", SupportSurface.Floor,
                        new Vector2(0.07f, 0.16f), new Vector2(0.20f, 0.36f), new Vector2(0.06f, 0.20f), false);
                case ObjectCategory.Camera:
                    return new CategoryInfo(category, "Camera", SupportSurface.AnyHorizontal,
                        new Vector2(0.04f, 0.16f), new Vector2(0.07f, 0.26f), new Vector2(0.05f, 0.20f), false);
                case ObjectCategory.Chair:
                default:
                    return new CategoryInfo(ObjectCategory.Chair, "Chair", SupportSurface.Floor,
                        new Vector2(0.33f, 0.75f), new Vector2(0.38f, 0.90f), new Vector2(0.62f, 1.25f), false);
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
    }
}
