using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Uma barra de tarefas nossa, encostada numa borda que o Windows deixou livre — topo, esquerda
/// ou direita —, com os painéis do app dentro.
///
/// A diferença entre isto e as outras janelas do app é o registro como <b>appbar</b>
/// (<see cref="DesktopAppBar"/>): quando ligado, o shell tira aquela faixa da área útil e as
/// janelas maximizadas param nela, como param na barra do Windows. Sem o registro a barra apenas
/// flutua por cima, o que também é oferecido — são as duas metades do "empurra as janelas OU fica
/// por cima" e podem valer ao mesmo tempo, que é como a barra do Windows se comporta.
///
/// O rodapé não é oferecido de propósito: lá já existe a barra do Windows, e duas barras
/// disputando a mesma borda só reserva o dobro de espaço.
/// </summary>
public partial class DockWindow : Window
{
    private readonly DesktopAppBar _appBar = new();

    // Rede de segurança do posicionamento: monitor que entra ou sai, barra do Windows que muda de
    // lado, tela que troca de resolução. O aviso do shell resolve a maioria dos casos; o tique
    // cobre o resto sem custo perceptível.
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    private AppSettings _settings = new();
    private UsageSnapshot? _snapshot;
    private HardwareSnapshot _hardware = HardwareSnapshot.Empty;

    private TaskbarInfo.RECT? _target;

    /// <summary>
    /// Janela já fechada. Depois do Close, mexer em Visibility ou pedir o handle explode com
    /// "Cannot set Visibility ... after a Window has closed" — e ainda chegam coisas para fazer:
    /// o tique do relógio que já estava na fila, o aviso do shell sobre a faixa, a mudança de
    /// configuração do Windows. Desligar a barra disparava justamente isso.
    /// </summary>
    private bool _closed;

    /// <summary>Já foi fechada? Quem guarda a referência precisa saber para criar outra.</summary>
    public bool Fechada => _closed;

    private bool _hiddenByUser;
    private bool _fullscreenApp;
    private bool _pendingRender;
    private DateTime _lastTopmost = DateTime.MinValue;

    /// <summary>
    /// Fundo fosco pedido nas preferências. Decidido no nascimento da janela porque
    /// <see cref="Window.AllowsTransparency"/> só pode ser mexido antes de a janela existir de
    /// fato — e os dois são exclusivos: com a transparência por pixel do WPF ligada, o compositor
    /// não tem onde compor o desfoque. Trocar a preferência recria a janela.
    /// </summary>
    private readonly bool _fosco;

    /// <summary>O Windows aceitou o fosco? Recusou, o fundo volta a ser opaco em vez de preto.</summary>
    private bool _foscoAtivo;

    /// <summary>
    /// Como está o fundo desta barra agora: <c>null</c> sem fosco pedido, <c>true</c> fosco no ar,
    /// <c>false</c> fosco pedido e recusado pelo Windows. A tela de configuração mostra isso —
    /// efeito que não entrou tem que dizer que não entrou, senão vira "não funcionou" sem pista.
    /// </summary>
    public bool? FoscoAtivo => _fosco ? _foscoAtivo : null;

