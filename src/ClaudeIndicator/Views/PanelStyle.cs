using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// O estilo dos painéis, num lugar só: as células do painel da barra de tarefas e as colunas da
/// barra própria, no mesmo desenho.
///
/// Era código privado do <see cref="TaskbarBarWindow"/>, e a barra própria acabou desenhando o
/// mesmo bloco com outra mão — texto sem contorno, trilha sem borda, medidas diferentes. Duas
/// cópias do mesmo elemento é o caminho garantido para elas divergirem, e aqui a divergência tinha
/// consequência: o painel muda de lugar e o usuário espera o <b>mesmo painel</b>, não um parecido.
///
/// Da barra própria em pé, o que é novo é só a <b>orientação</b>: as colunas usam o mesmo texto com
/// contorno, a mesma trilha (escura com borda clara, para aparecer sobre papel de parede claro), a
/// mesma régua de cor e a mesma marca de tempo das células deitadas.
/// </summary>
public static class PanelStyle
{
    private static readonly Color Verde = Color.FromArgb(255, 76, 195, 138);
    private static readonly Color Amarelo = Color.FromArgb(255, 232, 176, 75);
    private static readonly Color Vermelho = Color.FromArgb(255, 240, 92, 92);
    private static readonly Color Cinza = Color.FromArgb(255, 156, 151, 145);

    private static readonly Brush Contorno = Congelado(Color.FromArgb(0xE6, 0, 0, 0));

