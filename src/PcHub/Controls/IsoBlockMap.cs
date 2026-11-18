using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PcHub.Controls;

/// <summary>
/// An isometric "3D" block map of a drive: raised cubes for used space (orange ones are the fragmented
/// share), flat tiles for free space. Drawn to scale from percentages; the scatter of fragmented blocks is
/// a stable illustration, not a cluster-by-cluster map.
///
/// While <see cref="IsActive"/> is true (the drive is being optimized) it animates: a light sweep moves across
/// with <see cref="Progress"/>, cubes ripple up as it passes, and fragmented cubes jump out, turn blue in
/// mid-air and land back in place. Frames run only while active (~30 fps).
/// </summary>
public sealed class IsoBlockMap : Control
{
    private const int Cols = 14;
    private const int Rows = 6;
    private const double HopSeconds = 0.75;

    public static readonly StyledProperty<double> UsedFractionProperty = AvaloniaProperty.Register<IsoBlockMap, double>(nameof(UsedFraction));
    public static readonly StyledProperty<double> FragmentedFractionProperty = AvaloniaProperty.Register<IsoBlockMap, double>(nameof(FragmentedFraction));
    public static readonly StyledProperty<int> SeedProperty = AvaloniaProperty.Register<IsoBlockMap, int>(nameof(Seed), 1);
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<IsoBlockMap, bool>(nameof(IsActive));
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<IsoBlockMap, double>(nameof(Progress));

    private static readonly Shades Used = MakeShades(Color.FromRgb(0x4A, 0x8F, 0xE8));
    private static readonly Shades Fragmented = MakeShades(Color.FromRgb(0xF2, 0x8C, 0x3A));
    private static readonly Shades Glow = MakeShades(Color.FromRgb(0x7C, 0xC4, 0xFF));
    private static readonly IBrush FreeTile = new ImmutableSolidColorBrush(Color.FromRgb(0x23, 0x29, 0x33));
    private static readonly IBrush Hole = new ImmutableSolidColorBrush(Color.FromRgb(0x10, 0x13, 0x18));
    private static readonly IPen Edge = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0x0B, 0x0E, 0x13)), 0.6);
    private static readonly IPen FreeEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x2D, 0x34, 0x40)), 0.6);
    private static readonly IBrush[] TrimFlash = Enumerable.Range(0, 11)
        .Select(i => (IBrush)new ImmutableSolidColorBrush(Color.FromArgb((byte)(i * 14), 0x7C, 0xC4, 0xFF))).ToArray();
    private static readonly IBrush DoneSheen = new ImmutableSolidColorBrush(Color.FromArgb(30, 0xFF, 0xFF, 0xFF));

    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;

    static IsoBlockMap() => AffectsRender<IsoBlockMap>(UsedFractionProperty, FragmentedFractionProperty, SeedProperty, IsActiveProperty, ProgressProperty);

    public double UsedFraction { get => GetValue(UsedFractionProperty); set => SetValue(UsedFractionProperty, value); }
    public double FragmentedFraction { get => GetValue(FragmentedFractionProperty); set => SetValue(FragmentedFractionProperty, value); }
    public int Seed { get => GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty) UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopTimer();
    }

    /// <summary>Frames only while active and on screen; nothing runs otherwise.</summary>
    private void UpdateTimer()
    {
        if (IsActive && TopLevel.GetTopLevel(this) is not null)
        {
            if (_timer is not null) return;
            _clock.Restart();
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
            _timer.Start();
        }
        else
        {
            StopTimer();
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        _clock.Stop();
    }

    private static double Headroom(double cw) => cw * 1.4; // room above for hopping cubes

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width;
        double cw = 2 * w / (Cols + Rows);
        return new Size(w, Headroom(cw) + (Cols + Rows) * cw / 4 + cw * 0.5 + 2);
    }

    public override void Render(DrawingContext context)
    {
        var dc = context;
        double w = Bounds.Width;
        if (w <= 0) return;

        double cw = 2 * w / (Cols + Rows);   // cube width
        double th = cw / 2;                   // top face height
        double ch = cw * 0.5;                 // side height
        double originX = Rows * cw / 2;
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
        double sweep = active ? progress * (Cols + 1) - 0.5 + 0.35 * Math.Sin(t * 2.2) : double.NaN;

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

        // Back to front, so nearer cubes overlap farther ones.
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
                    Diamond(dc, x, yTop + ch, cw, th, FreeTile, FreeEdge);
                    if (active)
                    {
                        double near = 1 - Math.Min(1, Math.Abs(c - sweep) / 1.6);
                        if (near > 0) Diamond(dc, x, yTop + ch, cw, th, TrimFlash[(int)(near * 10)], null);
                    }

                    continue;
                }

                if (index == hopIndex)
                {
                    Diamond(dc, x, yTop + ch, cw, th, Hole, FreeEdge);
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

                Cube(dc, x, yTop - lift, cw, th, ch, shades);
                if (active && c < sweep - 1.8) Diamond(dc, x, yTop - lift, cw, th, DoneSheen, null);
            }
        }

        if (hopIndex >= 0)
        {
            int r = hopIndex / Cols, c = hopIndex % Cols;
            double x = originX + (c - r) * cw / 2;
            double yTop = originY + (c + r) * th / 2;
            double arc = Math.Sin(Math.PI * hopPhase);
            double wobble = Math.Sin(hopPhase * Math.PI * 2) * cw * 0.06;
            Cube(dc, x + wobble, yTop - arc * ch * 2.4, cw, th, ch, hopPhase < 0.5 ? Fragmented : Used);
        }
    }

    private static void Cube(DrawingContext dc, double x, double y, double cw, double th, double ch, Shades s)
    {
        double hw = cw / 2, hh = th / 2;
        dc.DrawGeometry(s.Left, Edge, Poly(new(x - hw, y + hh), new(x, y + th), new(x, y + th + ch), new(x - hw, y + hh + ch)));
        dc.DrawGeometry(s.Right, Edge, Poly(new(x + hw, y + hh), new(x, y + th), new(x, y + th + ch), new(x + hw, y + hh + ch)));
        Diamond(dc, x, y, cw, th, s.Top, Edge);
    }

    private static void Diamond(DrawingContext dc, double x, double y, double cw, double th, IBrush fill, IPen? pen)
    {
        double hw = cw / 2, hh = th / 2;
        dc.DrawGeometry(fill, pen, Poly(new(x, y), new(x + hw, y + hh), new(x, y + th), new(x - hw, y + hh)));
    }

    private static StreamGeometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i]);
            ctx.EndFigure(true);
        }

        return g;
    }

    private sealed record Shades(IBrush Top, IBrush Left, IBrush Right);

    private static Shades MakeShades(Color c)
    {
        static Color Scale(Color c, double f) => Color.FromRgb(
            (byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));

        // Light from the top-left: bright top, mid left face, dark right face.
        var top = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Scale(c, 1.35), 0), new GradientStop(Scale(c, 1.12), 1) },
        }.ToImmutable();
        return new Shades(top, new ImmutableSolidColorBrush(Scale(c, 0.85)), new ImmutableSolidColorBrush(Scale(c, 0.55)));
    }
}
