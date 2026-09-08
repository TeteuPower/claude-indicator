using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views;

/// <summary>
/// Os desenhos e os textos de CPU, GPU e memória fora do jogo: as linhas do gadget e as frases
/// que os painéis usam nos balões.
///
/// Existe porque o painel do computador e o gadget mostram os mesmos sensores com formas
/// diferentes — o painel cabe na altura da barra de tarefas, o gadget tem largura à vontade —,
/// mas as palavras têm de ser as mesmas: "uso", "temperatura", "watts", e as explicações de
/// quando uma medida não existe. Duas cópias desse texto viravam duas versões da verdade.
/// </summary>
public static class HardwareRenderer
{
    /// <summary>Medidas de apoio: temperatura, watts ou memória, conforme o componente.</summary>
    public static string Support(ComponentReading c, string rotulo)
    {
        var partes = new List<string>();
        if (c.Temperature.HasValue) partes.Add(c.Temperature.Format("°"));
        if (c.Power.HasValue) partes.Add(c.Power.Format(" W"));
        if (rotulo == "RAM" && c.MemoryUsed.HasValue) partes.Add(c.MemoryUsed.Format(" GB", 1));
        return string.Join(" · ", partes);
    }

    /// <summary>Balão de um componente: o que foi lido e, quando falta algo, por que falta.</summary>
    public static string Describe(string rotulo, ComponentReading c, HardwareSnapshot hw)
    {
        var sb = new StringBuilder();
        sb.Append(rotulo);
        if (c.Name.Length > 0) sb.Append(" — ").Append(c.Name);

        if (c.Load.HasValue) sb.Append("\nUso: ").Append(c.Load.Format("%"));
        if (c.Temperature.HasValue) sb.Append("\nTemperatura: ").Append(c.Temperature.Format(" °C"));
        if (c.Power.HasValue) sb.Append("\nConsumo: ").Append(c.Power.Format(" W", 1));
        if (c.MemoryUsed.HasValue)
        {
            sb.Append("\nMemória: ").Append(c.MemoryUsed.Format(" GB", 1));
            if (c.MemoryTotal.HasValue) sb.Append(" de ").Append(c.MemoryTotal.Format(" GB", 1));
        }

        if (rotulo == "CPU" && c.Temperature.HasValue && hw.CpuTemperatureFromThermalZone)
        {
            sb.Append("\n\nA temperatura vem da zona térmica ACPI — o conjunto ao redor do ")
              .Append("processador, não o sensor interno dele. Acompanha o aquecimento de perto, ")
              .Append("mas pode diferir alguns graus do que o Afterburner mostra.");
        }

        Consumidores(sb, rotulo, hw.Processes);

        if (rotulo == "CPU" && !c.Power.HasValue)
        {
            sb.Append("\n\nOs watts da CPU só existem nos registradores do processador, que ")
              .Append("precisam de um driver de kernel — é a única medida daqui que depende disso.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quem está consumindo mais este componente agora, do maior para o menor. É a pergunta que
    /// vem depois de "está em 75%": foi o navegador, foi a compilação, foi o jogo? Os processos são
    /// somados por nome, senão o Chrome ocuparia a lista inteira com as suas próprias abas.
    /// </summary>
    private static void Consumidores(StringBuilder sb, string rotulo, ProcessTops tops)
    {
        var lista = rotulo switch
        {
            "CPU" => tops.Cpu,
            "GPU" => tops.Gpu,
            "RAM" => tops.Ram,
            _ => null
        };

        if (lista == null) return;

        if (lista.Count == 0)
        {
            // só a GPU merece explicação: uso zero é comum ali, e lista vazia sem uma palavra
            // pareceria falha do app. Em CPU e memória, lista vazia é só a primeira leitura.
            if (rotulo == "GPU" && tops.GpuOk) sb.Append("\n\nNenhum programa usando a GPU agora.");
            return;
        }

        sb.Append("\n\nQuem está consumindo mais:");
        foreach (var uso in lista)
        {
            var valor = rotulo == "RAM"
                ? uso.Value.ToString("0.0") + " GB"
                : uso.Value.ToString("0.#") + "%";
            sb.Append("\n · ").Append(uso.Name).Append(" — ").Append(valor);
        }
    }

    /// <summary>
    /// Linha do gadget vertical, no mesmo desenho das linhas de limite: rótulo e medidas de apoio
    /// à esquerda, uso à direita, trilha embaixo. A cor é a régua de carga do app — verde, âmbar,
    /// vermelho —, então uso alto se vê sem ler o número.
    /// </summary>
    public static UIElement Row(string rotulo, ComponentReading c, HardwareSnapshot hw)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var apoio = Support(c, rotulo);
        var label = new TextBlock
        {
            Text = apoio.Length > 0 ? rotulo + "  ·  " + apoio : rotulo,
            FontSize = 11,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);
        header.Children.Add(label);

        var valor = new TextBlock
        {
            Text = c.Load.Format("%"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Cor(c),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(valor, 1);
        header.Children.Add(valor);

        container.Children.Add(header);
        container.Children.Add(Track(c, 7, new Thickness(0, 5, 0, 0)));

        return new Border
        {
            Child = container,
            Background = Brushes.Transparent,
            ToolTip = Describe(rotulo, c, hw)
        };
    }

    /// <summary>
    /// Célula do gadget horizontal, alinhada com as células de limite: rótulo em cima, uso e
    /// trilha na linha de baixo, apoio embaixo — a mesma leitura de cima para baixo.
    /// </summary>
    public static UIElement Cell(string rotulo, ComponentReading c, HardwareSnapshot hw)
    {
        // a mesma largura das células de limite (BarRenderer.BuildCell): com três limites e três
        // sensores, as colunas de cima e de baixo ficam alinhadas em vez de quase alinhadas
        var cell = new StackPanel { Width = 118 };

        cell.Children.Add(new TextBlock
        {
            Text = rotulo,
            FontSize = 10.5,
            Foreground = BarRenderer.Swatch("MutedBrush")
        });

        var linha = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        linha.Children.Add(new TextBlock
        {
            Text = c.Load.Format("%"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Cor(c),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 36
        });
        linha.Children.Add(Track(c, 6, new Thickness(0, 0, 0, 0), 62));
        cell.Children.Add(linha);

        var apoio = Support(c, rotulo);
        cell.Children.Add(new TextBlock
        {
            // espaço fino no lugar do vazio: sem ele, as células com e sem apoio ficariam com
            // alturas diferentes e a linha inteira desalinhava
            Text = apoio.Length > 0 ? apoio : " ",
            FontSize = 10,
            Foreground = BarRenderer.Swatch("MutedBrush"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        return new Border
        {
            Child = cell,
            Background = Brushes.Transparent,
            ToolTip = Describe(rotulo, c, hw)
        };
    }

    private static SolidColorBrush Cor(ComponentReading c) =>
        new(c.Load.HasValue ? BarRenderer.LoadRamp(c.Load.Value!.Value) : Color.FromArgb(255, 156, 151, 145));

    private static UIElement Track(ComponentReading c, double altura, Thickness margem, double? largura = null)
    {
        var raio = altura / 2;
        var track = new Border
        {
            Height = altura,
            Width = largura ?? double.NaN,
            CornerRadius = new CornerRadius(raio),
            Background = BarRenderer.Swatch("TrackBrush"),
            Margin = margem,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };

        var grid = new Grid();
        var frac = Math.Clamp((c.Load.Value ?? 0) / 100.0, 0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(raio),
            Background = Cor(c),
            MinWidth = frac > 0 ? 4 : 0
        });
        track.Child = grid;

        return track;
    }
}
