// World-space uGUI panel for QD3D Annotate, built entirely from code (no prefabs, no scene edits) and made
// pointable for Meta Interaction SDK rays: Canvas + GraphicRaycaster + PointableCanvas, and a RayInteractable on a
// clipped plane surface of the panel's size.
// 1 canvas pixel = 1 mm, so font size 30 is 3 cm tall: readable at 0.8 m. High contrast: light text on near-black.
// Screens rebuild the content on every state change: Clear(), then Label()/Button()/BeginRow()...
using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.UI;

namespace QuestDetect3D.Annotation
{
    public sealed class AnnPanel
    {
        private const float PixelsPerMetre = 1000f;
        public const int TitleSize = 40;
        public const int TextSize = 28;
        public const int ButtonSize = 30;
        public const int ButtonHeight = 68;

        public static readonly Color Background = new Color(0.04f, 0.05f, 0.07f, 0.94f);
        public static readonly Color TextColor = new Color(0.96f, 0.97f, 0.98f, 1f);
        public static readonly Color DimText = new Color(0.66f, 0.71f, 0.77f, 1f);
        public static readonly Color Accent = new Color(0.31f, 0.89f, 1f, 1f);
        public static readonly Color Warning = new Color(1f, 0.78f, 0.35f, 1f);
        public static readonly Color ButtonColor = new Color(0.16f, 0.21f, 0.29f, 1f);
        public static readonly Color PrimaryButton = new Color(0.05f, 0.36f, 0.62f, 1f);
        public static readonly Color DangerButton = new Color(0.5f, 0.14f, 0.12f, 1f);

        private readonly Font m_font;
        private readonly RectTransform m_content;
        private readonly Text m_title;
        private RectTransform m_parent;

        public Transform Root { get; }
        public float WidthM { get; }
        public float HeightM { get; }

        public bool Visible
        {
            get => Root.gameObject.activeSelf;
            set => Root.gameObject.SetActive(value);
        }

        public string Title
        {
            get => m_title.text;
            set => m_title.text = value;
        }

        public AnnPanel(string name, float widthM, float heightM, Font font)
        {
            m_font = font;
            WidthM = widthM;
            HeightM = heightM;

            // Built inactive so the Interaction SDK components get their dependencies before Awake/Start.
            var root = new GameObject(name);
            root.SetActive(false);
            Root = root.transform;

            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(Root, false);
            var canvasRect = (RectTransform)canvasGo.transform;
            canvasRect.sizeDelta = new Vector2(widthM * PixelsPerMetre, heightM * PixelsPerMetre);
            canvasRect.localScale = Vector3.one / PixelsPerMetre;
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 3f; // sharper dynamic-font glyphs at 1 px = 1 mm
            canvasGo.AddComponent<GraphicRaycaster>();

            var bg = NewRect("Background", canvasRect);
            Stretch(bg);
            bg.gameObject.AddComponent<Image>().color = Background;

            m_title = NewText("Title", canvasRect, "", TitleSize, Accent, TextAnchor.MiddleLeft);
            var titleRect = m_title.rectTransform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.offsetMin = new Vector2(28f, -76f);
            titleRect.offsetMax = new Vector2(-28f, -12f);
            m_title.fontStyle = FontStyle.Bold;

            m_content = NewRect("Content", canvasRect);
            m_content.anchorMin = Vector2.zero;
            m_content.anchorMax = Vector2.one;
            m_content.offsetMin = new Vector2(28f, 20f);
            m_content.offsetMax = new Vector2(-28f, -84f);
            var layout = m_content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            m_parent = m_content;

            // Interaction SDK: pointer events on this surface are forwarded to the Unity canvas.
            var pointable = root.AddComponent<PointableCanvas>();
            pointable.InjectAllPointableCanvas(canvas);

            var surfaceGo = new GameObject("Surface");
            surfaceGo.transform.SetParent(Root, false);
            var plane = surfaceGo.AddComponent<PlaneSurface>();
            plane.InjectAllPlaneSurface(PlaneSurface.NormalFacing.Backward, false);
            var clipper = surfaceGo.AddComponent<BoundsClipper>();
            clipper.Size = new Vector3(widthM, heightM, 0.01f);
            var clipped = surfaceGo.AddComponent<ClippedPlaneSurface>();
            clipped.InjectAllClippedPlaneSurface(plane, new List<IBoundsClipper> { clipper });

            var interactable = root.AddComponent<RayInteractable>();
            interactable.InjectAllRayInteractable(clipped);
            interactable.InjectOptionalPointableElement(pointable);
        }

