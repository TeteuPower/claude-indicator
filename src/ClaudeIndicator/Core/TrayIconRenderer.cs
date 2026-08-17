using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClaudeIndicator.Core;

/// <summary>Desenha o ícone da bandeja em tempo real com as barras de consumo.</summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private const int Size = 32;

    public static Color Ok => Color.FromArgb(255, 76, 195, 138);
    public static Color Warn => Color.FromArgb(255, 232, 176, 75);
    public static Color Alert => Color.FromArgb(255, 240, 92, 92);
    public static Color Muted => Color.FromArgb(255, 150, 145, 138);

    /// <summary>Trilha (parte vazia da barra). Clara o bastante para se ver quanto falta na barra escura.</summary>
    private static readonly Color TrackFill = Color.FromArgb(120, 236, 236, 238);

    /// <summary>Contorno escuro: define a extensão da barra também em barras de tarefas claras.</summary>
    private static readonly Color TrackEdge = Color.FromArgb(200, 18, 18, 22);

    public static Color ColorFor(double percent, AppSettings s)
    {
        if (percent >= s.AlertThreshold) return Alert;
        if (percent >= s.WarnThreshold) return Warn;
        return Ok;
    }

    public static Icon Render(UsageSnapshot? snap, AppSettings settings)
    {
        using var bmp = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            var bars = snap?.Visible(settings) ?? new List<UsageBar>();

            if (snap == null || bars.Count == 0)
            {
                DrawUnknown(g, snap?.Ok != false);
            }
            else if (bars.Count == 1)
            {
                DrawSingle(g, bars[0], settings);
            }
            else if (settings.TrayOrientation == BarOrientation.Horizontal)
            {
                DrawRows(g, bars, settings);
            }
            else
            {
                DrawColumns(g, bars, settings);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawUnknown(Graphics g, bool loading)
    {
        using var pen = new Pen(Muted, 3f);
        g.DrawEllipse(pen, 5, 5, Size - 11, Size - 11);
        if (!loading)
        {
            using var brush = new SolidBrush(Alert);
            using var font = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            DrawCentered(g, "!", font, brush);
        }
    }

    private static void DrawSingle(Graphics g, UsageBar bar, AppSettings s)
    {
        var color = ColorFor(bar.Percent, s);
        var pct = (int)Math.Round(bar.Percent);

        // trilha inferior
        var track = new RectangleF(2, Size - 8f, Size - 4, 6);
        DrawTrack(g, track, 3f);
        var inner = RectangleF.Inflate(track, -1.2f, -1.2f);
        var w = inner.Width * (float)bar.Fraction;
        if (w > 0.5f) FillRounded(g, new RectangleF(inner.X, inner.Y, Math.Max(2.5f, w), inner.Height), 1.8f, color);

        var text = pct >= 100 ? "99" : pct.ToString();
        using var font = new Font("Segoe UI", pct >= 100 ? 17f : 19f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        DrawCentered(g, text, font, brush, -3f);
    }

    private static void DrawColumns(Graphics g, List<UsageBar> bars, AppSettings s)
    {
        var n = Math.Min(bars.Count, 3);
        const float gap = 4f;
        var colWidth = (Size - gap * (n - 1)) / n;
        const float top = 2f;
        var height = Size - 4f;

        for (var i = 0; i < n; i++)
        {
            var x = i * (colWidth + gap);
            var trackRect = new RectangleF(x, top, colWidth, height);
            DrawTrack(g, trackRect, colWidth / 3f);

            // o preenchimento fica 1,2 px para dentro para o contorno escuro não sumir embaixo dele
            var inner = RectangleF.Inflate(trackRect, -1.2f, -1.2f);
            var fillH = inner.Height * (float)bars[i].Fraction;
            if (fillH < 2.5f && bars[i].Percent > 0) fillH = 2.5f;
            if (fillH <= 0) continue;

            var fillRect = new RectangleF(inner.X, inner.Bottom - fillH, inner.Width, fillH);
            FillRounded(g, fillRect, Math.Min(inner.Width / 3f, fillH / 2f), ColorFor(bars[i].Percent, s));
        }
    }

    /// <summary>Uma linha por barra, empilhadas de cima para baixo e preenchendo da esquerda para a direita.</summary>
    private static void DrawRows(Graphics g, List<UsageBar> bars, AppSettings s)
    {
        var n = Math.Min(bars.Count, 3);
        const float gap = 4f;
        var rowHeight = (Size - gap * (n - 1)) / n;
        const float left = 2f;
        var width = Size - 4f;

        for (var i = 0; i < n; i++)
        {
            var y = i * (rowHeight + gap);
            var trackRect = new RectangleF(left, y, width, rowHeight);
            DrawTrack(g, trackRect, rowHeight / 3f);

            var inner = RectangleF.Inflate(trackRect, -1.2f, -1.2f);
            var fillW = inner.Width * (float)bars[i].Fraction;
            if (fillW < 2.5f && bars[i].Percent > 0) fillW = 2.5f;
            if (fillW <= 0) continue;

            var fillRect = new RectangleF(inner.X, inner.Y, fillW, inner.Height);
            FillRounded(g, fillRect, Math.Min(inner.Height / 3f, fillW / 2f), ColorFor(bars[i].Percent, s));
        }
    }

    private static void DrawCentered(Graphics g, string text, Font font, Brush brush, float dy = 0f)
    {
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(text, font, brush, new RectangleF(0, dy, Size, Size), fmt);
    }

    /// <summary>Parte vazia da barra: preenchimento claro mais um contorno escuro de 1 px.</summary>
    private static void DrawTrack(Graphics g, RectangleF rect, float radius)
    {
        using var path = RoundedPath(rect, radius);
        using (var brush = new SolidBrush(TrackFill))
        {
            g.FillPath(brush, path);
        }
        using var pen = new Pen(TrackEdge, 1.2f);
        g.DrawPath(pen, path);
    }

    private static void FillRounded(Graphics g, RectangleF rect, float radius, Color color)
    {
        using var path = RoundedPath(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        radius = Math.Max(0.5f, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f));
        var path = new GraphicsPath();
        var d = radius * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Tooltip da bandeja. É onde o ritmo de consumo aparece no modo ícone: no desenho de 16 px
    /// não cabe, mas passar o mouse é gesto natural para "quero o detalhe".
    /// O limite do Windows é 127 caracteres.
    /// </summary>
    public static string Tooltip(UsageSnapshot? snap, AppSettings settings, RateReading? rate = null)
    {
        if (snap == null) return "Claude Indicator — carregando…";
        if (!snap.Ok && snap.Bars.Count == 0) return Truncate("Claude Indicator — " + snap.Error, 127);

        var parts = new List<string>();
        foreach (var kind in settings.EnabledKinds())
        {
            var bar = snap.Get(kind);
            if (bar == null) continue;
            parts.Add($"{settings.LabelFor(kind)} {Math.Round(bar.Percent)}%");
        }

        var text = parts.Count > 0 ? string.Join(" · ", parts) : "Claude Indicator";

        if (rate is { HasData: true })
        {
            text += $"\nRitmo: {ConsumptionRate.Format(rate)}";
            var left = ConsumptionRate.FormatTimeLeft(rate);
            if (left.Length > 0) text += " · " + left;
        }
        else
        {
            var first = snap.Visible(settings).FirstOrDefault();
            if (first?.ResetsAt != null) text += $"\n{first.ResetText()}";
        }

        return Truncate(text, 127);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