    /// <summary>
    /// A trilha vazia precisa aparecer tanto sobre a barra escura quanto sobre um papel de parede
    /// claro. Um preenchimento escuro resolve o segundo caso e a borda clara o primeiro —
    /// isolados, cada um sumiria justamente no outro. É o par que acompanha o texto com contorno.
    /// </summary>
    private static readonly Brush TrilhaEscura = Congelado(Color.FromArgb(0x73, 0, 0, 0));
    private static readonly Brush TrilhaBorda = Congelado(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

    /// <summary>Escala das células do painel da barra de tarefas.</summary>
    public static double ScaleOf(AppSettings s) => Math.Clamp(s.TaskbarBarScale, 0.8, 1.6);

    private static Brush FundoDaTrilha(AppSettings s) =>
        s.PanelOutline ? TrilhaEscura : BarRenderer.Swatch("TrackBrush");

    private static Thickness BordaDaTrilha(AppSettings s) => new(s.PanelOutline ? 1 : 0);

    // ------------------------------------------------------------------
    // Células deitadas
    // ------------------------------------------------------------------

    /// <summary>Célula compacta de um limite: cabe na altura da barra sem apertar o texto.</summary>
    public static UIElement Cell(UsageBar bar, AppSettings s, double scale)
    {
        var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        cell.Children.Add(new OutlinedText
        {
            Text = s.LabelFor(bar.Kind),
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new OutlinedText
        {
            Text = Math.Round(bar.Percent) + "%",
            FontSize = 12.5 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = BarRenderer.BrushFor(bar.Percent, s),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32 * scale
        });

        var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };

        var track = new Border
        {
            Width = 52 * scale,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = FundoDaTrilha(s),
            BorderBrush = TrilhaBorda,
            BorderThickness = BordaDaTrilha(s),
            ClipToBounds = true
        };
        var grid = new Grid();
        var frac = Math.Clamp(bar.Fraction, 0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = BarRenderer.BrushFor(bar.Percent, s),
            MinWidth = bar.Percent > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;

        // marca do tempo decorrido, no próprio trilho: preenchimento além dela é consumo adiantado
        var timeFrac = s.ShowTimeProgress ? bar.TimeFraction() : null;
        bars.Children.Add(BarRenderer.TrackWithMarker(track, timeFrac, 5));

        row.Children.Add(bars);
        cell.Children.Add(row);

        return Clicavel(cell, DescreverLimite(bar, s), () => AppHost.Current?.ShowDashboard());
    }

    /// <summary>
    /// Célula de um componente: rótulo, uso em destaque e as medidas de apoio (temperatura e
    /// watts) numa linha abaixo. A cor de apoio segue a métrica que mais preocupa, que é a
    /// temperatura quando existe — uso alto é trabalho, temperatura alta é problema.
    /// </summary>
    public static UIElement HardwareCell(string rotulo, ComponentReading c, AppSettings s,
        HardwareSnapshot hw, double scale)
    {
        var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(new OutlinedText
        {
            Text = rotulo,
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush")
        });

        var apoio = HardwareRenderer.Support(c, rotulo);
        if (apoio.Length > 0)
        {
            head.Children.Add(new OutlinedText
            {
                Text = "  " + apoio,
                FontSize = 9.5 * scale,
                Foreground = new SolidColorBrush(HardwareColor(c)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        cell.Children.Add(head);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new OutlinedText
        {
            Text = c.Load.Format("%"),
            FontSize = 12.5 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(LoadColor(c.Load)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 34 * scale
        });

        var largura = 44 * scale;
        var track = new Border
        {
            Width = largura,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = FundoDaTrilha(s),
            BorderBrush = TrilhaBorda,
            BorderThickness = BordaDaTrilha(s),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0),
            ClipToBounds = true
        };
        var grid = new Grid();
        var frac = Math.Clamp((c.Load.Value ?? 0) / 100.0, 0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = ScaleGradient(largura),
            MinWidth = frac > 0 ? 3 : 0
        };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        track.Child = grid;
        row.Children.Add(track);

        cell.Children.Add(row);

        return new Border
        {
            Child = cell,
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 0, 2, 0),
            ToolTip = HardwareRenderer.Describe(rotulo, c, hw)
        };
    }

    /// <summary>
    /// Velocímetro do ritmo: arco pequeno + o número, que é o que se lê de relance. O rótulo diz
    /// de qual limite é o ritmo, e clicar passa para o próximo — por isso ele não diz só "Ritmo".
    /// </summary>
    public static UIElement GaugeCell(RateReading rate, AppSettings s, double scale)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Border
        {
            Child = GaugeRenderer.Build(rate, 34 * scale),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Filtro(s, scale));
        text.Children.Add(new OutlinedText
        {
            Text = ConsumptionRate.Format(rate),
            FontSize = 12 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(GaugeRenderer.ColorFor(rate))
        });
        row.Children.Add(text);

        return Clicavel(row, GaugeRenderer.Describe(rate, s, s.RateKind)
                             + "\n\nClique para ver o ritmo de outro limite.",
            () => AppHost.Current?.CycleRateKind());
    }

    /// <summary>
    /// Botão que troca o tema claro/escuro do Windows. Mostra o tema de DESTINO, não o atual: um
    /// sol quando está escuro, uma lua quando está claro — é o que o clique vai fazer, e o balão
    /// diz isso com todas as letras para não sobrar dúvida.
    ///
    /// Os ícones são desenhados como forma, e não como caractere de fonte. Já houve glifo virando
    /// quadrado vazio neste app por causa do estilo global de fonte, e um botão que não se explica
    /// é pior que botão nenhum.
    /// </summary>
    public static UIElement ThemeCell(AppSettings s, double scale, Action redesenhar)
    {
        var claro = WindowsTheme.IsLight();
        var lado = 17 * scale;

        var icone = new Grid { Width = lado, Height = lado };

        // cópia escura por baixo, mais grossa: é o mesmo contorno do texto, para o ícone não
        // sumir sobre papel de parede claro
        if (s.PanelOutline)
        {
            foreach (var parte in BarRenderer.ThemeIcon(claro, lado, Contorno, 1.5))
                icone.Children.Add(parte);
        }
        foreach (var parte in BarRenderer.ThemeIcon(claro, lado, BarRenderer.Swatch("TextBrush"), 0))
            icone.Children.Add(parte);

        var alvo = claro ? "escuro" : "claro";
        var botao = new Border
        {
            Child = icone,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 0, 2, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = $"Tema do Windows: {(claro ? "claro" : "escuro")}.\nClique para mudar para o {alvo}."
        };
        botao.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;   // senão o clique subiria e abriria o painel
            WindowsTheme.Toggle();
            redesenhar();       // o ícone passa a mostrar o novo destino
        };
        return botao;
    }

    public static UIElement Divider() => new Border
    {
        Width = 1,
        Background = BarRenderer.Swatch("LineBrush"),
        Margin = new Thickness(10, 9, 10, 9)
    };

    /// <summary>Separador entre blocos empilhados, na barra própria em pé.</summary>
    public static UIElement HorizontalDivider() => new Border
    {
        Height = 1,
        Background = BarRenderer.Swatch("LineBrush"),
        Margin = new Thickness(6, 8, 6, 8)
    };

    // ------------------------------------------------------------------
    // Colunas em pé — mesma linguagem, outra orientação
    // ------------------------------------------------------------------

    /// <summary>
    /// Um limite como coluna: rótulo em cima, trilho em pé esticando no meio, porcentagem embaixo.
    ///
    /// O horário de renovação não vira texto aqui: ele já está na marca que atravessa o trilho, que
    /// diz a mesma coisa sem ocupar linha nenhuma — e é a mesma marca das células deitadas. O
    /// número exato continua no balão.
    /// </summary>
    public static UIElement Column(UsageBar bar, AppSettings s, double scale)
    {
        var conteudo = ColunaBase(
            s.LabelFor(bar.Kind),
            Math.Round(bar.Percent) + "%",
            BarRenderer.BrushFor(bar.Percent, s),
            BarRenderer.VerticalTrack(bar.Fraction, BarRenderer.BrushFor(bar.Percent, s),
                10 * scale, double.NaN, s.ShowTimeProgress ? bar.TimeFraction() : null,
                FundoDaTrilha(s), TrilhaBorda, BordaDaTrilha(s)),
            scale);

        return Clicavel(conteudo, DescreverLimite(bar, s), () => AppHost.Current?.ShowDashboard());
    }

    /// <summary>
    /// Um componente como coluna: o trilho de uso e, ao lado, o <b>termômetro</b> — o mesmo do
    /// indicador no jogo.
    ///
    /// São duas perguntas diferentes e por isso duas formas diferentes: o trilho diz quanto do
    /// total está em uso, o termômetro diz quão perto do limite físico a peça está. Ficam juntas
    /// porque a pergunta que se faz é sobre um componente ("como está a GPU?"), não sobre uma
    /// grandeza — termômetro solto entre duas colunas não teria dono.
    ///
    /// Sem leitura de temperatura o termômetro não é desenhado, em vez de aparecer vazio: é o caso
    /// da RAM, que não tem sensor, e da CPU sem elevação. Espaço reservado para nada é ruído.
    /// </summary>
    public static UIElement HardwareColumn(string rotulo, ComponentReading c, AppSettings s,
        HardwareSnapshot hw, double scale)
    {
        var temp = c.Temperature.HasValue ? c.Temperature.Value!.Value : (double?)null;

        var conteudo = ColunaBase(
            rotulo,
            c.Load.Format("%"),
            new SolidColorBrush(LoadColor(c.Load)),
            BarRenderer.VerticalTrack(Math.Clamp((c.Load.Value ?? 0) / 100.0, 0, 1),
                RampaAte(Math.Clamp((c.Load.Value ?? 0) / 100.0, 0, 1)), 10 * scale, double.NaN, null,
                FundoDaTrilha(s), TrilhaBorda, BordaDaTrilha(s)),
            scale,
            // mesma espessura do trilho de uso: os dois medem o mesmo componente, e um mais
            // magro que o outro sugeria hierarquia que não existe
            temp != null ? MeterRenderer.Thermometer(temp.Value, 10 * scale, s.PanelOutline) : null,
            temp != null ? $"{temp.Value:0}°" : null,
            temp != null ? new SolidColorBrush(MeterRenderer.TempRamp(temp.Value)) : null);

        return new Border
        {
            Child = conteudo,
            Background = Brushes.Transparent,
            ToolTip = HardwareRenderer.Describe(rotulo, c, hw)
        };
    }

    /// <summary>
    /// Velocímetro em pé: o arco, o rótulo do limite que ele acompanha e o número embaixo. O
    /// rótulo é o mesmo da célula deitada — sem ele, o velocímetro mede algo que só quem abriu as
    /// configurações sabe qual é.
    /// </summary>
    public static UIElement GaugeColumn(RateReading rate, AppSettings s, double scale)
    {
        var pilha = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        pilha.Children.Add(new Border
        {
            Child = GaugeRenderer.Build(rate, 62 * scale),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var filtro = Filtro(s, scale);
        filtro.HorizontalAlignment = HorizontalAlignment.Center;
        filtro.Margin = new Thickness(0, 3, 0, 0);
        pilha.Children.Add(filtro);

        pilha.Children.Add(new OutlinedText
        {
            Text = ConsumptionRate.Format(rate),
            FontSize = 12 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(GaugeRenderer.ColorFor(rate)),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        return Clicavel(pilha, GaugeRenderer.Describe(rate, s, s.RateKind)
                               + "\n\nClique para ver o ritmo de outro limite.",
            () => AppHost.Current?.CycleRateKind());
    }

    /// <summary>
    /// A carcaça de uma coluna: rótulo em cima, trilho esticando no meio, valor embaixo. O trilho
    /// fica na linha elástica da grade — é o que faz as colunas ocuparem a altura que a barra tem
    /// para dar, em vez de uma altura fixa escolhida no escuro.
    /// </summary>
    private static FrameworkElement ColunaBase(string rotulo, string valor, Brush corDoValor,
        UIElement trilho, double scale, UIElement? aoLado = null, string? valorAoLado = null,
        Brush? corAoLado = null)
    {
        var grade = new Grid { Margin = new Thickness(2, 3, 2, 3) };
        grade.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grade.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titulo = new OutlinedText
        {
            Text = rotulo,
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(titulo, 0);
        grade.Children.Add(titulo);

        if (trilho is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Stretch;
            fe.Margin = new Thickness(0, 4 * scale, 0, 4 * scale);
        }

        // o miolo: só o trilho, ou o trilho e o vizinho lado a lado esticando juntos
        UIElement miolo = trilho;
        if (aoLado != null)
        {
            var par = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            par.Children.Add(trilho);

            if (aoLado is FrameworkElement vizinho)
            {
                vizinho.VerticalAlignment = VerticalAlignment.Stretch;
                vizinho.Margin = new Thickness(4 * scale, 4 * scale, 0, 4 * scale);
            }
            par.Children.Add(aoLado);
            miolo = par;
        }

        Grid.SetRow(miolo, 1);
        grade.Children.Add(miolo);

        var numeros = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        numeros.Children.Add(new OutlinedText
        {
            Text = valor,
            FontSize = 12.5 * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = corDoValor,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (valorAoLado != null)
        {
            numeros.Children.Add(new OutlinedText
            {
                Text = valorAoLado,
                FontSize = 10.5 * scale,
                FontWeight = FontWeights.SemiBold,
                Foreground = corAoLado ?? BarRenderer.Swatch("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5 * scale, 0, 0, 0)
            });
        }

        Grid.SetRow(numeros, 2);
        grade.Children.Add(numeros);

        return grade;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Linha do tempo dos últimos ciclos: verde respondeu, âmbar não conseguiu falar por limite,
    /// vermelho falhou, e o ponto apagado é ciclo sem consulta porque o consumo não mudou.
    /// </summary>
    public static UIElement Dot(ApiCall? call, AppSettings s, double height)
    {
        var cor = call?.Outcome switch
        {
            ApiOutcome.Ok => BarRenderer.Swatch("OkBrush"),
            ApiOutcome.RateLimited => BarRenderer.Swatch("WarnBrush"),
            ApiOutcome.Failed => BarRenderer.Swatch("DangerBrush"),
            _ => s.PanelOutline ? TrilhaBorda : BarRenderer.Swatch("TrackBrush")
        };

        return new Border
        {
            Child = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = cor,
                Opacity = call == null || call.Outcome == ApiOutcome.Idle ? 0.5 : 1,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            },
            Background = Brushes.Transparent,
            Width = 11,
            Height = double.IsNaN(height) ? double.NaN : height,
            VerticalAlignment = double.IsNaN(height) ? VerticalAlignment.Stretch : VerticalAlignment.Center,
            ToolTip = call?.Describe() ?? "ciclo ainda não registrado"
        };
    }

    /// <summary>
    /// A linha do tempo <b>em pé</b>: as mesmas bolinhas, descendo pela lateral em vez de correr
    /// no rodapé.
    ///
    /// Elas ficam ao lado dos limites por dois motivos. Primeiro, é o que aproveita a lateral que
    /// sobrava — a barra em pé tem largura de sobra e altura disputada. Segundo, o dado é dos
    /// limites: cada bolinha é uma consulta que trouxe (ou não) os números que estão ali. No
    /// rodapé, no fim de um bloco de sensores, a faixa parecia falar da CPU.
    /// </summary>
    public static UIElement VerticalTimeline(AppSettings s, System.Collections.Generic.List<ApiCall> calls)
    {
        var coluna = new UniformGrid
        {
            Columns = 1,
            Rows = ApiCallLog.Capacity,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(2, 4, 0, 4)
        };

        // a mais recente embaixo, como a mais recente fica à direita na deitada
        for (var i = 0; i < ApiCallLog.Capacity - calls.Count; i++)
            coluna.Children.Add(Dot(null, s, double.NaN));

        foreach (var call in calls)
            coluna.Children.Add(Dot(call, s, double.NaN));

        return coluna;
    }

    /// <summary>O nome do limite que o velocímetro acompanha, com a seta de "dá para trocar".</summary>
    private static StackPanel Filtro(AppSettings s, double scale)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(new OutlinedText
        {
            Text = s.LabelFor(s.RateKind),
            FontSize = 9.5 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush")
        });
        header.Children.Add(new OutlinedText
        {
            Text = " ↻",
            FontSize = 9 * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        return header;
    }

    /// <summary>Área de clique do bloco inteiro, e não só onde há pixel pintado.</summary>
    private static UIElement Clicavel(UIElement conteudo, string balao, Action acao)
    {
        var hit = new Border
        {
            Child = conteudo,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Padding = new Thickness(2, 0, 2, 0),
            ToolTip = balao
        };
        hit.MouseLeftButtonUp += (_, e) =>
        {
            // sem isto o clique subiria para o painel e abriria a janela por baixo
            e.Handled = true;
            acao();
        };
        return hit;
    }

    private static string DescreverLimite(UsageBar bar, AppSettings s)
    {
        var tip = $"{s.LabelFor(bar.Kind)}: {bar.Percent:0.#}% usado, restam {Math.Max(0, 100 - bar.Percent):0.#}%";
        if (bar.ResetsAt != null) tip += $"\n{bar.ResetText()} (às {bar.ResetClock()})";

        var decorrido = bar.TimeProgressText();
        if (decorrido.Length > 0) tip += $"\n{decorrido}";

        return tip + "\n\nClique para abrir o painel.";
    }

    /// <summary>
    /// A régua recortada no ponto onde a barra parou, de baixo para cima.
    ///
    /// No trilho deitado o gradiente é medido sobre a trilha inteira em unidades absolutas, então
    /// o preenchimento mostra só o pedaço da régua que alcançou. Em pé a trilha estica com a barra
    /// e não há largura absoluta para medir — se o gradiente fosse relativo ao próprio
    /// preenchimento, <b>todo</b> sensor apareceria verde embaixo e vermelho em cima, mesmo a 20%.
    /// Recortar a régua no valor lido devolve exatamente a mesma leitura do trilho deitado.
    /// </summary>
    private static Brush RampaAte(double fracao)
    {
        var f = Math.Clamp(fracao, 0, 1);
        var g = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            StartPoint = new Point(0, 1),
            EndPoint = new Point(0, 0)
        };

        g.GradientStops.Add(new GradientStop(Verde, 0.0));
        if (f <= 0.5)
        {
            g.GradientStops.Add(new GradientStop(Mix(Verde, Amarelo, f <= 0 ? 0 : f / 0.5), 1.0));
        }
        else
        {
            g.GradientStops.Add(new GradientStop(Amarelo, 0.5 / f));
            g.GradientStops.Add(new GradientStop(Mix(Amarelo, Vermelho, (f - 0.5) / 0.5), 1.0));
        }

        g.Freeze();
        return g;
    }

    /// <summary>
    /// Régua de cor da trilha: verde no início, amarela no meio, vermelha no fim. O gradiente é
    /// medido sobre a trilha inteira, e não sobre a parte preenchida — sem isso ele se comprimiria
    /// dentro do preenchimento e a barra ficaria vermelha já nos primeiros por cento, que é o
    /// oposto da ideia.
    ///
    /// </summary>
    public static LinearGradientBrush ScaleGradient(double larguraDaTrilha)
    {
        var g = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(larguraDaTrilha, 0)
        };

        g.GradientStops.Add(new GradientStop(Verde, 0.0));
        g.GradientStops.Add(new GradientStop(Amarelo, 0.5));
        g.GradientStops.Add(new GradientStop(Vermelho, 1.0));
        g.Freeze();
        return g;
    }

    /// <summary>Cor da mesma régua no ponto onde a barra parou — é o que o número mostra.</summary>
    public static Color LoadColor(Reading load)
    {
        if (!load.HasValue) return Cinza;

        var f = Math.Clamp(load.Value!.Value / 100.0, 0, 1);
        return f <= 0.5
            ? Mix(Verde, Amarelo, f / 0.5)
            : Mix(Amarelo, Vermelho, (f - 0.5) / 0.5);
    }

    /// <summary>Temperatura manda na cor de apoio; sem ela, os watts não têm faixa universal.</summary>
    public static Color HardwareColor(ComponentReading c)
    {
        if (!c.Temperature.HasValue) return Cinza;

        var t = c.Temperature.Value!.Value;
        if (t >= 90) return Vermelho;
        if (t >= 80) return Amarelo;
        return Cinza;
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            255,
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
