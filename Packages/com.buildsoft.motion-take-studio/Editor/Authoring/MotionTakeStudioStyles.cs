// Hallmark · genre: modern-minimal · macrostructure: Workbench · theme: Cobalt
// Hallmark · pre-emit critique: P5 H5 E4 S5 R5 V4 · contrast: pass (40–41) · responsive: pass (49)
// States: default · hover · focus · active · disabled · loading · error · success.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    internal sealed class MotionTakeStudioStyles : IDisposable
    {
        private readonly List<Texture2D> _ownedTextures = new List<Texture2D>();
        private readonly GUIStyle _labelBase;
        private readonly GUIStyle _buttonBase;
        private readonly GUIStyle _boxBase;

        private MotionTakeStudioStyles(
            MotionTakeStudioPalette palette,
            GUIStyle labelBase,
            GUIStyle buttonBase,
            GUIStyle boxBase)
        {
            Palette = palette;
            _labelBase = new GUIStyle(labelBase ?? new GUIStyle());
            _buttonBase = new GUIStyle(buttonBase ?? new GUIStyle());
            _boxBase = new GUIStyle(boxBase ?? new GUIStyle());

            Card = CreateSurfaceStyle(palette.Surface, palette.Border, 16);
            Header = CreateSurfaceStyle(palette.Header, palette.Border, 20);

            HeaderEyebrow = CreateTextStyle(9, FontStyle.Bold, palette.MutedText, false);
            HeaderTitle = CreateTextStyle(20, FontStyle.Bold, palette.Text, true);
            HeaderTitle.margin = new RectOffset(0, 0, 4, 4);
            HeaderDescription = CreateTextStyle(11, FontStyle.Normal, palette.MutedText, true);

            SectionTitle = CreateTextStyle(12, FontStyle.Bold, palette.Text, true);
            SectionDescription = CreateTextStyle(10, FontStyle.Normal, palette.MutedText, true);
            SectionDescription.margin = new RectOffset(0, 0, 4, 4);
            TrackerId = new GUIStyle(SectionTitle)
            {
                wordWrap = false,
                clipping = TextClipping.Clip
            };

            StatusPill = CreateSurfaceStyle(palette.SurfaceElevated, palette.Border, 8);
            StatusPill.fontSize = 9;
            StatusPill.fontStyle = FontStyle.Bold;
            StatusPill.alignment = TextAnchor.MiddleCenter;
            StatusPill.normal.textColor = palette.Text;
            StatusPill.wordWrap = true;
            StatusPill.fixedHeight = 32f;

            PrimaryButton = CreateButtonStyle(
                palette.Accent,
                palette.Accent,
                palette.AccentText,
                40f);
            RecordingButton = CreateButtonStyle(
                palette.Recording,
                palette.Recording,
                BestTextColor(palette.Recording, palette.Text, palette.AccentText),
                40f);
            SecondaryButton = CreateButtonStyle(
                palette.SurfaceElevated,
                palette.Border,
                palette.Text,
                36f);

            Segment = CreateButtonStyle(
                palette.SurfaceElevated,
                palette.Border,
                palette.MutedText,
                32f);
            SegmentActive = CreateButtonStyle(
                Mix(palette.Accent, palette.Surface, 0.18f),
                palette.Accent,
                palette.Text,
                32f);
            SegmentComplete = CreateButtonStyle(
                palette.Surface,
                Mix(palette.Accent, palette.Border, 0.70f),
                palette.Text,
                32f);

            IssueRow = CreateNoticeStyle(
                palette.SurfaceElevated,
                palette.Border,
                palette.Text);
            IssueError = CreateNoticeStyle(
                Mix(palette.Error, palette.Surface, 0.12f),
                Mix(palette.Error, palette.Border, 0.72f),
                palette.Text);
            IssueWarning = CreateNoticeStyle(
                Mix(palette.Warning, palette.Surface, 0.12f),
                Mix(palette.Warning, palette.Border, 0.68f),
                palette.Text);
            IssueInfo = CreateNoticeStyle(
                Mix(palette.Accent, palette.Surface, 0.08f),
                palette.Border,
                palette.Text);

            TrackerRow = CreateSurfaceStyle(palette.SurfaceElevated, palette.Border, 8);
            TrackerRow.margin = new RectOffset(0, 0, 4, 4);
            TrackerRowDisconnected = CreateSurfaceStyle(
                Mix(palette.Window, palette.Surface, 0.55f),
                palette.Border,
                8);
            TrackerRowDisconnected.margin = new RectOffset(0, 0, 4, 4);
        }

        public MotionTakeStudioPalette Palette { get; }
        public GUIStyle Card { get; }
        public GUIStyle Header { get; }
        public GUIStyle HeaderEyebrow { get; }
        public GUIStyle HeaderTitle { get; }
        public GUIStyle HeaderDescription { get; }
        public GUIStyle SectionTitle { get; }
        public GUIStyle SectionDescription { get; }
        public GUIStyle TrackerId { get; }
        public GUIStyle StatusPill { get; }
        public GUIStyle PrimaryButton { get; }
        public GUIStyle RecordingButton { get; }
        public GUIStyle SecondaryButton { get; }
        public GUIStyle Segment { get; }
        public GUIStyle SegmentActive { get; }
        public GUIStyle SegmentComplete { get; }
        public GUIStyle IssueRow { get; }
        public GUIStyle IssueError { get; }
        public GUIStyle IssueWarning { get; }
        public GUIStyle IssueInfo { get; }
        public GUIStyle TrackerRow { get; }
        public GUIStyle TrackerRowDisconnected { get; }

        public static MotionTakeStudioStyles Create(bool darkTheme)
        {
            return Create(
                MotionTakeStudioPalette.Create(darkTheme),
                EditorStyles.label,
                EditorStyles.miniButton,
                EditorStyles.helpBox);
        }

        internal static MotionTakeStudioStyles Create(
            MotionTakeStudioPalette palette,
            GUIStyle labelBase,
            GUIStyle buttonBase,
            GUIStyle boxBase)
        {
            return new MotionTakeStudioStyles(palette, labelBase, buttonBase, boxBase);
        }

        public void Dispose()
        {
            foreach (var texture in _ownedTextures)
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            _ownedTextures.Clear();
        }

        private GUIStyle CreateSurfaceStyle(Color fill, Color border, int padding)
        {
            var style = new GUIStyle(_boxBase)
            {
                alignment = TextAnchor.UpperLeft,
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(padding, padding, padding, padding),
                margin = new RectOffset(0, 0, 0, 0),
                wordWrap = true
            };
            style.normal.background = CreateBorderTexture(fill, border);
            style.normal.textColor = Palette.Text;
            return style;
        }

        private GUIStyle CreateTextStyle(
            int fontSize,
            FontStyle fontStyle,
            Color textColor,
            bool wordWrap)
        {
            var style = new GUIStyle(_labelBase)
            {
                fontSize = fontSize,
                fontStyle = fontStyle,
                wordWrap = wordWrap,
                richText = false,
                clipping = wordWrap ? TextClipping.Overflow : TextClipping.Clip,
                margin = new RectOffset(0, 0, 0, 0)
            };
            style.normal.textColor = textColor;
            style.focused.textColor = textColor;
            return style;
        }

        private GUIStyle CreateButtonStyle(
            Color fill,
            Color border,
            Color textColor,
            float height)
        {
            var style = new GUIStyle(_buttonBase)
            {
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(12, 12, 8, 8),
                margin = new RectOffset(4, 4, 4, 4),
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                fixedHeight = height,
                wordWrap = false,
                clipping = TextClipping.Clip
            };

            var hoverFill = Mix(Palette.Text, fill, 0.08f);
            var activeFill = Mix(Palette.Window, fill, 0.12f);
            style.normal.background = CreateButtonTexture(fill, border);
            style.hover.background = CreateButtonTexture(hoverFill, border);
            style.focused.background = CreateFocusTexture(hoverFill);
            style.active.background = CreateButtonTexture(activeFill, border);
            style.onNormal.background = style.normal.background;
            style.onHover.background = style.hover.background;
            style.onFocused.background = style.focused.background;
            style.onActive.background = style.active.background;
            SetStateTextColors(style, textColor);
            return style;
        }

        private GUIStyle CreateNoticeStyle(Color fill, Color border, Color textColor)
        {
            var style = new GUIStyle(_boxBase)
            {
                alignment = TextAnchor.MiddleLeft,
                imagePosition = ImagePosition.ImageLeft,
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(12, 12, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
                fontSize = 10,
                wordWrap = true,
                stretchWidth = true,
                fixedHeight = 0f
            };
            style.normal.background = CreateButtonTexture(fill, border);
            style.hover.background = CreateButtonTexture(Mix(Palette.Text, fill, 0.04f), border);
            style.focused.background = CreateFocusTexture(fill);
            style.active.background = CreateButtonTexture(Mix(Palette.Window, fill, 0.07f), border);
            SetStateTextColors(style, textColor);
            return style;
        }

        private Texture2D CreateBorderTexture(Color fill, Color border)
        {
            var pixels = new[]
            {
                border, border, border,
                border, fill, border,
                border, border, border
            };
            return CreateTexture(3, pixels);
        }

        private Texture2D CreateButtonTexture(Color fill, Color border)
        {
            var pixels = new Color[25];
            for (var y = 0; y < 5; y++)
            {
                for (var x = 0; x < 5; x++)
                {
                    pixels[y * 5 + x] = x == 0 || y == 0 || x == 4 || y == 4
                        ? border
                        : fill;
                }
            }

            return CreateTexture(5, pixels);
        }

        private Texture2D CreateFocusTexture(Color fill)
        {
            var outer = BestTextColor(Palette.Window, Palette.Text, Palette.AccentText);
            var inner = BestTextColor(fill, Palette.Text, Palette.AccentText);
            var pixels = new Color[25];
            for (var y = 0; y < 5; y++)
            {
                for (var x = 0; x < 5; x++)
                {
                    pixels[y * 5 + x] = x == 0 || y == 0 || x == 4 || y == 4
                        ? outer
                        : x == 1 || y == 1 || x == 3 || y == 3
                            ? inner
                            : fill;
                }
            }

            return CreateTexture(5, pixels);
        }

        private Texture2D CreateTexture(int size, Color[] pixels)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "MotionTakeStudio UI Surface",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            _ownedTextures.Add(texture);
            return texture;
        }

        private static void SetStateTextColors(GUIStyle style, Color normal)
        {
            style.normal.textColor = normal;
            style.hover.textColor = normal;
            style.focused.textColor = normal;
            style.active.textColor = normal;
            style.onNormal.textColor = normal;
            style.onHover.textColor = normal;
            style.onFocused.textColor = normal;
            style.onActive.textColor = normal;
        }

        private static Color BestTextColor(Color background, Color first, Color second)
        {
            return MotionTakeStudioPalette.ContrastRatio(first, background) >=
                   MotionTakeStudioPalette.ContrastRatio(second, background)
                ? first
                : second;
        }

        private static Color Mix(Color foreground, Color background, float amount)
        {
            return Color.Lerp(background, foreground, Mathf.Clamp01(amount));
        }
    }
}
