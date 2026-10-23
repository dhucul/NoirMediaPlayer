using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer.Controls;

/// <summary>Windows/WPF text rendering shared by the video overlay and PNG snapshots.</summary>
public sealed class SubtitlePresenter : FrameworkElement
{
    private SubtitleCue? _cue;
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SubtitlePresenter),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PictureAspectRatioProperty = DependencyProperty.Register(nameof(PictureAspectRatio), typeof(double), typeof(SubtitlePresenter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double PictureAspectRatio { get => (double)GetValue(PictureAspectRatioProperty); set => SetValue(PictureAspectRatioProperty, value); }

    internal void SetCue(SubtitleCue? cue)
    {
        var text = cue?.Text ?? string.Empty;
        if (ReferenceEquals(_cue, cue) && Text == text) return;
        _cue = cue;
        Text = text;
        // Consecutive cues can have identical text and different inline styles.
        InvalidateVisual();
    }

    internal SubtitleCue? Cue => _cue;

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0 || string.IsNullOrEmpty(Text)) return;
        var picture = new Rect(0, 0, ActualWidth, ActualHeight);
        if (PictureAspectRatio > 0)
        {
            var width = Math.Min(picture.Width, picture.Height * PictureAspectRatio);
            var height = width / PictureAspectRatio;
            picture = new Rect((picture.Width - width) / 2, (picture.Height - height) / 2, width, height);
        }
        var cue = _cue?.Text == Text ? _cue : null;
        DrawCaption(drawing, Text, cue?.Styles, cue?.Alignment ?? SubtitleAlignment.BottomCenter,
            picture, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static void DrawCaption(DrawingContext drawing, string text, IReadOnlyList<SubtitleStyleRange>? styles,
        SubtitleAlignment alignment, Rect picture, double pixelsPerDip)
    {
        var fontSize = Math.Clamp(picture.Height * 0.045, 10, 64);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), fontSize, Brushes.White, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, picture.Width * 0.9),
            TextAlignment = alignment switch
            {
                SubtitleAlignment.BottomLeft or SubtitleAlignment.MiddleLeft or SubtitleAlignment.TopLeft => TextAlignment.Left,
                SubtitleAlignment.BottomRight or SubtitleAlignment.MiddleRight or SubtitleAlignment.TopRight => TextAlignment.Right,
                _ => TextAlignment.Center
            },
            Trimming = TextTrimming.None
        };
        if (styles is not null)
        {
            foreach (var range in styles)
            {
                if (range.Language is { } language) formatted.SetCulture(CultureInfo.GetCultureInfo(language), range.Start, range.Length);
                if ((range.Style & SubtitleStyle.Bold) != 0) formatted.SetFontWeight(FontWeights.Bold, range.Start, range.Length);
                if ((range.Style & SubtitleStyle.Italic) != 0) formatted.SetFontStyle(FontStyles.Italic, range.Start, range.Length);
                if ((range.Style & SubtitleStyle.Underline) != 0) formatted.SetTextDecorations(TextDecorations.Underline, range.Start, range.Length);
                if (range.Foreground is { } color)
                {
                    var brush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
                    brush.Freeze();
                    formatted.SetForegroundBrush(brush, range.Start, range.Length);
                }
            }
        }
        var top = alignment switch
        {
            SubtitleAlignment.TopLeft or SubtitleAlignment.TopCenter or SubtitleAlignment.TopRight => picture.Top + picture.Height * 0.045,
            SubtitleAlignment.MiddleLeft or SubtitleAlignment.MiddleCenter or SubtitleAlignment.MiddleRight => picture.Top + (picture.Height - formatted.Height) / 2,
            _ => picture.Bottom - picture.Height * 0.045 - formatted.Height
        };
        top = Math.Clamp(top, picture.Top, Math.Max(picture.Top, picture.Bottom - formatted.Height));
        var origin = new Point(picture.Left + picture.Width * 0.05, top);
        var geometry = formatted.BuildGeometry(origin);
        drawing.PushClip(new RectangleGeometry(picture));
        if (styles is not null)
        {
            foreach (var range in styles)
            {
                if (range.Background is not { } color) continue;
                // Windows text layout supplies the shaped selection boxes, including
                // line wrapping and bidirectional text. Paint these behind the glyphs.
                var bounds = formatted.BuildHighlightGeometry(origin, range.Start, range.Length);
                var brush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
                brush.Freeze();
                drawing.DrawGeometry(brush, null, bounds);
            }
        }
        drawing.DrawGeometry(Brushes.Black, new Pen(Brushes.Black, Math.Max(1, fontSize * 0.1)) { LineJoin = PenLineJoin.Round }, geometry);
        if (styles?.Any(range => range.Foreground is not null) == true)
        {
            // DrawText can rasterize differently on the transparent HWND overlay and
            // an opaque PNG. Use WPF's shaped glyph outlines for both, preserving the
            // foreground brush attached to each run (BuildGeometry discards brushes).
            var foreground = new DrawingVisual();
            using (var textDrawing = foreground.RenderOpen()) textDrawing.DrawText(formatted, origin);
            DrawForegroundGeometry(drawing, foreground.Drawing);
        }
        else drawing.DrawGeometry(Brushes.White, null, geometry);
        drawing.Pop();
    }

    private static void DrawForegroundGeometry(DrawingContext context, Drawing drawing)
    {
        switch (drawing)
        {
            case GlyphRunDrawing glyph:
                context.DrawGeometry(glyph.ForegroundBrush, null, glyph.GlyphRun.BuildGeometry());
                break;
            case GeometryDrawing shape: // Text decorations, including underlines.
                context.DrawGeometry(shape.Brush, shape.Pen, shape.Geometry);
                break;
            case DrawingGroup group:
                var transformed = group.Transform is { } transform && !transform.Value.IsIdentity;
                if (transformed) context.PushTransform(group.Transform);
                if (group.ClipGeometry is not null) context.PushClip(group.ClipGeometry);
                foreach (var child in group.Children) DrawForegroundGeometry(context, child);
                if (group.ClipGeometry is not null) context.Pop();
                if (transformed) context.Pop();
                break;
        }
    }

    internal static BitmapSource Composite(BitmapSource image, SubtitleCue? cue)
    {
        if (cue is null || string.IsNullOrEmpty(cue.Text)) return image;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            drawing.DrawImage(image, bounds);
            DrawCaption(drawing, cue.Text, cue.Styles, cue.Alignment, bounds, 1);
        }
        var rendered = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }
}
