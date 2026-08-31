using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeIndicator.Views;

/// <summary>
/// Os desenhos do layout "medidores" do indicador no jogo: anel de porcentagem e termômetro.
///
/// Ficam separados do BarRenderer e do GaugeRenderer porque são outra família — lá moram barras,
/// traçados e o velocímetro do consumo; aqui, formas de mostrador de hardware. O anel diz "quanto
/// do total"; o termômetro diz "quão perto do limite físico", que é uma pergunta diferente e por
/// isso ganha outra forma.
/// </summary>
public static class MeterRenderer
{
    /// <summary>
    /// Anel de porcentagem: um arco que cresce no sentido horário a partir do topo, com o valor
    /// no centro. A cor segue a régua de carga do app inteiro.
    ///
    /// O arco começa no topo porque é onde o olho espera o zero de um relógio; o fundo do anel
    /// fica sempre desenhado, para 15% não parecer um risco solto no nada.
    /// </summary>
    public static UIElement Ring(string rotulo, double percent, string valor, double diametro,
                                 bool contorno, string? apoio = null)
    {
        var p = Math.Clamp(percent, 0, 100);
        var cor = BarRenderer.LoadRamp(p);
        var espessura = Math.Max(3.5, diametro * 0.095);
        var raio = (diametro - espessura) / 2;
        var centro = diametro / 2;

        var caixa = new Grid { Width = diametro, Height = diametro };

        // contorno externo primeiro, para o anel não sumir sobre cena clara
        if (contorno)
        {
            caixa.Children.Add(new Ellipse
            {
                Width = diametro,
                Height = diametro,
                Stroke = Congelado(Color.FromArgb(0xB3, 0, 0, 0)),
                StrokeThickness = espessura + 2.5,
                Margin = new Thickness(-1.25)
            });
        }

        // trilho de fundo: o "total" de onde a porcentagem é fatia
        caixa.Children.Add(new Ellipse
        {
            Width = diametro,
            Height = diametro,
            Stroke = Congelado(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = espessura
        });

        // o arco em si
        if (p > 0.5)
        {
            caixa.Children.Add(new Path
            {
                Data = Arco(centro, raio, p),
                Stroke = Congelado(cor),
                StrokeThickness = espessura,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        var texto = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        texto.Children.Add(new OutlinedText
        {
            Text = valor,
            FontSize = diametro * 0.26,
            FontWeight = FontWeights.Bold,
            Foreground = Congelado(cor),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        if (apoio != null)
        {
            texto.Children.Add(new OutlinedText
            {
                Text = apoio,
                FontSize = diametro * 0.14,
                Foreground = BarRenderer.Swatch("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        caixa.Children.Add(texto);

        var pilha = new StackPanel { Margin = new Thickness(5, 0, 5, 0) };
        pilha.Children.Add(caixa);
        pilha.Children.Add(new OutlinedText
        {
            Text = rotulo,
            FontSize = 10.5,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return pilha;
    }

    /// <summary>
    /// Arco de 0..100% partindo do topo, sentido horário. Em 100% o arco fecharia sobre si mesmo
    /// e desapareceria — por isso o teto de 99.8%, que desenha um anel visivelmente cheio.
    /// </summary>
    private static Geometry Arco(double centro, double raio, double percent)
    {
        var fracao = Math.Min(percent, 99.8) / 100.0;
        var anguloFinal = fracao * 2 * Math.PI - Math.PI / 2;

        var inicio = new Point(centro, centro - raio);
        var fim = new Point(centro + raio * Math.Cos(anguloFinal),
                            centro + raio * Math.Sin(anguloFinal));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(inicio, false, false);
            ctx.ArcTo(fim, new Size(raio, raio), 0, fracao > 0.5, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>
    /// Termômetro vertical com o limite em 100 °C: tubo com bulbo, o mercúrio sobe e muda de cor
    /// com a temperatura.
    ///
    /// A régua daqui NÃO é a de carga. 50% de uso é metade do caminho e é amarelo; 50 °C é
    /// temperatura confortável e tem que ser verde. O amarelo entra aos 70 °C e o vermelho aos
    /// 90 °C, que é onde processadores de verdade começam a reduzir clock.
    /// </summary>
    public static UIElement Thermometer(string rotulo, double? tempC, double altura, bool contorno)
    {
        var largura = altura * 0.42;
        var tubo = altura * 0.16;
        var bulbo = tubo * 1.9;
        var util = altura - bulbo - 4;   // altura útil do mercúrio dentro do tubo

        var temp = tempC ?? 0;
        var fracao = Math.Clamp(temp / 100.0, 0, 1);
        var cor = tempC == null ? Color.FromArgb(255, 156, 151, 145) : TempRamp(temp);

        var caixa = new Grid { Width = largura, Height = altura };

        UIElement Tubo(double engorda, Color c, bool preenchido)
        {
            var g = new Grid { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
            g.Children.Add(new Border
            {
                Width = tubo + engorda,
                Height = altura - bulbo / 2 + engorda,
                CornerRadius = new CornerRadius((tubo + engorda) / 2),
                Background = preenchido ? Congelado(c) : null,
                BorderBrush = preenchido ? null : Congelado(c),
                BorderThickness = preenchido ? default : new Thickness(1.2)
            });
            return g;
        }

        // contorno escuro do conjunto, mesmo papel do contorno do texto
        if (contorno)
        {
            caixa.Children.Add(Tubo(4, Color.FromArgb(0xB3, 0, 0, 0), true));
            caixa.Children.Add(new Ellipse
            {
                Width = bulbo + 4,
                Height = bulbo + 4,
                Fill = Congelado(Color.FromArgb(0xB3, 0, 0, 0)),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, -2)
            });
        }

        // tubo vazio
        caixa.Children.Add(Tubo(0, Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), false));

        // mercúrio: sobe do bulbo até a fração da temperatura
        if (tempC != null && fracao > 0.02)
        {
            caixa.Children.Add(new Border
            {
                Width = tubo * 0.55,
                Height = Math.Max(2, util * fracao),
                CornerRadius = new CornerRadius(tubo * 0.275),
                Background = Congelado(cor),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, bulbo * 0.9)
            });
        }

        // bulbo, sempre na cor atual: é o "agora" do termômetro
        caixa.Children.Add(new Ellipse
        {
            Width = bulbo,
            Height = bulbo,
            Fill = Congelado(cor),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var pilha = new StackPanel { Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Bottom };
        pilha.Children.Add(caixa);
        pilha.Children.Add(new OutlinedText
        {
            Text = tempC != null ? $"{temp:0}°" : "—",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Congelado(cor),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });
        pilha.Children.Add(new OutlinedText
        {
            Text = rotulo,
            FontSize = 10.5,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        return pilha;
    }

    /// <summary>Verde até 70 °C, amarelo até 90, vermelho dali até o limite de 100.</summary>
    public static Color TempRamp(double tempC)
    {
        var verde = Color.FromArgb(255, 76, 195, 138);
        var amarelo = Color.FromArgb(255, 232, 176, 75);
        var vermelho = Color.FromArgb(255, 240, 92, 92);

        if (tempC <= 70) return verde;
        if (tempC <= 90) return Mix(amarelo, vermelho, Math.Max(0, (tempC - 80) / 10.0) * 0.35);
        return vermelho;
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(255,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Brush Congelado(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
