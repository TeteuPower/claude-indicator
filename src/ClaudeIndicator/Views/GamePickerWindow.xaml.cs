using System.Windows;
using System.Windows.Controls;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Lista as janelas abertas para o usuário apontar qual é o jogo.
///
/// O fluxo pensado é: abrir o jogo, deixá-lo rodando, voltar aqui e escolher a janela dele. Por
/// isso a lista mostra tamanho, se a janela cobre o monitor e o FPS quando há medição — os três
/// sinais que identificam o jogo sem precisar reconhecer o nome do executável.
/// </summary>
public partial class GamePickerWindow : Window
{
    /// <summary>Nome do processo escolhido, ou null se a janela foi cancelada.</summary>
    public string? ChosenProcess { get; private set; }

    public GamePickerWindow()
    {
        InitializeComponent();
        Carregar();
    }

    private void Carregar()
    {
        var frames = AppHost.Current;
        var janelas = WindowScanner.Scan();

        Lista.Items.Clear();
        foreach (var j in janelas)
        {
            if (frames?.FrameMonitorRunning == true)
                j.Fps = frames.FpsOf(j.ProcessId);

            Lista.Items.Add(new ListBoxItem
            {
                Content = Linha(j),
                Tag = j,
                Padding = new Thickness(10, 7, 10, 7)
            });
        }

        if (Lista.Items.Count > 0) Lista.SelectedIndex = 0;
        BtnUse.IsEnabled = Lista.Items.Count > 0;
    }

    private static UIElement Linha(WindowCandidate j)
    {
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = j.Title.Length > 70 ? j.Title[..70] + "…" : j.Title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        pilha.Children.Add(new TextBlock
        {
            Text = $"{j.ProcessName}.exe · {j.Describe()}",
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = BarRenderer.Swatch("MutedBrush")
        });
        return pilha;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Carregar();

    private void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (Lista.SelectedItem is not ListBoxItem item || item.Tag is not WindowCandidate j) return;
        ChosenProcess = j.ProcessName;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