    public DockWindow(bool fosco = false)
    {
        InitializeComponent();

        _fosco = fosco;
        AllowsTransparency = !fosco;

        VersionItem.Header = AppInfo.NameWithVersion;

        _follow.Tick += (_, _) => Reposition();

        ToolTipService.SetShowDuration(this, 120000);
        ToolTipService.SetInitialShowDelay(this, 350);
        ToolTipService.SetBetweenShowDelay(this, 0);

        // Com o cursor em cima, redesenhar fecharia o tooltip que está sendo lido: o conteúdo novo
        // espera a saída do mouse, igual aos outros painéis.
        MouseLeave += (_, _) =>
        {
            if (_pendingRender) Rebuild();
        };

        Loaded += (_, _) =>
        {
            Reposition();
            _follow.Start();

            // dá um instante para o compositor desenhar o primeiro quadro antes de conferir
            if (_fosco) Dispatcher.BeginInvoke(new Action(ConferirFosco), DispatcherPriority.ApplicationIdle);
        };

        // Uma faixa reservada por uma janela que morreu fica presa até o Explorer reiniciar. Por
        // isso a remoção acontece em três pontos: fechar a janela, sair do app e o fim do processo.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        Closed += (_, _) =>
        {
            // a ordem importa: marcar e soltar o gancho ANTES de devolver a faixa. Remover a
            // faixa muda a área útil, o Windows avisa todas as janelas, e o aviso voltaria a
            // chamar Reposition numa janela que já morreu.
            _closed = true;
            _follow.Stop();
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

            _hook?.RemoveHook(OnWindowMessage);
            _hook = null;

            _appBar.Remove();
        };
    }

    private HwndSource? _hook;

    private void OnProcessExit(object? sender, EventArgs e) => _appBar.Remove();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNoActivate(hwnd);

        // O fundo é pintado de novo aqui porque o pedido ao compositor precisa do handle, e o
        // ApplySettings pode ter rodado antes de a janela existir de fato.
        if (_fosco) AplicarFundo(_settings);

        _hook = HwndSource.FromHwnd(hwnd);
        _hook?.AddHook(OnWindowMessage);

