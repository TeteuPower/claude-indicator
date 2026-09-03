using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeIndicator.Views;

/// <summary>
/// Os desenhos do layout "medidores" do indicador no jogo.
///
/// Ficam separados do BarRenderer e do GaugeRenderer porque são outra família — lá moram barras,
/// traçados e o velocímetro do consumo; aqui, formas de mostrador de hardware.
///
/// A unidade daqui é o <see cref="SensorTile"/>: um componente inteiro num bloco só — o anel diz
/// "quanto do total está em uso" e o termômetro ao lado diz "quão perto do limite físico". São
/// duas perguntas diferentes, por isso duas formas diferentes; e ficam **juntas** porque a
/// pergunta que se faz no meio da partida é sobre um componente ("como está a GPU?"), não sobre
/// uma grandeza. Um termômetro solto entre dois anéis não tem dono: o olho não sabe se aquele
/// 67° é da CPU à esquerda ou da GPU à direita.
/// </summary>
public static class MeterRenderer
{
    /// <summary>Cinza dos valores sem leitura — mesmo tom do MutedBrush do app.</summary>
    private static readonly Color SemLeitura = Color.FromArgb(255, 156, 151, 145);

    /// <summary>
    /// Um componente inteiro: anel de uso, termômetro de temperatura e o rótulo embaixo.
    ///
    /// <paramref name="tempC"/> nulo tira o termômetro em vez de desenhar um vazio — sensor que
    /// não existe não ganha espaço reservado, que é ruído. É o caso da CPU sem elevação, onde a
    /// temperatura simplesmente não tem como ser lida.
    /// </summary>
    public static UIElement SensorTile(string rotulo, double? carga, string valorCarga,
                                       double? tempC, string? apoio, double diametro, bool contorno)
    {
        var conteudo = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        conteudo.Children.Add(Anel(carga, valorCarga, apoio, diametro, contorno));

        if (tempC != null)
            conteudo.Children.Add(Temperatura(tempC.Value, diametro, contorno));

        var pilha = new StackPanel();
        pilha.Children.Add(conteudo);
        pilha.Children.Add(new OutlinedText
        {
            Text = rotulo,
            FontSize = Math.Max(10.5, diametro * 0.19),
            FontWeight = FontWeights.SemiBold,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return pilha;
    }

    /// <summary>
    /// Anel de porcentagem: um arco que cresce no sentido horário a partir do topo, com o valor
    /// no centro. A cor segue a régua de carga do app inteiro.
    ///
    /// O arco começa no topo porque é onde o olho espera o zero de um relógio; o fundo do anel
    /// fica sempre desenhado, para 15% não parecer um risco solto no nada.
    /// </summary>
    private static UIElement Anel(double? carga, string valor, string? apoio, double diametro, bool contorno)
    {
        var p = Math.Clamp(carga ?? 0, 0, 100);
        var cor = carga == null ? SemLeitura : BarRenderer.LoadRamp(p);
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
        if (carga != null && p > 0.5)
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
            FontSize = diametro * 0.27,
            FontWeight = FontWeights.Bold,
            Foreground = Congelado(cor),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        if (apoio != null)
        {
            texto.Children.Add(new OutlinedText
            {
                Text = apoio,
                FontSize = Math.Max(9, diametro * 0.155),
                Foreground = BarRenderer.Swatch("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        caixa.Children.Add(texto);

        return caixa;
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
    /// Termômetro com o número grande ao lado, na mesma altura do anel do componente.
    ///
    /// O número é o que se lê de canto de olho — por isso ele é o maior texto do bloco e sai na
    /// cor da faixa térmica. O tubo fica ao lado dando a posição: o mesmo "76°" significa coisas
    /// diferentes dependendo de quanto falta para o teto, e a altura do mercúrio responde isso
    /// sem precisar ler número nenhum.
    /// </summary>
    private static UIElement Temperatura(double tempC, double altura, bool contorno)
    {
        var cor = TempRamp(tempC);

        var linha = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0)
        };
        linha.Children.Add(Tubo(tempC, altura, cor, contorno));
        linha.Children.Add(new OutlinedText
        {
            Text = $"{tempC:0}°",
            FontSize = altura * 0.32,
            FontWeight = FontWeights.Bold,
            Foreground = Congelado(cor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0)
        });
        return linha;
    }

    /// <summary>
    /// O tubo com bulbo: o mercúrio sobe até a fração de 100 °C e muda de cor com a temperatura.
    ///
    /// A régua daqui NÃO é a de carga. 50% de uso é metade do caminho e é amarelo; 50 °C é
    /// temperatura confortável e tem que ser verde. O amarelo entra aos 70 °C e o vermelho aos
    /// 90 °C, que é onde processadores de verdade começam a reduzir clock.
    /// </summary>
    private static UIElement Tubo(double tempC, double altura, Color cor, bool contorno)
    {
        var tubo = altura * 0.17;
        var bulbo = tubo * 1.85;
        var largura = bulbo + 5;         // sobra para o contorno escuro do bulbo
        var util = altura - bulbo - 4;   // altura útil do mercúrio dentro do tubo
        var fracao = Math.Clamp(tempC / 100.0, 0, 1);

        var caixa = new Grid { Width = largura, Height = altura };

        UIElement Corpo(double engorda, Color c, bool preenchido)
        {
            var g = new Grid
            {
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center
            };
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
            caixa.Children.Add(Corpo(4, Color.FromArgb(0xB3, 0, 0, 0), true));
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
        caixa.Children.Add(Corpo(0, Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), false));

        // mercúrio: sobe do bulbo até a fração da temperatura
        if (fracao > 0.02)
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

        return caixa;
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
