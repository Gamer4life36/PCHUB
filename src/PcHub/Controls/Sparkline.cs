using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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

/// <summary>Lightweight line chart drawn straight into the render list.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<Series?> SeriesProperty = AvaloniaProperty.Register<Sparkline, Series?>(nameof(Series));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.LimeGreen);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 100d);

    private IPen? _pen;
    private IBrush? _fill;
    private IBrush? _penFor;

    static Sparkline() => AffectsRender<Sparkline>(StrokeProperty, MaximumProperty, SeriesProperty);

    public Series? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SeriesProperty) return;
        if (change.OldValue is Series old) old.Updated -= InvalidateVisual;
        if (change.NewValue is Series now) now.Updated += InvalidateVisual;
    }

    public override void Render(DrawingContext context)
    {
        var series = Series;
        double w = Bounds.Width, h = Bounds.Height;
        if (series is null || series.Count < 2 || w <= 0 || h <= 0) return;

        EnsureBrushes();
        double max = Maximum > 0 ? Maximum : 1;
        double step = w / (series.Capacity - 1);
        double x0 = w - (series.Count - 1) * step;
        Point At(int i) => new(x0 + i * step, h - Math.Clamp(series[i] / max, 0, 1) * (h - 2) - 1);

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(x0, h), true);
            for (int i = 0; i < series.Count; i++) ctx.LineTo(At(i));
            ctx.LineTo(new Point(w, h));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(_fill, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(At(0), false);
            for (int i = 1; i < series.Count; i++) ctx.LineTo(At(i));
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, _pen, line);
    }

    private void EnsureBrushes()
    {
        var stroke = Stroke ?? Brushes.LimeGreen;
        if (ReferenceEquals(_penFor, stroke) && _pen is not null) return;
        _penFor = stroke;
        Color c = stroke is ISolidColorBrush sc ? sc.Color : Colors.LimeGreen;
        _pen = new ImmutablePen(new ImmutableSolidColorBrush(c), 1.5, lineJoin: PenLineJoin.Round);
        _fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(90, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
        }.ToImmutable();
    }
}
