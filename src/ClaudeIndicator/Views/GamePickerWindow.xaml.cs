using System.Windows;
using System.Windows.Controls;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>Para que serve a escolha — muda os textos e quais janelas a lista mostra.</summary>
public enum PickerAlvo
{
    /// <summary>A janela que recebe os indicadores no jogo.</summary>
    Jogo,

    /// <summary>Um aplicativo em que o indicador nunca deve aparecer.</summary>
    Excecao,

    /// <summary>Um aplicativo cujas janelas ficam translúcidas.</summary>
    Vidro
}

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

    private readonly PickerAlvo _alvo;

    /// <param name="alvo">
    /// A mesma lista serve a três propósitos — escolher onde o indicador aparece, escolher onde ele
    /// nunca deve aparecer, e escolher que aplicativo fica translúcido. Só muda o texto e a régua
    /// de quais janelas entram, então não vale duplicar a janela.
    /// </param>
    public GamePickerWindow(PickerAlvo alvo = PickerAlvo.Jogo)
    {
        InitializeComponent();
        _alvo = alvo;

        if (alvo == PickerAlvo.Excecao)
        {
            Janela.Title = "Escolher o aplicativo a ignorar";
            Titulo.Text = "Aplicativos abertos";
            Explicacao.Text = "O indicador nunca vai aparecer sobre o processo escolhido, mesmo "
                            + "que a janela dele ocupe a tela inteira. Fica guardado pelo nome do "
                            + "executável, então vale também nas próximas vezes que ele abrir.";
            BtnUse.Content = "Nunca mostrar neste";
        }
        else if (alvo == PickerAlvo.Vidro)
        {
            Janela.Title = "Escolher o aplicativo a deixar translúcido";
            Titulo.Text = "Janelas abertas";
            Explicacao.Text = "Fica guardado pelo nome do executável: todas as janelas desse "
                            + "aplicativo ficam translúcidas, agora e nas próximas vezes que ele "
                            + "abrir. As janelas do Explorador de Arquivos aparecem aqui; a barra "
                            + "de tarefas e a área de trabalho, não — elas têm ajuste próprio.";
            BtnUse.Content = "Deixar translúcido";
        }

        Carregar();
    }

    private void Carregar()
    {
        var frames = AppHost.Current;
        var janelas = _alvo == PickerAlvo.Vidro ? WindowGlass.Candidatas() : WindowScanner.Scan();

        Lista.Items.Clear();
        foreach (var j in janelas)
        {
            if (_alvo != PickerAlvo.Vidro && frames?.FrameMonitorRunning == true)
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
