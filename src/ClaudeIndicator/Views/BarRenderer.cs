using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Collections.Generic;
using System.Windows.Media;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>Monta as barras de consumo usadas no gadget e na prévia das configurações.</summary>
public static class BarRenderer
{
    public static Brush Swatch(string key)
    {
        var res = System.Windows.Application.Current?.TryFindResource(key);
        return res as Brush ?? Brushes.Gray;
    }

    /// <summary>
    /// Fatia (0..1) como porcentagem legível em qualquer ordem de grandeza: 20,3% e 0,004% têm
    /// que caber na mesma coluna sem virar "0%" nem "20,300%".
    /// </summary>
    public static string FormatShare(double share)
    {
        var pct = share * 100.0;
        if (pct <= 0) return "0%";
        if (pct < 0.001) return "<0,001%";
        if (pct < 0.1) return pct.ToString("0.###") + "%";
        if (pct < 1) return pct.ToString("0.##") + "%";
        if (pct < 10) return pct.ToString("0.#") + "%";
        return pct.ToString("0") + "%";
    }

    /// <summary>Consumo medido em pontos do limite (a barra subiu tanto), com sinal.</summary>
    public static string FormatLimitDelta(double? points)
    {
        if (points == null) return "—";
        var v = points.Value;
        if (v <= 0) return "0%";
        return "+" + (v < 0.1 ? v.ToString("0.##") : v < 10 ? v.ToString("0.#") : v.ToString("0")) + "%";
    }

