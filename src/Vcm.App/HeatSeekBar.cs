using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Vcm.App;

/// <summary>
/// Seek bar with a "most viewed parts" heat wave above the track. Heat is a normalised
/// per-bucket replay count; taller and warmer means that stretch was re-watched more.
/// </summary>
public sealed class HeatSeekBar : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<HeatSeekBar, double>(nameof(Position));

    public static readonly StyledProperty<double[]?> HeatProperty =
        AvaloniaProperty.Register<HeatSeekBar, double[]?>(nameof(Heat));

    /// <summary>Playback position as a fraction of duration (0..1).</summary>
    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double[]? Heat
    {
        get => GetValue(HeatProperty);
        set => SetValue(HeatProperty, value);
    }

    private double _hoverX = -1;

    /// <summary>Raised with a 0..1 fraction when the user clicks or drags the bar.</summary>
    public event Action<double>? Seeked;

    static HeatSeekBar()
    {
        AffectsRender<HeatSeekBar>(PositionProperty, HeatProperty);
    }

    public HeatSeekBar()
    {
        Height = 46;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hoverX = -1;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var w = Bounds.Width;
        if (w > 0)
        {
            var fraction = Math.Clamp(e.GetPosition(this).X / w, 0, 1);
            Position = fraction;
            Seeked?.Invoke(fraction);
        }
        base.OnPointerPressed(e);
    }

    // Cold → hot ramp. Deliberately amber, so heat never reads as the blue selection accent.
    private static Color HeatColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var stops = new (double T, Color C)[]
        {
            (0.00, Color.FromRgb(0x33, 0x3A, 0x4B)),
            (0.35, Color.FromRgb(0x6B, 0x5E, 0x44)),
            (0.70, Color.FromRgb(0xC8, 0x8E, 0x33)),
            (1.00, Color.FromRgb(0xFF, 0xC4, 0x6B)),
        };
        for (var i = 0; i < stops.Length - 1; i++)
        {
            var (t0, c0) = stops[i];
            var (t1, c1) = stops[i + 1];
            if (t > t1) continue;
            var k = (t - t0) / (t1 - t0);
            return Color.FromRgb(
                (byte)(c0.R + (c1.R - c0.R) * k),
                (byte)(c0.G + (c1.G - c0.G) * k),
                (byte)(c0.B + (c1.B - c0.B) * k));
        }
        return stops[^1].C;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0) return;

        const double trackH = 6;
        var waveTop = 0.0;
        var waveH = h - trackH - 6;
        var trackY = h - trackH;

        var heat = Heat;

        // --- heat wave -------------------------------------------------------------------
        if (heat is { Length: > 1 })
        {
            // One vertical column per pixel keeps the gradient smooth at any window width.
            var cols = (int)Math.Max(1, Math.Floor(w));
            for (var px = 0; px < cols; px++)
            {
                var t = px / (double)cols;
                var idx = t * (heat.Length - 1);
                var i0 = (int)idx;
                var i1 = Math.Min(i0 + 1, heat.Length - 1);
                var v = heat[i0] + (heat[i1] - heat[i0]) * (idx - i0);

                var colH = Math.Max(1.5, v * waveH);
                var played = t <= Position;
                var c = HeatColor(v);
                // Unplayed heat is dimmed so the played portion still reads at a glance.
                var brush = new SolidColorBrush(c, played ? 1.0 : 0.38);
                ctx.FillRectangle(brush, new Rect(px, waveTop + waveH - colH, 1.02, colH));
            }
        }

        // --- track -----------------------------------------------------------------------
        var trackRect = new RoundedRect(new Rect(0, trackY, w, trackH), 3);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3C)), null, trackRect);

        var playedW = Math.Clamp(Position, 0, 1) * w;
        if (playedW > 0)
        {
            var playedRect = new RoundedRect(new Rect(0, trackY, playedW, trackH), 3);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), null, playedRect);
        }

        // --- hover guide -----------------------------------------------------------------
        if (_hoverX >= 0 && _hoverX <= w)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF), 0.45), 1);
            ctx.DrawLine(pen, new Point(_hoverX, 0), new Point(_hoverX, h));
        }

        // --- thumb -----------------------------------------------------------------------
        var cx = playedW;
        ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), null,
            new Point(cx, trackY + trackH / 2), 7, 7);
        ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), null,
            new Point(cx, trackY + trackH / 2), 3.5, 3.5);
    }
}

/// <summary>The cold→hot strip used in the legend next to the seek bar.</summary>
public sealed class HeatLegend : Control
{
    public HeatLegend()
    {
        Width = 86;
        Height = 8;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var cols = (int)Math.Max(1, Math.Floor(w));
        for (var px = 0; px < cols; px++)
        {
            var c = HeatColorPublic(px / (double)cols);
            ctx.FillRectangle(new SolidColorBrush(c), new Rect(px, 0, 1.02, Bounds.Height));
        }
    }

    private static Color HeatColorPublic(double t)
    {
        var stops = new (double T, Color C)[]
        {
            (0.00, Color.FromRgb(0x33, 0x3A, 0x4B)),
            (0.35, Color.FromRgb(0x6B, 0x5E, 0x44)),
            (0.70, Color.FromRgb(0xC8, 0x8E, 0x33)),
            (1.00, Color.FromRgb(0xFF, 0xC4, 0x6B)),
        };
        t = Math.Clamp(t, 0, 1);
        for (var i = 0; i < stops.Length - 1; i++)
        {
            var (t0, c0) = stops[i];
            var (t1, c1) = stops[i + 1];
            if (t > t1) continue;
            var k = (t - t0) / (t1 - t0);
            return Color.FromRgb(
                (byte)(c0.R + (c1.R - c0.R) * k),
                (byte)(c0.G + (c1.G - c0.G) * k),
                (byte)(c0.B + (c1.B - c0.B) * k));
        }
        return stops[^1].C;
    }
}
