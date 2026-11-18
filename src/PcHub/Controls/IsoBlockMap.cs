using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace PcHub.Controls;

/// <summary>
/// An isometric "3D" block map of a drive: raised cubes for used space (orange ones are the
/// fragmented share), flat tiles for free space. Drawn to scale from percentages; the scatter of
/// fragmented blocks is a stable illustration, not a cluster-by-cluster map.
///
/// While <see cref="IsActive"/> is true (the drive is being optimized) it animates: a light sweep
/// moves across with <see cref="Progress"/>, cubes ripple up as it passes, and fragmented cubes
/// jump out, turn blue in mid-air and land back in place. Frames run only while active (~30 fps).
/// </summary>
public sealed class IsoBlockMap : FrameworkElement
{
    private const int Cols = 14;
    private const int Rows = 6;
    private const double FrameMs = 33;
    private const double HopSeconds = 0.75;

    public static readonly DependencyProperty UsedFractionProperty = DependencyProperty.Register(
        nameof(UsedFraction), typeof(double), typeof(IsoBlockMap), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FragmentedFractionProperty = DependencyProperty.Register(
        nameof(FragmentedFraction), typeof(double), typeof(IsoBlockMap), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(int), typeof(IsoBlockMap), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(IsoBlockMap), new FrameworkPropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(IsoBlockMap), new FrameworkPropertyMetadata(0d));

    private static readonly Shades Used = MakeShades(Color.FromRgb(0x4A, 0x8F, 0xE8));
    private static readonly Shades Fragmented = MakeShades(Color.FromRgb(0xF2, 0x8C, 0x3A));
    private static readonly Shades Glow = MakeShades(Color.FromRgb(0x7C, 0xC4, 0xFF));
    private static readonly Brush FreeTile = Frozen(new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x33)));
    private static readonly Brush Hole = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x18)));
    private static readonly Pen Edge = FrozenPen(Color.FromArgb(0x70, 0x0B, 0x0E, 0x13), 0.6);
    private static readonly Pen FreeEdge = FrozenPen(Color.FromRgb(0x2D, 0x34, 0x40), 0.6);
    private static readonly Brush[] TrimFlash = Enumerable.Range(0, 11)
        .Select(i => (Brush)Frozen(new SolidColorBrush(Color.FromArgb((byte)(i * 14), 0x7C, 0xC4, 0xFF)))).ToArray();
    private static readonly Brush[] DoneSheen = Enumerable.Range(0, 11)
        .Select(i => (Brush)Frozen(new SolidColorBrush(Color.FromArgb((byte)(i * 5), 0xFF, 0xFF, 0xFF)))).ToArray();

    private readonly Stopwatch _clock = new();
    private double _lastFrameMs = double.NegativeInfinity;
    private bool _hooked;

    public IsoBlockMap()
    {
        Loaded += (_, _) => UpdateHook();
        Unloaded += (_, _) => Unhook();
    }

    /// <summary>0..1 of the drive in use.</summary>
    public double UsedFraction
    {
        get => (double)GetValue(UsedFractionProperty);
        set => SetValue(UsedFractionProperty, value);
    }

    /// <summary>0..1 of the whole drive that is fragmented (a part of the used share).</summary>
    public double FragmentedFraction
    {
        get => (double)GetValue(FragmentedFractionProperty);
        set => SetValue(FragmentedFractionProperty, value);
    }

    /// <summary>Keeps each drive's scatter pattern stable between refreshes.</summary>
    public int Seed
    {
        get => (int)GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    /// <summary>True while the drive is being optimized; turns the animation on.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>0..1 progress of the current optimization stage.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (IsoBlockMap)d;
        map.UpdateHook();
        map.InvalidateVisual();
    }

    private void UpdateHook()
    {
        if (IsActive && IsLoaded && !_hooked)
        {
            _clock.Restart();
            _lastFrameMs = double.NegativeInfinity;
            CompositionTarget.Rendering += OnFrame;
            _hooked = true;
        }
        else if (!IsActive)
        {
            Unhook();
        }
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
        _clock.Stop();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastFrameMs < FrameMs) return; // ~30 fps is plenty and keeps CPU low
        _lastFrameMs = now;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width;
        return new Size(w, HeightFor(w));
    }

    // Extra headroom on top so hopping cubes stay inside the control.
    private static double Headroom(double cw) => cw * 1.4;

    private static double HeightFor(double width)
    {
        double cw = 2 * width / (Cols + Rows);
        return Headroom(cw) + (Cols + Rows) * cw / 4 + cw * 0.5 + 2;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double w = ActualWidth;
        if (w <= 0) return;

        var dc = drawingContext;
        double cw = 2 * w / (Cols + Rows);   // cube width
        double th = cw / 2;                   // diamond (top face) height
        double ch = cw * 0.5;                 // cube side height
        double originX = Rows * cw / 2;       // x of the back-left corner's top vertex
        double originY = Headroom(cw);

        int cells = Cols * Rows;
        int used = (int)Math.Round(Math.Clamp(UsedFraction, 0, 1) * cells);
        double fragShare = Math.Clamp(FragmentedFraction, 0, 1);
        int frag = Math.Min(used, (int)Math.Round(fragShare * cells));
        if (fragShare > 0 && frag == 0 && used > 0) frag = 1;

        var isFragmented = new bool[cells];
        if (frag > 0)
        {
            var rng = new Random(Seed);
            foreach (int i in Enumerable.Range(0, used).OrderBy(_ => rng.Next()).Take(frag)) isFragmented[i] = true;
        }

        bool active = IsActive;
        double t = _clock.Elapsed.TotalSeconds;
        double progress = Math.Clamp(Progress, 0, 1);
        // The sweep follows real progress, with a gentle idle drift so it never looks frozen at 0%.
        double sweep = active ? progress * (Cols + 1) - 0.5 + 0.35 * Math.Sin(t * 2.2) : double.NaN;

        // The fragmented cube currently hopping: the first unfixed one at or ahead of the sweep.
        int hopIndex = -1;
        double hopPhase = 0;
        if (active && frag > 0)
        {
            int k = (int)(t / HopSeconds);
            hopPhase = t / HopSeconds - k;
            var pending = new List<int>();
            for (int i = 0; i < used; i++)
            {
                if (isFragmented[i] && (i % Cols) >= sweep) pending.Add(i);
            }

            if (pending.Count > 0) hopIndex = pending[k % pending.Count];
        }

        for (int sum = 0; sum <= Cols + Rows - 2; sum++)
        {
            for (int r = 0; r < Rows; r++)
            {
                int c = sum - r;
                if (c < 0 || c >= Cols) continue;
                int index = r * Cols + c;
                double x = originX + (c - r) * cw / 2;
                double yTop = originY + (c + r) * th / 2;

                if (index >= used)
                {
                    DrawDiamond(dc, x, yTop + ch, cw, th, FreeTile, FreeEdge);
                    if (active)
                    {
                        // Trim pass: free space lights up briefly as the sweep crosses it.
                        double near = 1 - Math.Min(1, Math.Abs(c - sweep) / 1.6);
                        if (near > 0) DrawDiamond(dc, x, yTop + ch, cw, th, TrimFlash[(int)(near * 10)], null);
                    }

                    continue;
                }

                if (index == hopIndex)
                {
                    DrawDiamond(dc, x, yTop + ch, cw, th, Hole, FreeEdge); // empty slot while it hops
                    continue;
                }

                double lift = 0;
                Shades shades = isFragmented[index] && !(active && c < sweep) ? Fragmented : Used;
                if (active)
                {
                    double dist = Math.Abs(c - sweep);
                    if (dist < 1.8)
                    {
                        double k2 = 1 - dist / 1.8;
                        lift = ch * 0.45 * k2 * (0.65 + 0.35 * Math.Sin(t * 9 + r * 0.9));
                        if (k2 > 0.55) shades = Glow;
                    }
                }

                DrawCube(dc, x, yTop - lift, cw, th, ch, shades);
                if (active && c < sweep - 1.8)
                {
                    DrawDiamond(dc, x, yTop - lift, cw, th, DoneSheen[6], null); // finished area has a soft sheen
                }
            }
        }

        if (hopIndex >= 0)
        {
            // Drawn last so it passes in front of its neighbours: up, flip to blue at the top, back down.
            int r = hopIndex / Cols, c = hopIndex % Cols;
            double x = originX + (c - r) * cw / 2;
            double yTop = originY + (c + r) * th / 2;
            double arc = Math.Sin(Math.PI * hopPhase);
            double wobble = Math.Sin(hopPhase * Math.PI * 2) * cw * 0.06;
            DrawCube(dc, x + wobble, yTop - arc * ch * 2.4, cw, th, ch, hopPhase < 0.5 ? Fragmented : Used);
        }
    }

    private static void DrawCube(DrawingContext dc, double x, double y, double cw, double th, double ch, Shades s)
    {
        double hw = cw / 2, hh = th / 2;
        var left = Poly(new Point(x - hw, y + hh), new Point(x, y + th), new Point(x, y + th + ch), new Point(x - hw, y + hh + ch));
        var right = Poly(new Point(x + hw, y + hh), new Point(x, y + th), new Point(x, y + th + ch), new Point(x + hw, y + hh + ch));
        dc.DrawGeometry(s.Left, Edge, left);
        dc.DrawGeometry(s.Right, Edge, right);
        DrawDiamond(dc, x, y, cw, th, s.Top, Edge);
    }

    private static void DrawDiamond(DrawingContext dc, double x, double y, double cw, double th, Brush fill, Pen? pen)
    {
        double hw = cw / 2, hh = th / 2;
        dc.DrawGeometry(fill, pen, Poly(new Point(x, y), new Point(x + hw, y + hh), new Point(x, y + th), new Point(x - hw, y + hh)));
    }

    private static StreamGeometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, false);
        }

        g.Freeze();
        return g;
    }

    private sealed record Shades(Brush Top, Brush Left, Brush Right);

    private static Shades MakeShades(Color c)
    {
        static Color Scale(Color c, double f) => Color.FromRgb(
            (byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));

        // Light from the top-left: bright top, mid left face, dark right face.
        var top = new LinearGradientBrush(Scale(c, 1.35), Scale(c, 1.12), 45);
        return new Shades(Frozen(top), Frozen(new SolidColorBrush(Scale(c, 0.85))), Frozen(new SolidColorBrush(Scale(c, 0.55))));
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Pen FrozenPen(Color c, double thickness) => Frozen(new Pen(Frozen(new SolidColorBrush(c)), thickness));
}