        /// <summary>Place the panel in front of the head at reading distance, upright, facing the user.</summary>
        public void PlaceInFront(Transform head, float distance = 0.8f, float drop = 0.1f)
        {
            if (head == null)
            {
                return;
            }

            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
            {
                flat = Vector3.forward;
            }

            flat.Normalize();
            Root.position = head.position + flat * distance + Vector3.down * drop;
            // A world-space canvas is read from its -Z side: +Z points away from the user.
            Root.rotation = Quaternion.LookRotation(flat, Vector3.up);
        }

        // ------------------------------------------------------------------ content

        public void Clear()
        {
            m_parent = m_content;
            for (var i = m_content.childCount - 1; i >= 0; i--)
            {
                var child = m_content.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        public Text Label(string text, int size = TextSize, Color? color = null)
        {
            var label = NewText("Label", m_parent, text, size, color ?? TextColor, TextAnchor.UpperLeft);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            // Preferred height comes from the Text itself (wrapped), so long help texts lay out correctly.
            return label;
        }

        public Button Button(string text, Action onClick, bool enabled = true, Color? color = null, int height = ButtonHeight,
            int fontSize = ButtonSize)
        {
            var rect = NewRect("Button " + text, m_parent);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleWidth = 1f;
            var image = rect.gameObject.AddComponent<Image>();
            image.color = Color.white;
            var button = rect.gameObject.AddComponent<Button>();
            var normal = color ?? ButtonColor;
            var colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = Color.Lerp(normal, Color.white, 0.28f);
            colors.pressedColor = Accent;
            colors.selectedColor = normal;
            colors.disabledColor = new Color(0.11f, 0.12f, 0.14f, 1f);
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            button.interactable = enabled;
            var nav = button.navigation;
            nav.mode = Navigation.Mode.None;
            button.navigation = nav;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            var label = NewText("Text", rect, text, fontSize, enabled ? TextColor : new Color(0.45f, 0.48f, 0.52f, 1f),
                TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            label.raycastTarget = false;
            return button;
        }

        /// <summary>Image of fixed height (width from the texture aspect).</summary>
        public RawImage Image(Texture texture, float heightPx)
        {
            var rect = NewRect("Image", m_parent);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = heightPx;
            element.preferredHeight = heightPx;
            var image = rect.gameObject.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            var fitter = rect.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            fitter.aspectRatio = texture != null && texture.height > 0 ? texture.width / (float)texture.height : 1.333f;
            return image;
        }

        /// <summary>Following widgets are laid out side by side until EndRow().</summary>
        public void BeginRow()
        {
            var row = NewRect("Row", m_content);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            m_parent = row;
        }

        public void EndRow() => m_parent = m_content;

        public void Space(float pixels = 12f)
        {
            var rect = NewRect("Space", m_parent);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = pixels;
            element.preferredHeight = pixels;
        }

        // ------------------------------------------------------------------ helpers

        private Text NewText(string name, Transform parent, string text, int size, Color color, TextAnchor anchor)
        {
            var rect = NewRect(name, parent);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = m_font;
            label.fontSize = size;
            label.color = color;
            label.alignment = anchor;
            label.supportRichText = true;
            label.raycastTarget = false;
            label.text = text;
            return label;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