        // posiciona antes do primeiro quadro: a barra nasce no lugar, sem piscar no meio da tela
        Reposition();
    }

    // ------------------------------------------------------------------
    // Configuração e conteúdo
    // ------------------------------------------------------------------

    public void ApplySettings(AppSettings s)
    {
        if (_closed) return;

        var mudouRegistro = s.DockReserveSpace != _settings.DockReserveSpace
                            || s.DockEdge != _settings.DockEdge
                            || !string.Equals(s.DockMonitor, _settings.DockMonitor, StringComparison.OrdinalIgnoreCase);

        _settings = s;

        AplicarFundo(s);

        // a linha fica na borda de dentro — a que encara o desktop
        Edge.HorizontalAlignment = s.DockEdge switch
        {
            DockEdge.Left => HorizontalAlignment.Right,
            DockEdge.Right => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Stretch
        };
        Edge.VerticalAlignment = s.DockEdge == DockEdge.Top ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        Edge.Width = s.DockEdge == DockEdge.Top ? double.NaN : 1;
        Edge.Height = s.DockEdge == DockEdge.Top ? 1 : double.NaN;

        RootScale.ScaleX = s.DockScale;
        RootScale.ScaleY = s.DockScale;

        Topmost = s.DockTopmost && !_fullscreenApp;
        OutlinedText.SetOutlineEnabled(Root, s.PanelOutline);

        // Trocar de borda ou de modo é registrar de novo: o shell guarda a borda no registro, e
        // pedir SETPOS numa borda diferente da registrada não reserva nada.
        if (mudouRegistro) _appBar.Remove();

        Rebuild();
        Reposition();
    }

    /// <summary>
    /// Pinta o fundo conforme o modo. Sem fosco, a opacidade é a do próprio fundo — e o alfa nunca
    /// chega a 0: numa janela transparente o Windows decide o hit-test pelo alfa do pixel, e 0
    /// deixaria a barra clicável-através. Com fosco, o desfoque é do compositor e a opacidade é só
    /// o tom por cima dele. Fosco pedido mas recusado pelo Windows volta a opaco, porque
    /// "transparente" sem compositor pintando atrás é preto.
    /// </summary>
    /// <summary>
    /// Pinta o fundo conforme o modo.
    ///
    /// Sem fosco, o fundo é um pincel do WPF e a opacidade é a dele — com piso de alfa 1, porque em
    /// janela transparente o Windows decide o hit-test pelo alfa do pixel e 0 deixaria a barra
    /// clicável-através.
    ///
    /// Com fosco, o tom vai <b>no pedido ao compositor</b> e o fundo do WPF fica transparente. Essa
    /// divisão não é preferência: testei as duas em janelas lado a lado, e pintar o tom no WPF por
    /// cima do acrílico dá <b>preto</b> — a janela não é layered, então o alfa do WPF não tem o que
    /// compor. Foi exatamente o que apareceu quando a barra ficou preta.
    /// </summary>
    private void AplicarFundo(AppSettings s)
    {
        if (_fosco)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // O tom tem piso alto de propósito. Vidro quase sem tom deixa as colunas soltas
                // sobre o que estiver atrás e, pior, fica indistinguível de um fundo que não
                // funcionou — foi o que apareceu quando o tom herdado da barra do Windows era 0.
                var tom = (byte)Math.Clamp(Math.Round(s.DockOpacityEffective * 255), 90, 250);
                _foscoAtivo = WindowBackdrop.ApplyFrosted(hwnd, tom);
            }

            // Efeito recusado pelo Windows: fundo opaco. "Transparente" sem o compositor pintando
            // atrás é preto, e barra preta chapada não é o que foi pedido.
            Root.Background = _foscoAtivo
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(255, 0x1B, 0x1A, 0x19));
            return;
        }

        var alpha = (byte)Math.Clamp(Math.Round(s.DockOpacityEffective * 255), 1, 255);
        Root.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x1B, 0x1A, 0x19));
    }

    /// <summary>
    /// Confere se o fosco de fato apareceu, olhando os pixels da própria barra.
    ///
    /// Existe porque o Windows <b>aceita</b> o pedido e devolve sucesso mesmo quando não compõe
    /// nada — foi o que aconteceu numa tela secundária: efeito aceito, nada composto, e a barra
    /// ficou preta, porque em janela não-layered o que não é composto é preto. Não há como
    /// perguntar isso à API, então o app olha o resultado: vidro sobre qualquer fundo produz pelo
    /// menos o tom da barra, nunca preto puro. Preto puro em toda a amostra só acontece quando o
    /// efeito não entrou.
    ///
    /// Concluindo que não entrou, o fundo volta a ser opaco e a tela de configurações passa a
    /// dizer isso — uma barra escura com explicação é melhor que uma barra preta sem nenhuma.
    /// </summary>
    private void ConferirFosco()
    {
        if (_closed || !_fosco || !_foscoAtivo || _target == null) return;

        var r = _target.Value;
        var largura = Math.Min(6, Math.Max(r.Width - 2, 1));
        var altura = Math.Min(60, Math.Max(r.Height - 2, 1));

        // uma tira da borda de fora, onde não há conteúdo desenhado por cima
        var x = _settings.DockEdge == DockEdge.Right ? r.Right - largura - 1 : r.Left + 1;
        var y = r.Top + Math.Max((r.Height - altura) / 2, 1);

        try
        {
            if (!TudoPreto(x, y, largura, altura)) return;   // tem cor: o efeito entrou

            // Preto na barra pode ser efeito que não entrou OU tela que não se deixa fotografar —
            // existe display cuja captura vem preta mesmo com o conteúdo aparecendo na tela. A
            // referência ao lado, fora da barra, separa os dois casos: se ela também vier preta, a
            // conclusão é sobre a captura, não sobre o efeito, e nada muda.
            var fora = _settings.DockEdge == DockEdge.Right ? r.Left - largura - 2 : r.Right + 2;
            if (TudoPreto(fora, y, largura, altura)) return;
        }
        catch
        {
            // sem conseguir olhar, fica como está: melhor não desfazer um efeito que talvez esteja bom
            return;
        }

        _foscoAtivo = false;
        Root.Background = new SolidColorBrush(Color.FromArgb(255, 0x1B, 0x1A, 0x19));
    }

    /// <summary>A região da tela é preto puro em todos os pixels?</summary>
    private static bool TudoPreto(int x, int y, int largura, int altura)
    {
        using var bmp = new System.Drawing.Bitmap(largura, altura);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(largura, altura));

        for (var px = 0; px < largura; px++)
        {
            for (var py = 0; py < altura; py++)
            {
                var c = bmp.GetPixel(px, py);
                if (c.R != 0 || c.G != 0 || c.B != 0) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A janela precisa nascer de novo? Só quando o modo do fundo muda: fosco e transparência por
    /// pixel se decidem antes de a janela existir.
    /// </summary>
    public bool PrecisaRecriar(AppSettings s) => s.DockFrostedEffective != _fosco;

    public void Render(UsageSnapshot? snap, AppSettings s)
    {
        if (_closed) return;

        _snapshot = snap;
        _settings = s;
        Rebuild();
    }

    public void RenderHardware(HardwareSnapshot hw, AppSettings s)
    {
        if (_closed) return;

        _hardware = hw;
        _settings = s;
        Rebuild();
    }

    /// <summary>
    /// Monta os dois painéis e põe cada um no seu lado da barra.
    ///
    /// A forma muda com a orientação, e não é enfeite: <b>em pé</b>, cada limite é uma coluna que
    /// enche de baixo para cima, lado a lado — numa faixa estreita e alta, trilhos deitados um
    /// sobre o outro gastariam a altura e desperdiçariam a largura. <b>Deitada</b>, cada limite é
    /// uma célula ao lado da outra, no mesmo desenho do painel da barra de tarefas.
    ///
    /// O lado de cada painel vem da configuração que ele já tinha na barra de tarefas: "à esquerda"
    /// vira o começo da barra (esquerda na deitada, topo na em pé) e "junto ao relógio" vira o fim.
    /// É o que faz o painel mudar de casa levando as próprias preferências.
    /// </summary>
    private void Rebuild()
    {
        if (_closed) return;

        if (IsMouseOver)
        {
            _pendingRender = true;
            return;
        }
        _pendingRender = false;

        var vertical = _settings.DockEdge != DockEdge.Top;

        // barra estreita aperta o conteúdo: fontes um degrau abaixo, vãos e bolinhas menores.
        // O corte é em 110 unidades, onde a linha "32% 63°" no tamanho cheio começa a encostar
        // na fila de bolinhas.
        var compacto = vertical && _settings.DockWidth < 110;

        Layout.Margin = vertical
            ? new Thickness(compacto ? 5 : 9, compacto ? 8 : 10, compacto ? 5 : 9, compacto ? 8 : 10)
            : new Thickness(12, 5, 12, 5);
        Layout.Children.Clear();

        // Deitada, a linha do tempo entra primeiro e fica na ponta direita: no DockPanel, quem
        // entra antes fica mais na borda. Em pé ela não vem para o rodapé — desce pela lateral,
        // ao lado dos limites, dentro do bloco deles.
        SidePanel.Children.Clear();
        if (!vertical)
        {
            BuildTimeline();
            if (SidePanel.Children.Count > 0)
            {
                DockPanel.SetDock(SidePanel, Dock.Right);
                SidePanel.HorizontalAlignment = HorizontalAlignment.Right;
                SidePanel.VerticalAlignment = VerticalAlignment.Center;
                SidePanel.Margin = new Thickness(12, 0, 0, 0);
                Layout.Children.Add(SidePanel);
            }
        }

        var claude = BuildClaudeBlock(vertical, compacto);
        var pc = BuildPcBlock(vertical, compacto);

        if (vertical)
        {
            // Em pé, os dois blocos dividem a altura INTEIRA da barra, em proporção ao número de
            // indicadores de cada um: é o que faz as colunas serem altas em vez de três tocos
            // no topo com a barra vazia embaixo. O lado escolhido decide quem fica em cima.
            var grade = new Grid();
            var deCima = _settings.DockBarsSide == TaskbarAnchor.Left ? claude : pc;
            var deBaixo = ReferenceEquals(deCima, claude) ? pc : claude;

            // os dois no mesmo lado: mantém a ordem em que estão nas configurações
            if (_settings.DockBarsSide == _settings.DockPcSide)
            {
                deCima = claude;
                deBaixo = pc;
            }

            Empilhar(grade, deCima, Peso(deCima));

            // linha entre os dois: são assuntos diferentes — assinatura e computador — e sem ela
            // as seis colunas viram uma lista só, onde a Fable e a CPU parecem do mesmo grupo
            if (deCima != null && deBaixo != null) Empilhar(grade, PanelStyle.HorizontalDivider(), 0);

            Empilhar(grade, deBaixo, Peso(deBaixo));

            if (grade.Children.Count > 0)
            {
                Layout.Children.Add(grade);
                DockPanel.SetDock(grade, Dock.Top);

                // o que sobra depois da linha do tempo é o que a grade ocupa
                Layout.LastChildFill = true;
            }
        }
        else
        {
            Layout.LastChildFill = false;
            Place(claude, _settings.DockBarsSide);
            Place(pc, _settings.DockPcSide);
        }

        if (claude == null && pc == null)
        {
            var vazio = Aviso("Nenhum painel escolhido para esta barra.", vertical ? 160 : 320);
            DockPanel.SetDock(vazio, vertical ? Dock.Top : Dock.Left);
            Layout.Children.Add(vazio);
        }
    }

    /// <summary>Quantos indicadores o bloco tem — é o peso dele na divisão da altura.</summary>
    /// <summary>
    /// Quantos indicadores o bloco tem — é o peso dele na divisão da altura. Vem anotado no
    /// próprio elemento na montagem: adivinhar percorrendo a árvore quebrava a cada camada nova
    /// (a fila de bolinhas ao lado foi uma).
    /// </summary>
    private static int Peso(UIElement? bloco) =>
        bloco is FrameworkElement fe && fe.Tag is int n && n > 0 ? n : 1;

    /// <summary>
    /// Mais uma linha na pilha. Peso zero é para o que tem tamanho próprio — o separador —, e o
    /// resto divide a altura em proporção ao número de indicadores: assim as colunas dos dois
    /// painéis saem do mesmo tamanho, em vez de o painel com menos indicadores ganhar colunas
    /// mais altas.
    /// </summary>
    private static void Empilhar(Grid grade, UIElement? bloco, int peso)
    {
        if (bloco == null) return;

        grade.RowDefinitions.Add(new RowDefinition
        {
            Height = peso > 0 ? new GridLength(peso, GridUnitType.Star) : GridLength.Auto
        });
        Grid.SetRow(bloco, grade.RowDefinitions.Count - 1);
        grade.Children.Add(bloco);
    }

    /// <summary>Encosta o bloco no começo ou no fim da barra deitada.</summary>
    private void Place(UIElement? bloco, TaskbarAnchor lado)
    {
        if (bloco == null) return;

        DockPanel.SetDock(bloco, lado == TaskbarAnchor.Left ? Dock.Left : Dock.Right);
        Layout.Children.Add(bloco);
    }

    /// <summary>
    /// O painel da assinatura: os limites e, junto com eles, o velocímetro do ritmo — na barra de
    /// tarefas o velocímetro também mora no painel da IA.
    ///
    /// As células e as colunas vêm do <see cref="PanelStyle"/>, o mesmo desenho que o painel da
    /// barra de tarefas usa: o painel aparecendo aqui é o MESMO painel, não um parecido.
    /// </summary>
    private UIElement? BuildClaudeBlock(bool vertical, bool compacto = false)
    {
        if (!_settings.DockShowBars && !_settings.DockShowRate) return null;

        var bars = _settings.DockShowBars
            ? _snapshot?.Visible(_settings) ?? new List<UsageBar>()
            : new List<UsageBar>();

        if (vertical)
        {
            // as colunas esticam na linha elástica; o velocímetro fica na linha de tamanho próprio
            // logo abaixo, porque ele não é uma coluna e roubaria altura das que são
            var colunas = new UniformGrid { Columns = 1 };

            if (_settings.DockShowBars && bars.Count == 0)
                colunas.Children.Add(Aviso(SemDados(), 160));

            foreach (var bar in bars)
                colunas.Children.Add(PanelStyle.Column(bar, _settings, 1.0, compacto));

            colunas.Rows = Math.Max(colunas.Children.Count, 1);

            var extra = _settings.DockShowRate
                ? PanelStyle.GaugeColumn(AppHost.Current?.Rate ?? RateReading.Empty, _settings, 1.0, compacto)
                : null;

            if (colunas.Children.Count == 0 && extra == null) return null;

            // A fila de consultas desce pela direita na altura DAS COLUNAS, e não do bloco todo:
            // ela é a linha do tempo dos limites, então acompanha os limites — descer ao lado do
            // velocímetro faria a última bolinha parecer falar dele.
            UIElement miolo = colunas;
            if (_settings.ShowCallTimeline && colunas.Children.Count > 0)
            {
                var comFila = new Grid();
                comFila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                comFila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(colunas, 0);
                comFila.Children.Add(colunas);

                var fila = PanelStyle.VerticalTimeline(_settings,
                    AppHost.Current?.Calls.Recent() ?? new List<ApiCall>(), compacto);
                Grid.SetColumn(fila, 1);
                comFila.Children.Add(fila);

                miolo = comFila;
            }

            return Empilhado(miolo, extra, colunas.Children.Count);
        }

        var linha = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (_settings.DockShowBars && bars.Count == 0) linha.Children.Add(Aviso(SemDados(), 320));

        foreach (var bar in bars)
        {
            if (linha.Children.Count > 0) linha.Children.Add(PanelStyle.Divider());
            linha.Children.Add(PanelStyle.Cell(bar, _settings, 1.0));
        }

        if (_settings.DockShowRate)
        {
            if (linha.Children.Count > 0) linha.Children.Add(PanelStyle.Divider());
            linha.Children.Add(PanelStyle.GaugeCell(AppHost.Current?.Rate ?? RateReading.Empty, _settings, 1.0));
        }

        return linha.Children.Count > 0 ? linha : null;
    }

    /// <summary>O painel do computador: os sensores escolhidos para ele, e o botão do tema.</summary>
    private UIElement? BuildPcBlock(bool vertical, bool compacto = false)
    {
        if (!_settings.DockShowHardware) return null;

        var sensores = Sensores();
        if (sensores.Count == 0) return null;

        if (vertical)
        {
            // o disco viaja junto da memória; sem ela, ganha coluna própria em vez de sumir
            var discoSozinho = _settings.PcShowDisk && !_settings.PcShowRam && _hardware.Disk.HasAnything;
            var quantas = sensores.Count + (discoSozinho ? 1 : 0);

            var colunas = new UniformGrid { Columns = 1, Rows = quantas };
            foreach (var (rotulo, leitura) in sensores)
                colunas.Children.Add(PanelStyle.HardwareColumn(rotulo, leitura, _settings, _hardware, 1.0, compacto));

            if (discoSozinho)
                colunas.Children.Add(PanelStyle.DiskColumn(_hardware.Disk, _settings, _hardware, 1.0, compacto));

            var extra = _settings.ShowThemeToggle
                ? PanelStyle.ThemeCell(_settings, 1.0, Rebuild)
                : null;

            return Empilhado(colunas, extra, quantas);
        }

        var linha = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (rotulo, leitura) in sensores)
        {
            if (linha.Children.Count > 0) linha.Children.Add(PanelStyle.Divider());
            linha.Children.Add(PanelStyle.HardwareCell(rotulo, leitura, _settings, _hardware, 1.0));
        }

        if (_settings.ShowThemeToggle)
        {
            linha.Children.Add(PanelStyle.Divider());
            linha.Children.Add(PanelStyle.ThemeCell(_settings, 1.0, Rebuild));
        }

        return linha;
    }

    /// <summary>
    /// As colunas na linha elástica e o acessório (velocímetro, botão do tema) na linha de tamanho
    /// próprio embaixo: assim as colunas ocupam toda a altura que a barra tem para dar, e o
    /// acessório não passa a valer uma coluna.
    /// </summary>
    private static UIElement Empilhado(UIElement colunas, UIElement? extra, int quantasColunas)
    {
        if (colunas is FrameworkElement bloco) bloco.Tag = quantasColunas;
        if (extra == null) return colunas;

        var grade = new Grid();
        grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grade.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(colunas, 0);
        grade.Children.Add(colunas);

        if (extra is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.Margin = new Thickness(0, 10, 0, 2);
        }
        Grid.SetRow(extra, 1);
        grade.Children.Add(extra);

        grade.Tag = quantasColunas;
        return grade;
    }

    private string SemDados() =>
        _snapshot == null ? "Claude · carregando…" : _snapshot.Error ?? "Claude · sem dados";

    private List<(string Rotulo, ComponentReading Leitura)> Sensores()
    {
        var quais = new List<(string Rotulo, ComponentReading Leitura)>();
        if (_settings.PcShowCpu) quais.Add(("CPU", _hardware.Cpu));
        if (_settings.PcShowGpu) quais.Add(("GPU", _hardware.Gpu));
        if (_settings.PcShowRam) quais.Add(("RAM", _hardware.Ram));
        return quais;
    }

    private void BuildTimeline()
    {
        SidePanel.Children.Clear();
        if (!_settings.ShowCallTimeline) return;

        var calls = AppHost.Current?.Calls.Recent() ?? new List<ApiCall>();
        for (var i = 0; i < ApiCallLog.Capacity - calls.Count; i++)
            SidePanel.Children.Add(PanelStyle.Dot(null, _settings, 14));
        foreach (var call in calls)
            SidePanel.Children.Add(PanelStyle.Dot(call, _settings, 14));
    }

    private FrameworkElement Aviso(string texto, double largura) => new TextBlock
    {
        Text = texto,
        FontSize = 11,
        Foreground = BarRenderer.Swatch("MutedBrush"),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = largura,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ------------------------------------------------------------------
    // Posição
    // ------------------------------------------------------------------

    /// <summary>
    /// Coloca a barra na borda escolhida, em pixels de tela — o shell fala em pixels, e é por isso
    /// que nada aqui passa por Left/Top do WPF.
    ///
    /// Reservando espaço, quem decide o retângulo final é o Windows: pedimos a borda inteira e ele
    /// devolve o que sobrou depois das barras que já estavam lá. É o que faz a nossa barra no topo
    /// conviver com a do Windows no rodapé sem as duas se sobreporem.
    /// </summary>
    private void Reposition()
    {
        if (_closed || _hiddenByUser || !_settings.ShowDock) return;

        var monitor = TaskbarInfo.ResolveMonitor(_settings.DockMonitor);
        if (monitor == null) return;

        // No modo por cima, jogo em tela cheia manda a barra sair da frente. Reservando espaço não
        // há o que esconder: a faixa está fora da área útil e o jogo cobre a tela toda de qualquer
        // jeito — e desregistrar a cada alt-tab piscaria a área de trabalho inteira.
        var esconder = !_settings.DockReserveSpace
                       && _settings.DockHideOnFullscreen
                       && TaskbarInfo.FullscreenAppOnMonitor(monitor);

        if (esconder)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var espessura = _settings.DockEdge == DockEdge.Top ? _settings.DockHeight : _settings.DockWidth;
        var alvo = DesktopAppBar.EdgeRect(_settings.DockEdge, monitor, espessura);

        if (_settings.DockReserveSpace)
        {
            if (!_appBar.Registered) _appBar.Register(hwnd);
            alvo = _appBar.Reserve(_settings.DockEdge, alvo);
        }
        else if (_appBar.Registered)
        {
            _appBar.Remove();
        }

        _target = alvo;

        // Só escreve quando muda de verdade: reposicionar a cada tique fecharia o tooltip aberto.
        if (!GetWindowRect(hwnd, out var atual)
            || atual.Left != alvo.Left || atual.Top != alvo.Top
            || atual.Right != alvo.Right || atual.Bottom != alvo.Bottom)
        {
            SetWindowPos(hwnd, IntPtr.Zero, alvo.Left, alvo.Top,
                Math.Max(alvo.Width, 1), Math.Max(alvo.Height, 1), SWP_NOZORDER | SWP_NOACTIVATE);

            if (_settings.DockReserveSpace) _appBar.NotifyMoved();
        }

        ReassertTopmost();
    }

    /// <summary>
    /// Reafirma o topo da pilha de vez em quando. Ativar qualquer janela refaz a ordem-Z, e uma
    /// barra que fica atrás não é uma barra. Com o cursor em cima não se mexe: mudar Topmost fecha
    /// o tooltip aberto — e janela coberta não tem cursor em cima, por definição.
    /// </summary>
    private void ReassertTopmost()
    {
        if (_closed || !_settings.DockTopmost || _fullscreenApp || IsMouseOver) return;
        if (DateTime.UtcNow - _lastTopmost < TimeSpan.FromSeconds(10)) return;

        _lastTopmost = DateTime.UtcNow;
        Topmost = false;
        Topmost = true;
    }

    /// <summary>
    /// Avisos que mexem na posição: os do shell sobre a faixa reservada e tela cheia, mais os do
    /// Windows sobre resolução, DPI e área de trabalho. O WM_WINDOWPOSCHANGING é a trava: sem ela,
    /// cada mudança de DPI devolvia a janela para o tamanho antigo em unidades do WPF.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_closed) return IntPtr.Zero;

        if (msg == DesktopAppBar.CallbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case DesktopAppBar.ABN_POSCHANGED:
                    Reposition();
                    break;

                case DesktopAppBar.ABN_FULLSCREENAPP:
                    _fullscreenApp = lParam != IntPtr.Zero;
                    Topmost = _settings.DockTopmost && !_fullscreenApp;
                    break;
            }
            return IntPtr.Zero;
        }

        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED or WM_SETTINGCHANGE)
        {
            Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Background);
            return IntPtr.Zero;
        }

        if (msg == WM_WINDOWPOSCHANGING && _target != null && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.x = _target.Value.Left;
            pos.y = _target.Value.Top;
            pos.cx = Math.Max(_target.Value.Width, 1);
            pos.cy = Math.Max(_target.Value.Height, 1);
            pos.flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
            Marshal.StructureToPtr(pos, lParam, false);
        }

        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------
    // Mostrar e esconder
    // ------------------------------------------------------------------

    public void ShowDock()
    {
        if (_closed) return;

        _hiddenByUser = false;
        Show();
        Reposition();
    }

    /// <summary>Esconde e devolve a faixa: barra oculta que continua reservando espaço é um bug.</summary>
    public void HideDock()
    {
        _hiddenByUser = true;
        if (_closed) return;

        _appBar.Remove();
        _target = null;
        Hide();
    }

    // ------------------------------------------------------------------
    // Interação
    // ------------------------------------------------------------------

    private void OnClick(object sender, MouseButtonEventArgs e) => AppHost.Current?.ShowDashboard();

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var host = AppHost.Current;
        if (host != null) _ = host.RefreshAsync(true);
    }

    private void OnDashboardClick(object sender, RoutedEventArgs e) => AppHost.Current?.ShowDashboard();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => AppHost.Current?.ShowSettings();

    private void OnHideClick(object sender, RoutedEventArgs e) => AppHost.Current?.HideDockBar();

    private void OnExitClick(object sender, RoutedEventArgs e) => AppHost.Current?.Exit();

    // ------------------------------------------------------------------

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_SETTINGCHANGE = 0x001A;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out TaskbarInfo.RECT rect);
}
