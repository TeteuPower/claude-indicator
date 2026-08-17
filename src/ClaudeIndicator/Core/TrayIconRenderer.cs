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
        var trackTop = Size - 7f;
        FillRounded(g, new RectangleF(2, trackTop, Size - 4, 5), 2.5f, Color.FromArgb(70, 255, 255, 255));
        var w = (Size - 4) * (float)bar.Fraction;
        if (w > 0.5f) FillRounded(g, new RectangleF(2, trackTop, Math.Max(3f, w), 5), 2.5f, color);

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
            FillRounded(g, trackRect, colWidth / 2.5f, Color.FromArgb(70, 255, 255, 255));

            var fillH = height * (float)bars[i].Fraction;
            if (fillH < 2.5f && bars[i].Percent > 0) fillH = 2.5f;
            if (fillH <= 0) continue;

            var fillRect = new RectangleF(x, top + (height - fillH), colWidth, fillH);
            FillRounded(g, fillRect, Math.Min(colWidth / 2.5f, fillH / 2f), ColorFor(bars[i].Percent, s));
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

    private static void FillRounded(Graphics g, RectangleF rect, float radius, Color color)
    {
        radius = Math.Max(0.5f, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f));
        using var path = new GraphicsPath();
        var d = radius * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Tooltip da bandeja (máx. 63 caracteres por segurança).</summary>
    public static string Tooltip(UsageSnapshot? snap, AppSettings settings)
    {
        if (snap == null) return "Claude Indicator — carregando…";
        if (!snap.Ok && snap.Bars.Count == 0) return Truncate("Claude Indicator — " + snap.Error, 63);

        var parts = new List<string>();
        foreach (var kind in settings.EnabledKinds())
        {
            var bar = snap.Get(kind);
            if (bar == null) continue;
            parts.Add($"{settings.LabelFor(kind)} {Math.Round(bar.Percent)}%");
        }

        var text = parts.Count > 0 ? string.Join(" · ", parts) : "Claude Indicator";
        var first = snap.Visible(settings).FirstOrDefault();
        if (first?.ResetsAt != null) text += $"\n{first.ResetText()}";
        return Truncate(text, 63);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
