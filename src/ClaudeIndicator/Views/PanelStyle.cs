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
    public static UIElement Column(UsageBar bar, AppSettings s, double scale, bool compacto = false)
    {
        var conteudo = ColunaBase(
            s.LabelFor(bar.Kind),
            Math.Round(bar.Percent) + "%",
            BarRenderer.BrushFor(bar.Percent, s),
            BarRenderer.VerticalTrack(bar.Fraction, BarRenderer.BrushFor(bar.Percent, s),
                10 * scale, double.NaN, s.ShowTimeProgress ? bar.TimeFraction() : null,
                FundoDaTrilha(s), TrilhaBorda, BordaDaTrilha(s)),
            scale, compacto: compacto);

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
        HardwareSnapshot hw, double scale, bool compacto = false)
    {
        var temp = c.Temperature.HasValue ? c.Temperature.Value!.Value : (double?)null;

        // O vizinho da coluna: o termômetro em CPU e GPU, a barra de disco na memória. A memória
        // não tem sensor de temperatura e a vaga ficava vazia; o disco é a medida que faltava e
        // que ninguém tinha onde pôr.
        UIElement? vizinho = null;
        string? textoVizinho = null;
        Brush? corVizinho = null;

        if (temp != null)
        {
            // mesma espessura do trilho de uso: os dois medem o mesmo componente, e um mais
            // magro que o outro sugeria hierarquia que não existe
            vizinho = MeterRenderer.Thermometer(temp.Value, 10 * scale, s.PanelOutline);
            textoVizinho = $"{temp.Value:0}°";
            corVizinho = new SolidColorBrush(MeterRenderer.TempRamp(temp.Value));
        }
        else if (rotulo == "RAM" && s.PcShowDisk && hw.Disk.HasAnything)
        {
            vizinho = ComBalao(DiskBar(hw.Disk, s, 10 * scale),
                               HardwareRenderer.DescribeDisk(hw.Disk, hw.Processes));

            // O número é o mesmo tempo de atividade que a coluna "Disco" do Gerenciador de Tarefas
            // mostra, e é exatamente o que a barra desenha. Antes aqui vinha a taxa em MB/s, que
            // obrigava a saber de cor o que o disco aguenta para dizer se 800 era muito ou pouco.
            // Os MB/s continuam no balão, onde há espaço para dizer de que direção eles são.
            textoVizinho = hw.Disk.Busy.Format("%");
            corVizinho = new SolidColorBrush(LoadColor(hw.Disk.Busy));
        }

        var conteudo = ColunaBase(
            rotulo,
            c.Load.Format("%"),
            new SolidColorBrush(LoadColor(c.Load)),
            BarRenderer.VerticalTrack(Math.Clamp((c.Load.Value ?? 0) / 100.0, 0, 1),
                RampaAte(Math.Clamp((c.Load.Value ?? 0) / 100.0, 0, 1)), 10 * scale, double.NaN, null,
                FundoDaTrilha(s), TrilhaBorda, BordaDaTrilha(s)),
            scale,
            vizinho,
            textoVizinho,
            corVizinho,
            compacto);

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
    public static UIElement GaugeColumn(RateReading rate, AppSettings s, double scale, bool compacto = false)
    {
        var pilha = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        // arco menor que a versão anterior: ele é acessório do bloco de limites, e ocupando 62
        // unidades competia em peso com as colunas, que são o assunto
        pilha.Children.Add(new Border
        {
            Child = GaugeRenderer.Build(rate, (compacto ? 40 : 48) * scale),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var filtro = Filtro(s, scale);
        filtro.HorizontalAlignment = HorizontalAlignment.Center;
        filtro.Margin = new Thickness(0, 3, 0, 0);
        pilha.Children.Add(filtro);

        // "0,05% p/min" numa coluna de 72 unidades sai cortado nas duas pontas: o número fica na
        // linha do valor e a unidade desce para uma linha própria, menor
        pilha.Children.Add(new OutlinedText
        {
            Text = ConsumptionRate.FormatShort(rate),
            FontSize = (compacto ? 11 : 12) * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(GaugeRenderer.ColorFor(rate)),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        if (rate.HasData)
        {
            pilha.Children.Add(new OutlinedText
            {
                Text = "p/min",
                FontSize = (compacto ? 8.5 : 9) * scale,
                Foreground = BarRenderer.Swatch("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        return Clicavel(pilha, GaugeRenderer.Describe(rate, s, s.RateKind)
                               + "\n\nClique para ver o ritmo de outro limite.",
            () => AppHost.Current?.CycleRateKind());
    }

    /// <summary>
    /// A carcaça de uma coluna: rótulo em cima, trilho esticando no meio, valor embaixo. O trilho
    /// fica na linha elástica da grade — é o que faz as colunas ocuparem a altura que a barra tem
    /// para dar, em vez de uma altura fixa escolhida no escuro.
    /// </summary>
    /// <summary>
    /// Carcaça de uma coluna. No modo <paramref name="compacto"/> as fontes descem um degrau e os
    /// vãos encurtam: é o que permite a barra em pé chegar a 72 unidades de largura sem cortar
    /// número nenhum. Abaixo disso o rótulo é que não caberia mais, e coluna sem nome não diz de
    /// que limite ela fala.
    /// </summary>
    private static FrameworkElement ColunaBase(string rotulo, string valor, Brush corDoValor,
        UIElement trilho, double scale, UIElement? aoLado = null, string? valorAoLado = null,
        Brush? corAoLado = null, bool compacto = false)
    {
        var grade = new Grid { Margin = new Thickness(2, 3, 2, 3) };
        grade.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grade.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titulo = new OutlinedText
        {
            Text = rotulo,
            FontSize = (compacto ? 9.0 : 9.5) * scale,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(titulo, 0);
        grade.Children.Add(titulo);

        if (trilho is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Stretch;
            fe.Margin = new Thickness(0, (compacto ? 3 : 4) * scale, 0, (compacto ? 3 : 4) * scale);
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
                var folga = (compacto ? 3 : 4) * scale;
                vizinho.Margin = new Thickness(folga, folga, 0, folga);
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
            FontSize = (compacto ? 11.0 : 12.5) * scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = corDoValor,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (valorAoLado != null)
        {
            numeros.Children.Add(new OutlinedText
            {
                Text = valorAoLado,
                FontSize = (compacto ? 9.5 : 10.5) * scale,
                FontWeight = FontWeights.SemiBold,
                Foreground = corAoLado ?? BarRenderer.Swatch("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness((compacto ? 3 : 5) * scale, 0, 0, 0)
            });
        }

        Grid.SetRow(numeros, 2);
        grade.Children.Add(numeros);

        return grade;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Linha do tempo dos últimos ciclos: verde respondeu, âmbar não conseguiu falar por limite,
    /// vermelho falhou, e o ponto apagado é ciclo que fechou sem resposta.
    /// </summary>
    public static UIElement Dot(ApiCall? call, AppSettings s, double height, double width = 11)
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
            Width = width,
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
    public static UIElement VerticalTimeline(AppSettings s, System.Collections.Generic.List<ApiCall> calls,
        bool compacto = false)
    {
        var coluna = new UniformGrid
        {
            Columns = 1,
            Rows = ApiCallLog.Capacity,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(compacto ? 1 : 2, 4, 0, 4)
        };

        var largura = compacto ? 9.0 : 11.0;

        // a mais recente embaixo, como a mais recente fica à direita na deitada
        for (var i = 0; i < ApiCallLog.Capacity - calls.Count; i++)
            coluna.Children.Add(Dot(null, s, double.NaN, largura));

        foreach (var call in calls)
            coluna.Children.Add(Dot(call, s, double.NaN, largura));

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
    /// O disco como coluna inteira, para quando ele não tem a memória de anfitriã.
    ///
    /// Existe porque desligar a memória não pode fazer o disco sumir junto: um esconde o outro
    /// sem dizer, e quem desligou a memória não tem como adivinhar que perdeu o disco também.
    /// </summary>
    public static UIElement DiskColumn(DiskReading d, AppSettings s, HardwareSnapshot hw,
        double scale, bool compacto = false)
    {
        var conteudo = ColunaBase(
            "DISCO",
            d.Busy.Format("%"),
            new SolidColorBrush(LoadColor(d.Busy)),
            DiskBar(d, s, 10 * scale),
            scale,
            compacto: compacto);

        return new Border
        {
            Child = conteudo,
            Background = Brushes.Transparent,
            ToolTip = HardwareRenderer.DescribeDisk(d, hw.Processes)
        };
    }

    /// <summary>
    /// Embrulha um elemento com balão próprio. O WPF mostra o balão do elemento mais interno sob o
    /// ponteiro, então a barra de disco fala de disco mesmo morando dentro da coluna da memória.
    /// </summary>
    private static UIElement ComBalao(UIElement elemento, string texto)
    {
        return new Border
        {
            Child = elemento,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
            ToolTip = texto
        };
    }

    /// <summary>
    /// A barra de disco: uma trilha só, com o zero na linha do meio. A leitura cresce do centro
    /// para cima, a gravação do centro para baixo.
    ///
    /// <b>Por que duas direções e não duas barras.</b> Ler e gravar disputam o mesmo aparelho, e o
    /// que se quer saber olhando de relance é "quanto" e "fazendo o quê". Duas colunas separadas
    /// responderiam as duas perguntas, mas obrigariam a comparar alturas em lugares diferentes.
    /// Saindo da mesma linha, o equilíbrio entre as duas se lê sem comparar nada.
    ///
    /// <b>O que a altura significa.</b> Cada metade vale de 0 a 100% do tempo em que o disco esteve
    /// ocupado. Metade de cima cheia é disco totalmente ocupado lendo. Não é porcentagem da
    /// velocidade máxima do disco: esse número não existe, e o porquê está em <see cref="DiskMonitor"/>.
    ///
    /// A régua de cor é a mesma das outras trilhas, espelhada: verde encostado no centro, vermelho
    /// nas pontas.
    /// </summary>
    public static UIElement DiskBar(DiskReading d, AppSettings s, double largura)
    {
        var raio = largura / 2;
        var leitura = Math.Clamp(d.ReadPercent / 100.0, 0, 1);
        var gravacao = Math.Clamp(d.WritePercent / 100.0, 0, 1);

        var trilha = new Border
        {
            Width = largura,
            CornerRadius = new CornerRadius(raio),
            Background = FundoDaTrilha(s),
            BorderBrush = TrilhaBorda,
            BorderThickness = BordaDaTrilha(s),
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };

        var metades = new Grid();
        metades.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        metades.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var cima = Metade(leitura, raio, true);
        Grid.SetRow(cima, 0);
        metades.Children.Add(cima);

        var baixo = Metade(gravacao, raio, false);
        Grid.SetRow(baixo, 1);
        metades.Children.Add(baixo);

        // A linha do zero, sempre visível. Sem ela uma barra parada seria indistinguível de uma
        // trilha vazia qualquer, e o sentido de "sobe e desce a partir daqui" se perderia.
        var meio = new Border
        {
            Height = 1,
            Background = TrilhaBorda,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRowSpan(meio, 2);
        metades.Children.Add(meio);

        trilha.Child = metades;
        return trilha;
    }

    /// <summary>
    /// Uma das metades da barra de disco. O preenchimento encosta no centro e cresce para longe
    /// dele, então a linha elástica vazia fica do lado de fora.
    /// </summary>
    private static UIElement Metade(double fracao, double raio, bool paraCima)
    {
        var f = Math.Clamp(fracao, 0, 1);

        var grade = new Grid();
        var vazio = new GridLength(Math.Max(1 - f, 0.0001), GridUnitType.Star);
        var cheio = new GridLength(Math.Max(f, 0.0001), GridUnitType.Star);

        grade.RowDefinitions.Add(new RowDefinition { Height = paraCima ? vazio : cheio });
        grade.RowDefinitions.Add(new RowDefinition { Height = paraCima ? cheio : vazio });

        // Arredonda só a ponta de fora. Com as quatro pontas redondas as duas metades se
        // encostavam por dois arcos e abriam um estrangulamento no centro, que lia como duas
        // barras separadas em vez de uma medida saindo do zero.
        var canto = paraCima
            ? new CornerRadius(raio, raio, 0, 0)
            : new CornerRadius(0, 0, raio, raio);

        var enchimento = new Border
        {
            CornerRadius = canto,
            Background = RampaAte(f, paraCima),
            MinHeight = f > 0 ? 3 : 0
        };
        Grid.SetRow(enchimento, paraCima ? 1 : 0);
        grade.Children.Add(enchimento);

        return grade;
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
    private static Brush RampaAte(double fracao) => RampaAte(fracao, true);

    /// <param name="doFundoParaCima">
    /// Falso espelha a régua: o verde nasce em cima e o vermelho cresce para baixo. É o que a
    /// metade de gravação da barra de disco precisa, para que as duas direções saiam do centro
    /// verdes e fiquem quentes conforme se afastam dele.
    /// </param>
    private static Brush RampaAte(double fracao, bool doFundoParaCima)
    {
        var f = Math.Clamp(fracao, 0, 1);
        var g = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            StartPoint = doFundoParaCima ? new Point(0, 1) : new Point(0, 0),
            EndPoint = doFundoParaCima ? new Point(0, 0) : new Point(0, 1)
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