    /// <summary>
    /// Fio fino do tempo decorrido na janela do limite. Fica sob a barra de consumo: se o fio
    /// está à frente, você gasta mais devagar que o relógio; atrás, mais rápido.
    /// </summary>
    /// <summary>
    /// Marca vertical no trilho, no ponto onde o tempo da janela já chegou.
    ///
    /// Antes isto era um segundo fio, mais fino, logo abaixo da barra — e ninguém achava onde ele
    /// estava: cinza sobre cinza, com 2 px de altura, competindo com a barra de verdade. Marcar
    /// DENTRO do trilho resolve as duas coisas de uma vez: fica visível, e a comparação que
    /// interessa vira imediata — preenchimento antes da marca é consumo abaixo do relógio, depois
    /// dela é consumo adiantado.
    ///
    /// A marca é clara com as bordas escuras, então se destaca tanto sobre o trilho escuro quanto
    /// sobre um preenchimento colorido, e transborda a altura do trilho para ninguém confundi-la
    /// com um pedaço do preenchimento.
    /// </summary>
    /// <summary>
    /// As formas do ícone: sol (disco e raios) ou lua (crescente). <paramref name="engrossar"/>
    /// maior que zero devolve a versão de contorno, que vai por baixo.
    /// </summary>
    public static List<UIElement> ThemeIcon(bool claro, double lado, Brush cor, double engrossar)
    {
        var partes = new List<UIElement>();
        var c = lado / 2;

        if (claro)
        {
            // lua crescente: um disco menos outro deslocado
            var cheia = new EllipseGeometry(new Point(c, c), lado * 0.33, lado * 0.33);
            var mordida = new EllipseGeometry(new Point(c + lado * 0.20, c - lado * 0.16),
                                              lado * 0.29, lado * 0.29);
            partes.Add(new Path
            {
                Data = new CombinedGeometry(GeometryCombineMode.Exclude, cheia, mordida),
                Fill = cor,
                Stroke = engrossar > 0 ? cor : null,
                StrokeThickness = engrossar,
                StrokeLineJoin = PenLineJoin.Round
            });
            return partes;
        }

        // sol: disco e oito raios
        partes.Add(new Path
        {
            Data = new EllipseGeometry(new Point(c, c), lado * 0.21, lado * 0.21),
            Fill = cor,
            Stroke = engrossar > 0 ? cor : null,
            StrokeThickness = engrossar,
            StrokeLineJoin = PenLineJoin.Round
        });

        var raios = new GeometryGroup();
        for (var i = 0; i < 8; i++)
        {
            var ang = i * Math.PI / 4;
            var de = lado * 0.31;
            var ate = lado * 0.45;
            raios.Children.Add(new LineGeometry(
                new Point(c + Math.Cos(ang) * de, c + Math.Sin(ang) * de),
                new Point(c + Math.Cos(ang) * ate, c + Math.Sin(ang) * ate)));
        }
        partes.Add(new Path
        {
            Data = raios,
            Stroke = cor,
            // o reforço engrossa o raio pelos dois lados, então metade de cada lado é o que
            // aparece como contorno — mais que isso e os oito raios viram um borrão só
            StrokeThickness = (1.5 + engrossar) * (lado / 17),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        return partes;
    }

    public static UIElement BuildTimeMarker(double fraction, double trackHeight)
    {
        var grade = new Grid { IsHitTestVisible = false };

        var f = Math.Clamp(fraction, 0, 1);
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(f, 0.0001), GridUnitType.Star) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - f, 0.0001), GridUnitType.Star) });

        var marca = new Border
        {
            Width = 4,
            Height = trackHeight + 6,
            Background = MarcaClara,
            BorderBrush = MarcaEscura,
            BorderThickness = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            // meia largura para a esquerda: a marca fica centrada no ponto, e não começando nele
            Margin = new Thickness(-2, 0, 0, 0)
        };
        Grid.SetColumn(marca, 1);
        grade.Children.Add(marca);
        return grade;
    }

    /// <summary>
    /// A cor de uma carga de 0 a 100: verde no começo, amarela na metade, vermelha no fim.
    /// É a mesma régua das barras do painel — se o mesmo 70% aparecesse laranja num lugar e
    /// amarelo no outro, a cor deixaria de ser informação e viraria decoração.
    /// </summary>
    public static Color LoadRamp(double percent)
    {
        var f = Math.Clamp(percent / 100.0, 0, 1);
        return f <= 0.5
            ? Misturar(RampaVerde, RampaAmarelo, f / 0.5)
            : Misturar(RampaAmarelo, RampaVermelho, (f - 0.5) / 0.5);
    }

    private static readonly Color RampaVerde = Color.FromArgb(255, 76, 195, 138);
    private static readonly Color RampaAmarelo = Color.FromArgb(255, 232, 176, 75);
    private static readonly Color RampaVermelho = Color.FromArgb(255, 240, 92, 92);

    private static Color Misturar(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(255,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>
    /// Traçado das últimas leituras: linha com área preenchida por baixo, na escala fixa de 0 a
    /// 100%.
    ///
    /// Escala fixa, e não ajustada ao maior valor da amostra: com escala automática uma variação
    /// de 3% ocuparia a altura toda e pareceria drama. O que se quer saber é se está perto do
    /// teto, então o teto tem que ser sempre o mesmo.
    ///
    /// Com contorno, uma cópia escura e mais grossa vai por baixo — sobre cena de jogo clara a
    /// linha sozinha desaparece.
    /// </summary>
    public static UIElement Sparkline(double[] valores, double largura, double altura,
                                      Color cor, bool contorno)
    {
        var caixa = new Grid { Width = largura, Height = altura, ClipToBounds = true };
        if (valores.Length < 2) return caixa;

        var pontos = new PointCollection(valores.Length);
        for (var i = 0; i < valores.Length; i++)
        {
            var x = largura * i / (valores.Length - 1);
            var y = altura - altura * Math.Clamp(valores[i] / 100.0, 0, 1);
            pontos.Add(new Point(x, y));
        }

        // área por baixo: fecha a linha nos dois cantos de baixo
        var area = new PointCollection(pontos) { new(largura, altura), new(0, altura) };
        caixa.Children.Add(new Polygon
        {
            Points = area,
            Fill = Freeze(Color.FromArgb(0x38, cor.R, cor.G, cor.B))
        });

        if (contorno)
        {
            caixa.Children.Add(new Polyline
            {
                Points = pontos,
                Stroke = Freeze(Color.FromArgb(0xCC, 0, 0, 0)),
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round
            });
        }

        caixa.Children.Add(new Polyline
        {
            Points = pontos,
            Stroke = Freeze(cor),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round
        });

        return caixa;
    }

    private static readonly Brush MarcaClara = Freeze(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MarcaEscura = Freeze(Color.FromArgb(0xCC, 0x10, 0x10, 0x10));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Trilho com a marca do tempo por cima, quando há tempo a marcar. O trilho sozinho quando não
    /// há — limite sem horário de renovação informado pela API.
    /// </summary>
    public static UIElement TrackWithMarker(UIElement track, double? timeFraction, double trackHeight)
    {
        if (timeFraction == null) return track;

        // A marca é mais alta que o trilho, então o Grid fica mais alto que ele. Sem centrar, o
        // trilho encostaria no topo e a marca pareceria pendurada embaixo, e não atravessando.
        if (track is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;

        var pilha = new Grid();
        pilha.Children.Add(track);
        pilha.Children.Add(BuildTimeMarker(timeFraction.Value, trackHeight));
        return pilha;
    }

    public static UIElement BuildTimeLine(double fraction, double width, double height, Thickness margin)
    {
        var track = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = Swatch("TrackBrush"),
            Margin = margin,
            ClipToBounds = true
        };
        // largura NaN significa "ocupe o espaço disponível" (usado no gadget, que é elástico)
        if (!double.IsNaN(width)) track.Width = width;

        var grid = new Grid();
        var f = Math.Clamp(fraction, 0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(f, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - f, 0.0001), GridUnitType.Star) });

        var fill = new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            Background = Swatch("MutedBrush"),
            Opacity = 0.85,
            MinWidth = f > 0 ? 2 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;
        return track;
    }

    public static Brush BrushFor(double percent, AppSettings s)
    {
        if (percent >= s.AlertThreshold) return Swatch("DangerBrush");
        if (percent >= s.WarnThreshold) return Swatch("WarnBrush");
        return Swatch("OkBrush");
    }

    public static UIElement BuildRow(UsageBar bar, AppSettings s, bool showReset, double labelSize = 11)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = s.LabelFor(bar.Kind);
        if (showReset && bar.ResetsAt != null)
            labelText += "  ·  " + bar.ResetText();

        var label = new TextBlock
        {
            Text = labelText,
            FontSize = labelSize,
            Foreground = Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);
        header.Children.Add(label);

        var value = new TextBlock
        {
            Text = Math.Round(bar.Percent).ToString("0") + "%",
            FontSize = labelSize + 1,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFor(bar.Percent, s),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 1);
        header.Children.Add(value);

        container.Children.Add(header);

        var track = new Border
        {
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = Swatch("TrackBrush"),
            Margin = new Thickness(0, 5, 0, 0),
            ClipToBounds = true
        };

        var grid = new Grid();
        var frac = Math.Max(0.0, Math.Min(1.0, bar.Fraction));
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });

        var fill = new Border
        {
            CornerRadius = new CornerRadius(3.5),
            Background = BrushFor(bar.Percent, s),
            MinWidth = bar.Percent > 0 ? 4 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;

        var timeFrac = s.ShowTimeProgress ? bar.TimeFraction() : null;
        container.Children.Add(TrackWithMarker(track, timeFrac, 7));

        return container;
    }

    /// <summary>
    /// Célula do gadget horizontal: rótulo em cima, porcentagem + barra na linha de baixo,
    /// horário de renovação embaixo. As células ficam lado a lado, com um separador entre elas.
    /// </summary>
    public static UIElement BuildCell(UsageBar bar, AppSettings s, bool showReset)
    {
        var cell = new StackPanel { Width = 118 };

        cell.Children.Add(new TextBlock
        {
            Text = s.LabelFor(bar.Kind),
            FontSize = 10.5,
            Foreground = Swatch("MutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var value = new TextBlock
        {
            Text = Math.Round(bar.Percent).ToString("0") + "%",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFor(bar.Percent, s),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 0);
        row.Children.Add(value);

        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Swatch("TrackBrush"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        var grid = new Grid();
        var frac = Math.Max(0.0, Math.Min(1.0, bar.Fraction));
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = BrushFor(bar.Percent, s),
            MinWidth = bar.Percent > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;
        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        cell.Children.Add(row);

        if (showReset && bar.ResetsAt != null)
        {
            cell.Children.Add(new TextBlock
            {
                Text = bar.ResetText(),
                FontSize = 9.5,
                Foreground = Swatch("MutedBrush"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        return cell;
    }

    /// <summary>Separador vertical entre as células do gadget horizontal.</summary>
    public static UIElement BuildCellSeparator()
    {
        return new Border
        {
            Width = 1,
            Background = Swatch("LineBrush"),
            Margin = new Thickness(12, 2, 12, 2),
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }
}
