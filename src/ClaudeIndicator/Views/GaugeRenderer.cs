using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Velocímetro do ritmo de consumo: um arco de 180° onde o meio da escala é o ritmo que o limite
/// aguenta até a renovação. Ponteiro à esquerda do meio = dá para seguir assim; à direita = o
/// limite acaba antes de renovar.
/// </summary>
public static class GaugeRenderer
{
    /// <summary>Fim da escala em múltiplos do ritmo sustentável.</summary>
    private const double FullScale = 2.0;

    public static Color ColorFor(RateReading r)
    {
        if (!r.HasData || r.Sustainable <= 0) return Color.FromArgb(255, 156, 151, 145);
        var ratio = r.Ratio;
        if (ratio <= 1.0) return Color.FromArgb(255, 76, 195, 138);
        if (ratio <= 1.4) return Color.FromArgb(255, 232, 176, 75);
        return Color.FromArgb(255, 240, 92, 92);
    }

    /// <summary>Desenha o arco com o ponteiro. A largura é sempre o dobro da altura útil.</summary>
    public static UIElement Build(RateReading r, double size)
    {
        var w = size;
        var h = size / 2 + 3;
        var canvas = new Canvas { Width = w, Height = h, SnapsToDevicePixels = true };

        var cx = w / 2;
        var cy = h - 2;
        var radius = w / 2 - 3;
        var thickness = Math.Max(2.5, size / 11);

        // trilho
        canvas.Children.Add(Arc(cx, cy, radius, 0, 1, thickness,
            new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))));

        // marca do ritmo sustentável, no meio da escala
        if (r.HasData && r.Sustainable > 0)
        {
            var tick = new Line
            {
                X1 = cx, Y1 = cy - radius - thickness / 2 - 1,
                X2 = cx, Y2 = cy - radius + thickness / 2 + 1,
                Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                StrokeThickness = 1.2
            };
            canvas.Children.Add(tick);
        }

        if (r.HasData)
        {
            var frac = r.Sustainable > 0
                ? Math.Clamp(r.Ratio / FullScale, 0, 1)
                : (r.PerMinute > 0 ? 0.5 : 0);

            var color = ColorFor(r);
            if (frac > 0.001)
                canvas.Children.Add(Arc(cx, cy, radius, 0, frac, thickness, new SolidColorBrush(color)));

            // ponteiro
            var angle = Math.PI * frac;
            var nx = cx - Math.Cos(angle) * (radius - thickness / 2);
            var ny = cy - Math.Sin(angle) * (radius - thickness / 2);
            canvas.Children.Add(new Line
            {
                X1 = cx, Y1 = cy, X2 = nx, Y2 = ny,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = Math.Max(1.5, size / 22),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            var hub = new Ellipse
            {
                Width = thickness, Height = thickness,
                Fill = new SolidColorBrush(color)
            };
            Canvas.SetLeft(hub, cx - thickness / 2);
            Canvas.SetTop(hub, cy - thickness / 2);
            canvas.Children.Add(hub);
        }

        return canvas;
    }

    /// <summary>Trecho do arco superior, de <paramref name="from"/> a <paramref name="to"/> (0..1).</summary>
    private static Path Arc(double cx, double cy, double radius, double from, double to,
        double thickness, Brush brush)
    {
        var a1 = Math.PI * Math.Clamp(from, 0, 1);
        var a2 = Math.PI * Math.Clamp(to, 0, 1);

        var p1 = new Point(cx - Math.Cos(a1) * radius, cy - Math.Sin(a1) * radius);
        var p2 = new Point(cx - Math.Cos(a2) * radius, cy - Math.Sin(a2) * radius);

        var figure = new PathFigure { StartPoint = p1, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = p2,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = a2 - a1 > Math.PI
        });

        var geo = new PathGeometry();
        geo.Figures.Add(figure);

        return new Path
        {
            Data = geo,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    /// <summary>Texto de apoio do velocímetro, para tooltip.</summary>
    public static string Describe(RateReading r, AppSettings s, BarKind kind)
    {
        if (!r.HasData) return "Ritmo: ainda sem medição suficiente.";

        var text = $"{s.LabelFor(kind)}: {ConsumptionRate.Format(r)}"
                   + $"\nMédia dos últimos {ConsumptionRate.DescribeWindow(r.WindowMinutes)}";
        var left = ConsumptionRate.FormatTimeLeft(r);
        if (left.Length > 0) text += "\n" + left + " no ritmo atual";

        if (r.Sustainable > 0)
        {
            text += r.Ratio <= 1
                ? $"\nDentro do que o limite aguenta (até {r.Sustainable:0.###}% p/min)."
                : $"\nAcima do que o limite aguenta ({r.Sustainable:0.###}% p/min): vai acabar antes de renovar.";
        }
        return text;
    }
}
