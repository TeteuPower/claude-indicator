using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Gráficos do consumo registrado em <see cref="UsageHistory"/>: o nível da barra ao longo
/// do tempo (linha) e quanto foi consumido por hora ou por dia (colunas), sempre de uma
/// barra por vez — a identidade vem do seletor, não de cores.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly AppHost _host;
    private List<HistoryPoint> _all = new();
    private bool _ready;

    // pontos desenhados na linha, para o hover achar o mais próximo (x em pixels)
    private readonly List<(double X, double Y, DateTimeOffset At, double Value)> _linePts = new();
    private Ellipse? _hoverDot;

    /// <summary>Um buraco maior que isso entre dois pontos significa app fechado: a linha
    /// quebra e a diferença de consumo não é somada em nenhum balde.</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(90);

    public HistoryWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        SerSession.Content = host.Settings.SessionLabel;
        SerWeekly.Content = host.Settings.WeeklyLabel;
        SerFable.Content = host.Settings.FableLabel;
        SerWeekly.IsChecked = true;
        Rng24h.IsChecked = true;

        _ready = true;
        ReloadData();
        _host.Updated += OnUsageUpdated;
    }

    // ------------------------------------------------------------------
    // Estado dos filtros
    // ------------------------------------------------------------------

    private BarKind Kind =>
        SerSession.IsChecked == true ? BarKind.Session :
        SerFable.IsChecked == true ? BarKind.Fable : BarKind.Weekly;

    private TimeSpan Range =>
        Rng7d.IsChecked == true ? TimeSpan.FromDays(7) :
        Rng30d.IsChecked == true ? TimeSpan.FromDays(30) : TimeSpan.FromHours(24);

    private bool HourlyBuckets => Rng24h.IsChecked == true;

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) Redraw();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready) Redraw();
    }

    private void OnUsageUpdated(UsageSnapshot? _)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ReloadData);
            return;
        }
        ReloadData();
    }

    private void OnClosed(object sender, EventArgs e) => _host.Updated -= OnUsageUpdated;

    private void ReloadData()
    {
        _all = UsageHistory.Load(TimeSpan.FromDays(31));
        Redraw();
    }

    // ------------------------------------------------------------------
    // Redesenho
    // ------------------------------------------------------------------

    private void Redraw()
    {
        var kindLabel = _host.Settings.LabelFor(Kind);
        LineTitle.Text = $"Nível — {kindLabel} (%)";
        BarsTitle.Text = HourlyBuckets
            ? $"Consumo por hora — {kindLabel} (pts do limite)"
            : $"Consumo por dia — {kindLabel} (pts do limite)";
        StatRangeCaption.Text = Rng30d.IsChecked == true ? "últimos 30 dias"
            : Rng7d.IsChecked == true ? "últimos 7 dias" : "últimas 24 horas";

        StatHour.Text = FormatPts(SumConsumption(TimeSpan.FromHours(1)));
        StatDay.Text = FormatPts(SumConsumption(TimeSpan.FromHours(24)));
        StatRange.Text = FormatPts(SumConsumption(Range));

        DrawLineChart();
        DrawBarChart();
    }

    private static string FormatPts(double? v) =>
        v == null ? "—" : "+" + v.Value.ToString("0.#") + " pts";

    /// <summary>Soma das subidas da barra na janela (as quedas são renovações, não consumo).</summary>
    private double? SumConsumption(TimeSpan window)
    {
        var cutoff = DateTimeOffset.Now - window;
        double sum = 0;
        var any = false;
        HistoryPoint? prev = null;

        foreach (var p in _all)
        {
            if (p.Get(Kind) == null) continue;
            if (prev != null && p.At >= cutoff)
            {
                any = true;
                if (p.At - prev.At <= MaxGap)
                {
                    var d = p.Get(Kind)!.Value - prev.Get(Kind)!.Value;
                    if (d > 0) sum += d;
                }
            }
            prev = p;
        }
        return any ? Math.Round(sum, 1) : null;
    }

    // ------------------------------------------------------------------
    // Gráfico de linha (nível %)
    // ------------------------------------------------------------------

    private const double MarginLeft = 38;
    private const double MarginRight = 10;
    private const double MarginTop = 8;
    private const double MarginBottom = 22;

    private static Brush Muted => BarRenderer.Swatch("MutedBrush");
    private static Brush Grid => BarRenderer.Swatch("LineBrush");
    private static Brush Accent => BarRenderer.Swatch("AccentBrush");

    private void DrawLineChart()
    {
        var c = LineCanvas;
        c.Children.Clear();
        _linePts.Clear();
        _hoverDot = null;
        LineHover.Text = "";

        var w = c.ActualWidth;
        var h = c.ActualHeight;
        if (w < 60 || h < 40) return;

        var now = DateTimeOffset.Now;
        var from = now - Range;
        var plotW = w - MarginLeft - MarginRight;
        var plotH = h - MarginTop - MarginBottom;

        double X(DateTimeOffset t) => MarginLeft + plotW * (t - from).TotalSeconds / Range.TotalSeconds;
        double Y(double pct) => MarginTop + plotH * (1 - pct / 100.0);

        // grade horizontal fixa 0..100 (o eixo é estável: é % do limite)
        for (var pct = 0; pct <= 100; pct += 25)
        {
            AddLine(c, MarginLeft, Y(pct), w - MarginRight, Y(pct), Grid, 1);
            AddText(c, pct + "%", 2, Y(pct) - 7, 10, Muted);
        }

        // referências de atenção/alerta, discretas e tracejadas
        AddThreshold(c, Y(_host.Settings.WarnThreshold), w, BarRenderer.Swatch("WarnBrush"));
        AddThreshold(c, Y(_host.Settings.AlertThreshold), w, BarRenderer.Swatch("DangerBrush"));

        // marcações de tempo
        foreach (var (t, label) in TimeTicks(from, now))
        {
            var x = X(t);
            AddLine(c, x, MarginTop, x, h - MarginBottom, Grid, 0.6);
            AddText(c, label, x - 16, h - MarginBottom + 4, 10, Muted);
        }

        var pts = new List<HistoryPoint>();
        foreach (var p in _all)
        {
            if (p.At >= from && p.Get(Kind) != null) pts.Add(p);
        }

        if (pts.Count < 2)
        {
            AddText(c, "Ainda não há histórico suficiente neste período.\nEle é gravado enquanto o app está em execução.",
                MarginLeft + 12, h / 2 - 16, 12, Muted);
            return;
        }

        // uma polyline por trecho contínuo; buracos (app fechado) quebram a linha
        Polyline? current = null;
        HistoryPoint? prev = null;
        foreach (var p in pts)
        {
            var v = p.Get(Kind)!.Value;
            var x = X(p.At);
            var y = Y(v);
            _linePts.Add((x, y, p.At, v));

            if (current == null || (prev != null && p.At - prev.At > MaxGap))
            {
                current = new Polyline
                {
                    Stroke = Accent,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round
                };
                c.Children.Add(current);
            }
            current.Points.Add(new System.Windows.Point(x, y));
            prev = p;
        }

        // marcador de hover (invisível até o mouse entrar)
        _hoverDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = Accent,
            Stroke = BarRenderer.Swatch("BgBrush"),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed
        };
        c.Children.Add(_hoverDot);
    }

    private void AddThreshold(Canvas c, double y, double w, Brush brush)
    {
        var ln = new Line
        {
            X1 = MarginLeft, Y1 = y, X2 = w - MarginRight, Y2 = y,
            Stroke = brush, StrokeThickness = 1, Opacity = 0.35,
            StrokeDashArray = new DoubleCollection { 4, 4 }
        };
        c.Children.Add(ln);
    }

    private IEnumerable<(DateTimeOffset T, string Label)> TimeTicks(DateTimeOffset from, DateTimeOffset to)
    {
        if (HourlyBuckets)
        {
            // a cada 4 horas, em horas cheias
            var t = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, from.Offset).AddHours(1);
            while (t < to)
            {
                if (t.Hour % 4 == 0) yield return (t, t.ToString("HH'h'"));
                t = t.AddHours(1);
            }
        }
        else
        {
            var step = Rng30d.IsChecked == true ? 5 : 1;
            var t = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, from.Offset).AddDays(1);
            var i = 0;
            while (t < to)
            {
                if (i % step == 0) yield return (t, t.ToString("dd/MM"));
                t = t.AddDays(1);
                i++;
            }
        }
    }

    private void OnLineMouseMove(object sender, MouseEventArgs e)
    {
        if (_hoverDot == null || _linePts.Count == 0) return;
        var pos = e.GetPosition(LineCanvas);

        var best = -1;
        var bestDist = double.MaxValue;
        for (var i = 0; i < _linePts.Count; i++)
        {
            var d = Math.Abs(_linePts[i].X - pos.X);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        if (best < 0) return;

        var (x, y, at, v) = _linePts[best];
        Canvas.SetLeft(_hoverDot, x - _hoverDot.Width / 2);
        Canvas.SetTop(_hoverDot, y - _hoverDot.Height / 2);
        _hoverDot.Visibility = Visibility.Visible;
        LineHover.Text = $"{at.ToLocalTime():dd/MM HH:mm} · {v:0.#}%";
    }

    private void OnLineMouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverDot != null) _hoverDot.Visibility = Visibility.Collapsed;
        LineHover.Text = "";
    }

    // ------------------------------------------------------------------
    // Gráfico de colunas (consumo por balde)
    // ------------------------------------------------------------------

    private void DrawBarChart()
    {
        var c = BarsCanvas;
        c.Children.Clear();

        var w = c.ActualWidth;
        var h = c.ActualHeight;
        if (w < 60 || h < 40) return;

        var now = DateTimeOffset.Now;
        var bucket = HourlyBuckets ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var count = HourlyBuckets ? 24 : (Rng30d.IsChecked == true ? 30 : 7);

        // início alinhado à hora/dia cheio
        var end = HourlyBuckets
            ? new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).Add(bucket)
            : new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).Add(bucket);
        var start = end - TimeSpan.FromTicks(bucket.Ticks * count);

        var sums = new double[count];
        var has = new bool[count];
        HistoryPoint? prev = null;
        foreach (var p in _all)
        {
            if (p.Get(Kind) == null) continue;
            if (prev != null && p.At > start && p.At - prev.At <= MaxGap)
            {
                var idx = (int)((p.At - start).Ticks / bucket.Ticks);
                if (idx >= 0 && idx < count)
                {
                    has[idx] = true;
                    var d = p.Get(Kind)!.Value - prev.Get(Kind)!.Value;
                    if (d > 0) sums[idx] += d;
                }
            }
            prev = p;
        }

        var max = 0.0;
        var maxIdx = -1;
        var anyData = false;
        for (var i = 0; i < count; i++)
        {
            anyData |= has[i];
            if (sums[i] > max)
            {
                max = sums[i];
                maxIdx = i;
            }
        }

        if (!anyData)
        {
            AddText(c, "Sem dados neste período ainda.", MarginLeft + 12, h / 2 - 8, 12, Muted);
            return;
        }

        var niceMax = NiceMax(max);
        var plotW = w - MarginLeft - MarginRight;
        var plotH = h - MarginTop - MarginBottom;
        double Y(double v) => MarginTop + plotH * (1 - v / niceMax);

        // grade: base, meio e topo
        foreach (var v in new[] { 0.0, niceMax / 2, niceMax })
        {
            AddLine(c, MarginLeft, Y(v), w - MarginRight, Y(v), Grid, 1);
            AddText(c, v.ToString("0.#"), 2, Y(v) - 7, 10, Muted);
        }

        var slot = plotW / count;
        var barW = Math.Max(3.0, slot - 2); // 2 px de respiro entre colunas

        for (var i = 0; i < count; i++)
        {
            var bucketStart = start + TimeSpan.FromTicks(bucket.Ticks * i);
            var x = MarginLeft + slot * i + (slot - barW) / 2;

            if (sums[i] > 0)
            {
                var y = Y(sums[i]);
                var rect = new Rectangle
                {
                    Width = barW,
                    Height = Math.Max(2, MarginTop + plotH - y),
                    Fill = Accent,
                    RadiusX = Math.Min(3, barW / 2),
                    RadiusY = Math.Min(3, barW / 2),
                    ToolTip = HourlyBuckets
                        ? $"{bucketStart.ToLocalTime():HH'h'}–{(bucketStart + bucket).ToLocalTime():HH'h'} · +{sums[i]:0.#} pts"
                        : $"{bucketStart.ToLocalTime():dd/MM} · +{sums[i]:0.#} pts"
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                c.Children.Add(rect);

                // rótulo direto apenas no maior valor; o resto fica no tooltip
                if (i == maxIdx)
                    AddText(c, "+" + sums[i].ToString("0.#"), x + barW / 2 - 12, y - 15, 10.5, BarRenderer.Swatch("TextBrush"));
            }

            // rótulos de tempo
            var every = HourlyBuckets ? 4 : (Rng30d.IsChecked == true ? 5 : 1);
            if (i % every == 0)
            {
                var label = HourlyBuckets ? bucketStart.ToLocalTime().ToString("HH'h'") : bucketStart.ToLocalTime().ToString("dd/MM");
                AddText(c, label, x - 2, h - MarginBottom + 4, 10, Muted);
            }
        }
    }

    private static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        var steps = new[] { 1.0, 2, 2.5, 5, 10, 15, 20, 25, 40, 50, 75, 100 };
        foreach (var s in steps)
        {
            if (v <= s) return s;
        }
        return Math.Ceiling(v / 50) * 50;
    }

    // ------------------------------------------------------------------

    private static void AddLine(Canvas c, double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        c.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness });
    }

    private static void AddText(Canvas c, string text, double x, double y, double size, Brush brush)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = brush };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        c.Children.Add(tb);
    }
}
