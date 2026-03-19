using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ClaudeUsage.Controls;

public class UsageGauge : FrameworkElement
{
    private const double StartAngle = 135.0;
    private const double SweepAngle = 270.0;

    #region Dependency Properties

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(UsageGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeElapsedPercentProperty =
        DependencyProperty.Register(nameof(TimeElapsedPercent), typeof(double?), typeof(UsageGauge),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(UsageGauge),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ResetTextProperty =
        DependencyProperty.Register(nameof(ResetText), typeof(string), typeof(UsageGauge),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDarkThemeProperty =
        DependencyProperty.Register(nameof(IsDarkTheme), typeof(bool), typeof(UsageGauge),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnThemeChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double? TimeElapsedPercent { get => (double?)GetValue(TimeElapsedPercentProperty); set => SetValue(TimeElapsedPercentProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string ResetText { get => (string)GetValue(ResetTextProperty); set => SetValue(ResetTextProperty, value); }
    public bool IsDarkTheme { get => (bool)GetValue(IsDarkThemeProperty); set => SetValue(IsDarkThemeProperty, value); }

    #endregion

    private static readonly FontFamily SegoeUI = new("Segoe UI");
    private static readonly Typeface BoldTypeface = new(SegoeUI, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface SemiBoldTypeface = new(SegoeUI, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NormalTypeface = new(SegoeUI, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    #region Cached Brushes & Pens (frozen for perf)

    // Theme-dependent cached resources (both themes cached, selected at render time)
    private Brush? _labelBrushDark;
    private Brush? _labelBrushLight;
    private Pen? _bgArcPenDark;
    private Pen? _bgArcPenLight;
    private Brush? _tickBrushDark;
    private Brush? _tickBrushLight;
    private Brush? _scaleBrushDark;
    private Brush? _scaleBrushLight;
    private Brush? _needleBrushDark;
    private Brush? _needleBrushLight;
    private Brush? _outerDotBrushDark;
    private Brush? _outerDotBrushLight;
    private Brush? _innerDotBrushDark;
    private Brush? _innerDotBrushLight;
    private Brush? _resetBrushDark;
    private Brush? _resetBrushLight;

    // Theme-independent
    private static readonly Pen TimeMarkerPen;
    private double _lastArcThick;

    // Cached background arc geometry (invalidated on size change)
    private PathGeometry? _bgArcGeom;
    private double _bgArcGeomRadius;

    // Cached tick ring geometry
    private Geometry? _tickGeomMajor;
    private Geometry? _tickGeomMinor;
    private double _tickGeomRadius;

    static UsageGauge()
    {
        var tmPen = new Pen(Brushes.White, 2.5);
        tmPen.Freeze();
        TimeMarkerPen = tmPen;
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenRoundPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private void EnsureCachedResources()
    {
        // Only build once per theme
        if (_labelBrushDark != null) return;

        _labelBrushDark = FrozenBrush(210, 210, 210);
        _labelBrushLight = FrozenBrush(50, 50, 50);

        _tickBrushDark = FrozenBrush(95, 95, 95);
        _tickBrushLight = FrozenBrush(170, 170, 170);

        _scaleBrushDark = FrozenBrush(150, 150, 150);
        _scaleBrushLight = FrozenBrush(120, 120, 120);

        _needleBrushDark = FrozenBrush(190, 190, 190);
        _needleBrushLight = FrozenBrush(70, 70, 70);

        _outerDotBrushDark = FrozenBrush(155, 155, 155);
        _outerDotBrushLight = FrozenBrush(140, 140, 140);

        _innerDotBrushDark = FrozenBrush(70, 70, 70);
        _innerDotBrushLight = FrozenBrush(230, 230, 230);

        _resetBrushDark = FrozenBrush(120, 130, 140);
        _resetBrushLight = FrozenBrush(120, 120, 120);
    }

    private void EnsureBgArcPen(double arcThick)
    {
        if (_bgArcPenDark != null && Math.Abs(_lastArcThick - arcThick) < 0.01) return;
        _lastArcThick = arcThick;

        var darkBrush = FrozenBrush(55, 55, 55);
        _bgArcPenDark = FrozenRoundPen(darkBrush, arcThick);

        var lightBrush = FrozenBrush(225, 225, 225);
        _bgArcPenLight = FrozenRoundPen(lightBrush, arcThick);
    }

    private static void OnThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Theme change doesn't invalidate cached brushes — we keep both sets
    }

    #endregion

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var dark = IsDarkTheme;
        var value = Math.Clamp(Value, 0, 100);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        EnsureCachedResources();

        var cx = w / 2;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Layout
        var labelFontSize = 13.0;
        var labelAreaH = labelFontSize + 16;
        var bottomTextH = 36.0;

        var availableH = h - labelAreaH - bottomTextH;
        var maxRadiusFromH = availableH / 2.0;
        var maxRadiusFromW = w * 0.40;
        var radius = Math.Min(maxRadiusFromH, maxRadiusFromW);
        var arcThick = radius * 0.26;
        var tickGap = 8.0;

        EnsureBgArcPen(arcThick);

        var cy = labelAreaH + radius + arcThick / 2;

        var valueTextY = cy + radius * 0.7;
        var resetTextY = valueTextY + 16;

        // Draw
        DrawLabel(dc, cx, labelFontSize, dark, labelFontSize, dpi);
        DrawBackgroundArc(dc, cx, cy, radius, arcThick, dark);
        DrawTickRing(dc, cx, cy, radius, arcThick, tickGap, dark);
        DrawFillArc(dc, cx, cy, radius, arcThick, value);

        if (TimeElapsedPercent.HasValue)
            DrawTimeMarker(dc, cx, cy, radius, arcThick, TimeElapsedPercent.Value);

        DrawNeedle(dc, cx, cy, radius, arcThick, tickGap, value, dark);
        DrawScaleLabels(dc, cx, cy, radius, arcThick, dark, dpi);
        DrawValueText(dc, cx, valueTextY, value, dark, dpi);
        DrawResetText(dc, cx, resetTextY, dark, dpi);
    }

    // --- Helper: angle to point on circle ---
    private static Point AngleToPoint(double cx, double cy, double radius, double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return new Point(cx + Math.Cos(rad) * radius, cy + Math.Sin(rad) * radius);
    }

    // --- Helper: build a frozen arc geometry ---
    private static PathGeometry BuildArcGeometry(double cx, double cy, double r, double startAngle, double sweepAngle)
    {
        var start = AngleToPoint(cx, cy, r, startAngle);
        var end = AngleToPoint(cx, cy, r, startAngle + sweepAngle);
        var size = new Size(r, r);
        var isLargeArc = sweepAngle > 180;

        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, size, 0, isLargeArc, SweepDirection.Clockwise, true));

        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        geom.Freeze();
        return geom;
    }

    // --- Helper: draw an arc stroke ---
    private static void DrawArcStroke(DrawingContext dc, Pen pen, double cx, double cy, double r, double startAngle, double sweepAngle)
    {
        if (sweepAngle <= 0) return;
        dc.DrawGeometry(null, pen, BuildArcGeometry(cx, cy, r, startAngle, sweepAngle));
    }

    // --- Drawing methods ---

    private void DrawLabel(DrawingContext dc, double cx, double y, bool dark, double fontSize, double dpi)
    {
        if (string.IsNullOrEmpty(Label)) return;
        var brush = dark ? _labelBrushDark! : _labelBrushLight!;
        var ft = new FormattedText(Label, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, BoldTypeface, fontSize, brush, dpi);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y - ft.Height / 2));
    }

    private void DrawBackgroundArc(DrawingContext dc, double cx, double cy, double r, double thick, bool dark)
    {
        // Cache the arc geometry — it only changes when radius changes
        if (_bgArcGeom == null || Math.Abs(_bgArcGeomRadius - r) > 0.01)
        {
            _bgArcGeom = BuildArcGeometry(cx, cy, r, StartAngle, SweepAngle);
            _bgArcGeomRadius = r;
        }
        var pen = dark ? _bgArcPenDark! : _bgArcPenLight!;
        dc.DrawGeometry(null, pen, _bgArcGeom);
    }

    private static void DrawFillArc(DrawingContext dc, double cx, double cy, double r, double thick, double value)
    {
        if (value <= 0) return;

        var (startColor, endColor) = GetGradientForValue(value);
        var sweep = SweepAngle * value / 100.0;

        var startRad = StartAngle * Math.PI / 180;
        var endRad = (StartAngle + sweep) * Math.PI / 180;
        var startPt = new Point(cx + Math.Cos(startRad) * r, cy + Math.Sin(startRad) * r);
        var endPt = new Point(cx + Math.Cos(endRad) * r, cy + Math.Sin(endRad) * r);

        var gradBrush = new LinearGradientBrush(startColor, endColor, startPt, endPt)
        {
            MappingMode = BrushMappingMode.Absolute
        };

        var pen = new Pen(gradBrush, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DrawArcStroke(dc, pen, cx, cy, r, StartAngle, sweep);
    }

    // Cached tick pens (2 per theme: major + minor)
    private Pen? _tickMajorPenDark, _tickMajorPenLight, _tickMinorPenDark, _tickMinorPenLight;

    private void EnsureTickPens()
    {
        if (_tickMajorPenDark != null) return;
        _tickMajorPenDark = FrozenRoundPen(_tickBrushDark!, 2.8);
        _tickMajorPenLight = FrozenRoundPen(_tickBrushLight!, 2.8);
        _tickMinorPenDark = FrozenRoundPen(_tickBrushDark!, 1.5);
        _tickMinorPenLight = FrozenRoundPen(_tickBrushLight!, 1.5);
    }

    private void DrawTickRing(DrawingContext dc, double cx, double cy, double r, double arcThick, double gap, bool dark)
    {
        EnsureTickPens();

        // Rebuild tick geometry only when radius changes
        if (_tickGeomMajor == null || Math.Abs(_tickGeomRadius - r) > 0.01)
        {
            var arcInner = r - arcThick / 2;
            var tickOuterR = arcInner - gap;
            var tickThick = arcThick * 0.4;
            var tickInnerR = tickOuterR - tickThick;

            var majorGeom = new StreamGeometry();
            var minorGeom = new StreamGeometry();
            using (var majorCtx = majorGeom.Open())
            using (var minorCtx = minorGeom.Open())
            {
                for (double pct = 0; pct <= 100.01; pct += 2.5)
                {
                    var i = (int)Math.Round(pct);
                    var major = i == 20 || i == 50 || i == 80;

                    var angle = CosmeticAngle(pct);
                    var rad = angle * Math.PI / 180;
                    var cos = Math.Cos(rad);
                    var sin = Math.Sin(rad);

                    var inner = major ? tickInnerR : tickOuterR - tickThick * 0.5;
                    var ctx = major ? majorCtx : minorCtx;

                    ctx.BeginFigure(new Point(cx + cos * inner, cy + sin * inner), false, false);
                    ctx.LineTo(new Point(cx + cos * tickOuterR, cy + sin * tickOuterR), true, false);
                }
            }
            majorGeom.Freeze();
            minorGeom.Freeze();
            _tickGeomMajor = majorGeom;
            _tickGeomMinor = minorGeom;
            _tickGeomRadius = r;
        }

        var majorPen = dark ? _tickMajorPenDark! : _tickMajorPenLight!;
        var minorPen = dark ? _tickMinorPenDark! : _tickMinorPenLight!;
        dc.DrawGeometry(null, minorPen, _tickGeomMinor);
        dc.DrawGeometry(null, majorPen, _tickGeomMajor);
    }

    private void DrawScaleLabels(DrawingContext dc, double cx, double cy, double r, double thick, bool dark, double dpi)
    {
        var brush = dark ? _scaleBrushDark! : _scaleBrushLight!;
        var fontSize = 9.0;
        var labelR = r - thick / 2 - 26;
        var labelY = cy;

        var angle20 = StartAngle + SweepAngle * 20.0 / 100;
        var angle80 = StartAngle + SweepAngle * 80.0 / 100;
        var x20 = cx + Math.Cos(angle20 * Math.PI / 180) * labelR;
        var x80 = cx + Math.Cos(angle80 * Math.PI / 180) * labelR;

        var ft20 = new FormattedText("20", CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, NormalTypeface, fontSize, brush, dpi);
        var ft80 = new FormattedText("80", CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, NormalTypeface, fontSize, brush, dpi);

        dc.DrawText(ft20, new Point(x20 - ft20.Width / 2, labelY - ft20.Height / 2));
        dc.DrawText(ft80, new Point(x80 - ft80.Width / 2, labelY - ft80.Height / 2));
    }

    private static void DrawTimeMarker(DrawingContext dc, double cx, double cy, double r, double thick, double pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        var angle = StartAngle + SweepAngle * pct / 100;
        var rad = angle * Math.PI / 180;

        var innerR = r - thick / 2 - 3;
        var outerR = r + thick / 2 + 3;

        dc.DrawLine(TimeMarkerPen,
            new Point(cx + Math.Cos(rad) * innerR, cy + Math.Sin(rad) * innerR),
            new Point(cx + Math.Cos(rad) * outerR, cy + Math.Sin(rad) * outerR));
    }

    private void DrawNeedle(DrawingContext dc, double cx, double cy, double r, double arcThick, double tickGap, double value, bool dark)
    {
        var angle = StartAngle + SweepAngle * value / 100;
        var rad = angle * Math.PI / 180;

        var arcInner = r - arcThick / 2;
        var tipLen = arcInner - tickGap - 12;

        var tipX = cx + Math.Cos(rad) * tipLen;
        var tipY = cy + Math.Sin(rad) * tipLen;

        var needleBrush = dark ? _needleBrushDark! : _needleBrushLight!;

        // 1. Background circle
        var outerDotBrush = dark ? _outerDotBrushDark! : _outerDotBrushLight!;
        dc.DrawEllipse(outerDotBrush, null, new Point(cx, cy), 11, 11);

        // 2. Needle — tapered trapezoid
        var perp = rad + Math.PI / 2;
        double baseHalfW = 7, tipHalfW = 2.5;

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(new Point(tipX + Math.Cos(perp) * tipHalfW, tipY + Math.Sin(perp) * tipHalfW), true, true);
            ctx.LineTo(new Point(cx + Math.Cos(perp) * baseHalfW, cy + Math.Sin(perp) * baseHalfW), true, false);
            ctx.LineTo(new Point(cx - Math.Cos(perp) * baseHalfW, cy - Math.Sin(perp) * baseHalfW), true, false);
            ctx.LineTo(new Point(tipX - Math.Cos(perp) * tipHalfW, tipY - Math.Sin(perp) * tipHalfW), true, false);
        }
        geom.Freeze();
        dc.DrawGeometry(needleBrush, null, geom);

        // Rounded tip
        dc.DrawEllipse(needleBrush, null, new Point(tipX, tipY), tipHalfW, tipHalfW);

        // 3. Inner circle on top
        var innerDotBrush = dark ? _innerDotBrushDark! : _innerDotBrushLight!;
        dc.DrawEllipse(innerDotBrush, null, new Point(cx, cy), 6.5, 6.5);
    }

    private static readonly Brush ValueBrushGreen = FrozenBrush(0x52, 0xD1, 0x7C);
    private static readonly Brush ValueBrushYellow = FrozenBrush(0xFF, 0xB3, 0x57);
    private static readonly Brush ValueBrushRed = FrozenBrush(0xEB, 0x48, 0x24);

    private void DrawValueText(DrawingContext dc, double cx, double y, double value, bool dark, double dpi)
    {
        var brush = value >= 90 ? ValueBrushRed : value >= 70 ? ValueBrushYellow : ValueBrushGreen;
        var ft = new FormattedText($"{(int)value}%", CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, SemiBoldTypeface, 20, brush, dpi);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y - ft.Height / 2));
    }

    private void DrawResetText(DrawingContext dc, double cx, double y, bool dark, double dpi)
    {
        if (string.IsNullOrEmpty(ResetText)) return;
        var brush = dark ? _resetBrushDark! : _resetBrushLight!;
        var ft = new FormattedText(ResetText, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, NormalTypeface, 10, brush, dpi);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y - ft.Height / 2));
    }

    // --- Cosmetic angle mapping ---
    private static double CosmeticAngle(double pct)
    {
        if (pct <= 20) return 135 + (pct / 20) * (180 - 135);
        if (pct <= 50) return 180 + ((pct - 20) / 30) * (270 - 180);
        if (pct <= 80) return 270 + ((pct - 50) / 30) * (360 - 270);
        return 360 + ((pct - 80) / 20) * (405 - 360);
    }

    private static (Color start, Color end) GetGradientForValue(double value)
    {
        if (value >= 90) return (Color.FromRgb(0xFF, 0x92, 0x1F), Color.FromRgb(0xEB, 0x48, 0x24));
        if (value >= 70) return (Color.FromRgb(0xFF, 0xD3, 0x94), Color.FromRgb(0xFF, 0xB3, 0x57));
        return (Color.FromRgb(0x52, 0xD1, 0x7C), Color.FromRgb(0x22, 0x91, 0x8B));
    }

    private static Color GetColorForValue(double value)
    {
        if (value >= 90) return Color.FromRgb(0xEB, 0x48, 0x24);
        if (value >= 70) return Color.FromRgb(0xFF, 0xB3, 0x57);
        return Color.FromRgb(0x52, 0xD1, 0x7C);
    }
}
