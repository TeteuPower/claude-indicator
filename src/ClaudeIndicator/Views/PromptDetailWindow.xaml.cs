using System;
using System.IO;
using System.Windows;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Um prompt inteiro, com o custo do turno que ele disparou. O texto é lido do arquivo de
/// transcrição na hora de abrir — o índice guarda só arquivo e posição.
/// </summary>
public partial class PromptDetailWindow : Window
{
    private string _text = "";

    public PromptDetailWindow(PromptEntry entry, string projectName, bool folderExists)
    {
        InitializeComponent();

        Title = $"Prompt — {projectName}";
        HeaderWhen.Text = entry.At.ToLocalTime().ToString("dd/MM/yyyy 'às' HH:mm");

        HeaderProject.Text = folderExists
            ? entry.Project
            : entry.Project + "  ·  esta pasta não existe mais";

        StatShare.Text = BarRenderer.FormatShare(entry.Share);
        StatTurns.Text = entry.Cost.Turns.ToString("n0");
        StatOutput.Text = Compact(entry.Cost.Output);
        StatCache.Text = Compact(entry.Cost.CacheRead);

        // 200 mil caracteres cobre qualquer prompt real sem risco de travar a janela
        _text = TranscriptIndex.ReadPromptText(entry.File, entry.Offset, 200_000);
        TxtPrompt.Text = _text.Length > 0 ? _text : "(não foi possível ler o texto deste prompt)";

        FooterSource.Text = Path.GetFileName(entry.File) + "  ·  posição " + entry.Offset.ToString("n0");
        FooterSource.ToolTip = entry.File;
    }

    /// <summary>1.234.567 vira "1,2 M": o número exato está no tooltip.</summary>
    private static string Compact(long value)
    {
        if (value >= 1_000_000) return (value / 1_000_000.0).ToString("0.#") + " M";
        if (value >= 1_000) return (value / 1_000.0).ToString("0.#") + " k";
        return value.ToString("n0");
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_text);
        }
        catch
        {
            // área de transferência ocupada por outro app: nada a fazer
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
