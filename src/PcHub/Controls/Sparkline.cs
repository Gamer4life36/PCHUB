using System.Windows;
using System.Windows.Media;

namespace PcHub.Controls;

/// <summary>Fixed-size ring buffer of samples. No allocation per sample.</summary>
public sealed class Series
{
    private readonly double[] _buf;
    private int _start;

    public Series(int capacity) => _buf = new double[capacity];

    public event Action? Updated;
    public int Count { get; private set; }
    public int Capacity => _buf.Length;
    public double this[int i] => _buf[(_start + i) % _buf.Length];

    public void Push(double v)
    {
        if (Count < _buf.Length)
        {
            _buf[(_start + Count) % _buf.Length] = v;
            Count++;
        }
        else
        {
            _buf[_start] = v;
            _start = (_start + 1) % _buf.Length;
        }

        Updated?.Invoke();
    }
}

/// <summary>Lightweight line chart drawn straight into the render list (one frozen geometry per update).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(Series), typeof(Sparkline), new FrameworkPropertyMetadata(null, OnSeriesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _pen;
    private Brush? _fill;
    private Brush? _penFor;

    public Series? Series
    {
        get => (Series?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Sparkline)d;
        if (e.OldValue is Series old) old.Updated -= s.InvalidateVisual;
        if (e.NewValue is Series now) now.Updated += s.InvalidateVisual;
        s.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var series = Series;
        double w = ActualWidth, h = ActualHeight;
        if (series is null || series.Count < 2 || w <= 0 || h <= 0) return;

        EnsureBrushes();
        double max = Maximum > 0 ? Maximum : 1;
        double step = w / (series.Capacity - 1);
        double x0 = w - (series.Count - 1) * step;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < series.Count; i++)
            {
                double v = Math.Clamp(series[i] / max, 0, 1);
                ctx.LineTo(new Point(x0 + i * step, h - v * (h - 2) - 1), true, true);
            }

            ctx.LineTo(new Point(w, h), false, false);
        }

        geo.Freeze();
        dc.DrawGeometry(_fill, null, geo);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            for (int i = 0; i < series.Count; i++)
            {
                double v = Math.Clamp(series[i] / max, 0, 1);
                var p = new Point(x0 + i * step, h - v * (h - 2) - 1);
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
            }
        }

        line.Freeze();
        dc.DrawGeometry(null, _pen, line);
    }

    private void EnsureBrushes()
    {
        if (ReferenceEquals(_penFor, Stroke) && _pen is not null) return;
        _penFor = Stroke;
        var pen = new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        _pen = pen;

        Color c = Stroke is SolidColorBrush sc ? sc.Color : Colors.LimeGreen;
        var fill = new LinearGradientBrush(Color.FromArgb(90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        fill.Freeze();
        _fill = fill;
    }
}
