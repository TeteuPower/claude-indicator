using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Indicadores desenhados por cima do jogo.
///
/// É uma janela em camada, sempre no topo, que não recebe foco e por onde o clique atravessa —
/// o mesmo caminho que overlays de terceiros usam quando não querem injetar nada no processo do
/// jogo. Ela aparece quando um jogo está em primeiro plano e some no instante em que ele sai.
///
/// Limite conhecido e honesto: em tela cheia **exclusiva de verdade** — sem as otimizações de tela
/// cheia do Windows, que hoje são o padrão — o jogo é dono do buffer da tela e nenhuma janela
/// aparece por cima. Nesse caso só um overlay injetado no processo (o caminho do RTSS) resolveria.
/// Em janela sem bordas e em tela cheia com as otimizações ligadas, que é o caso normal no Windows
/// 11, o indicador aparece.
/// </summary>
public partial class GameOverlayWindow : Window
{
    private AppSettings _settings = new();
    private DateTime _lastTopmost = DateTime.MinValue;

    public GameOverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeClickThrough(new WindowInteropHelper(this).Handle);
    }

    public void ApplySettings(AppSettings s)
    {
        _settings = s;
        RootScale.ScaleX = RootScale.ScaleY = s.OverlayScale;

        var alpha = (byte)Math.Clamp(Math.Round(s.OverlayOpacity * 255), 1, 255);
        Root.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x12, 0x11, 0x10));

        OutlinedText.SetOutlineEnabled(Root, s.PanelOutline);
    }

    /// <summary>
    /// Redesenha e reposiciona. <paramref name="game"/> nulo esconde: não há jogo em foco.
    /// </summary>
    public void Render(GameInfo? game, HardwareSnapshot hw, UsageSnapshot? usage, AppSettings s)
    {
        _settings = s;

        if (game == null)
        {
            if (IsVisible) Hide();
            return;
        }

        Rows.Children.Clear();

        if (s.OverlayShowFps) Rows.Children.Add(FpsRow(game.Frames));
        if (s.OverlayShowCpu) Rows.Children.Add(HardwareRow("CPU", hw.Cpu, mostrarMemoria: false));
        if (s.OverlayShowGpu) Rows.Children.Add(HardwareRow("GPU", hw.Gpu, mostrarMemoria: false));
        if (s.OverlayShowRam) Rows.Children.Add(HardwareRow("RAM", hw.Ram, mostrarMemoria: true));
        if (s.OverlayShowClaude) AddClaudeRows(usage, s);

        if (Rows.Children.Count == 0)
        {
            if (IsVisible) Hide();
            return;
        }

        if (!IsVisible) Show();

        // O tamanho só é conhecido depois da medição; sem isto o primeiro posicionamento usaria
        // largura zero e o bloco nasceria fora do canto escolhido.
        Dispatcher.BeginInvoke(new Action(() => Reposition(game)),
                               System.Windows.Threading.DispatcherPriority.Loaded);
        Reposition(game);
    }

    // ------------------------------------------------------------------
    // Conteúdo
    // ------------------------------------------------------------------

    private UIElement FpsRow(FrameStats frames)
    {
        var linha = new StackPanel { Orientation = Orientation.Horizontal };

        if (!frames.HasValue)
        {
            linha.Children.Add(new OutlinedText
            {
                Text = "FPS —",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = BarRenderer.Swatch("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            return linha;
        }

        linha.Children.Add(new OutlinedText
        {
            Text = $"{frames.Fps:0}",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(FpsColor(frames.Fps)),
            VerticalAlignment = VerticalAlignment.Center
        });
        linha.Children.Add(new OutlinedText
        {
            Text = "FPS",
            FontSize = 11,
            Margin = new Thickness(4, 0, 0, 2),
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Bottom
        });

        if (_settings.OverlayShowFrameTime)
        {
            linha.Children.Add(new OutlinedText
            {
                Text = $"{frames.FrameTimeMs:0.0} ms",
                FontSize = 12,
                Margin = new Thickness(10, 0, 0, 2),
                Foreground = BarRenderer.Swatch("TextBrush"),
                VerticalAlignment = VerticalAlignment.Bottom
            });
            linha.Children.Add(new OutlinedText
            {
                Text = $"1% {frames.OnePercentLowFps:0}",
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 2),
                Foreground = BarRenderer.Swatch("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }

        return linha;
    }

    private static UIElement HardwareRow(string rotulo, ComponentReading c, bool mostrarMemoria)
    {
        var linha = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

        linha.Children.Add(new OutlinedText
        {
            Text = rotulo,
            FontSize = 11.5,
            Width = 32,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var valor = mostrarMemoria && c.MemoryUsed.HasValue
            ? c.MemoryUsed.Format(" GB", 1)
            : c.Load.Format("%", 0);

        linha.Children.Add(new OutlinedText
        {
            Text = valor,
            FontSize = 13,
            MinWidth = 46,
            FontWeight = FontWeights.SemiBold,
            Foreground = BarRenderer.Swatch("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        // temperatura e watts só entram quando existem: espaço reservado para nada é ruído
        if (c.Temperature.HasValue)
            linha.Children.Add(Secundario(c.Temperature.Format("°", 0)));
        if (c.Power.HasValue)
            linha.Children.Add(Secundario(c.Power.Format(" W", 0)));
        if (mostrarMemoria && c.Load.HasValue)
            linha.Children.Add(Secundario(c.Load.Format("%", 0)));

        return linha;
    }

    private void AddClaudeRows(UsageSnapshot? usage, AppSettings s)
    {
        var barras = usage?.Visible(s);
        if (barras == null || barras.Count == 0) return;

        var linha = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        for (var i = 0; i < barras.Count; i++)
        {
            var b = barras[i];
            linha.Children.Add(new OutlinedText
            {
                Text = s.LabelFor(b.Kind),
                FontSize = 10.5,
                Margin = new Thickness(i == 0 ? 0 : 10, 0, 4, 0),
                Foreground = BarRenderer.Swatch("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            linha.Children.Add(new OutlinedText
            {
                Text = $"{b.Percent:0}%",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = BarRenderer.BrushFor(b.Percent, s),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        Rows.Children.Add(linha);
    }

    private static TextBlock Secundario(string texto) => new()
    {
        Text = texto,
        FontSize = 11.5,
        Margin = new Thickness(8, 0, 0, 0),
        Foreground = BarRenderer.Swatch("MutedBrush"),
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Verde acima de 60, âmbar entre 30 e 60, vermelho abaixo — a leitura de sempre.</summary>
    private static Color FpsColor(double fps) => fps switch
    {
        >= 60 => Color.FromArgb(255, 76, 195, 138),
        >= 30 => Color.FromArgb(255, 232, 176, 75),
        _ => Color.FromArgb(255, 240, 92, 92)
    };

    // ------------------------------------------------------------------
    // Posicionamento
    // ------------------------------------------------------------------

    /// <summary>
    /// Encosta o bloco no canto escolhido da janela do jogo. Segue a janela, e não o monitor, para
    /// que o indicador continue certo quando o jogo roda em janela menor que a tela.
    /// </summary>
    private void Reposition(GameInfo game)
    {
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var escalaX = m.M11 == 0 ? 1 : m.M11;
        var escalaY = m.M22 == 0 ? 1 : m.M22;

        var esquerda = game.Bounds.Left * escalaX;
        var topo = game.Bounds.Top * escalaY;
        var largura = game.Bounds.Width * escalaX;
        var altura = game.Bounds.Height * escalaY;

        var w = ActualWidth > 0 ? ActualWidth : 160;
        var h = ActualHeight > 0 ? ActualHeight : 60;
        var margem = _settings.OverlayMargin;

        var x = _settings.OverlayAnchor switch
        {
            OverlayAnchor.TopLeft or OverlayAnchor.MiddleLeft or OverlayAnchor.BottomLeft
                => esquerda + margem,
            OverlayAnchor.TopCenter or OverlayAnchor.MiddleCenter or OverlayAnchor.BottomCenter
                => esquerda + (largura - w) / 2,
            _ => esquerda + largura - w - margem
        };

        var y = _settings.OverlayAnchor switch
        {
            OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight
                => topo + margem,
            OverlayAnchor.MiddleLeft or OverlayAnchor.MiddleCenter or OverlayAnchor.MiddleRight
                => topo + (altura - h) / 2,
            _ => topo + altura - h - margem
        };

        if (Math.Abs(Left - x) > 0.5) Left = x;
        if (Math.Abs(Top - y) > 0.5) Top = y;

        ReassertTopmost();
    }

    /// <summary>
    /// O jogo também pede para ficar por cima, e a cada troca de foco a ordem-Z é refeita. Sem
    /// reafirmar de tempos em tempos, o indicador acaba atrás do jogo e some.
    /// </summary>
    private void ReassertTopmost()
    {
        if (DateTime.UtcNow - _lastTopmost < TimeSpan.FromSeconds(2)) return;
        _lastTopmost = DateTime.UtcNow;
        Topmost = false;
        Topmost = true;
    }
}
