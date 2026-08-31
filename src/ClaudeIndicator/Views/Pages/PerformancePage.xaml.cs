using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeIndicator.Core;

namespace ClaudeIndicator.Views.Pages;

/// <summary>
/// Histórico de desempenho do PC: uso, temperatura e watts ao longo do tempo, com a janela e os
/// componentes como filtros.
///
/// A página serve à pergunta "o que aconteceu?" — o jogo engasgou às 21h, o PC estava quente? —
/// então as escalas são honestas: uso é sempre 0–100%, temperatura sempre até 100 °C. Só os watts
/// se ajustam à janela, porque não têm teto natural comum entre CPU e GPU.
/// </summary>
public partial class PerformancePage : UserControl
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) };

    private static readonly Color CorCpu = Color.FromArgb(255, 91, 173, 255);
    private static readonly Color CorGpu = Color.FromArgb(255, 76, 195, 138);
    private static readonly Color CorRam = Color.FromArgb(255, 217, 119, 87);

    public PerformancePage()
    {
        InitializeComponent();
        Win3h.IsChecked = true;

        // a página se mantém sozinha enquanto está visível; fora dela, nada roda
        _refresh.Tick += (_, _) => Render();
        Loaded += (_, _) => { Render(); _refresh.Start(); };
        Unloaded += (_, _) => _refresh.Stop();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Render();
    }

    private TimeSpan Janela =>
        Win30m.IsChecked == true ? TimeSpan.FromMinutes(30) :
        Win24h.IsChecked == true ? TimeSpan.FromHours(24) :
        Win7d.IsChecked == true ? TimeSpan.FromDays(7) : TimeSpan.FromHours(3);

    private void Render()
    {
        var pontos = HardwareHistory.Load(Janela);
        var vazio = pontos.Count < 2;

        EmptyText.Visibility = vazio ? Visibility.Visible : Visibility.Collapsed;
        if (vazio)
        {
            SummaryGrid.Children.Clear();
            LoadChartHost.Children.Clear();
            TempChartHost.Children.Clear();
            WattsChartHost.Children.Clear();
            LoadLegend.Children.Clear();
            TempLegend.Children.Clear();
            WattsLegend.Children.Clear();
            return;
        }

        RenderSummary(pontos);

        var series = new List<(string Nome, Color Cor, Func<HardwarePoint, double?> Valor)>();
        if (ChkCpu.IsChecked == true) series.Add(("CPU", CorCpu, p => p.CpuLoad));
        if (ChkGpu.IsChecked == true) series.Add(("GPU", CorGpu, p => p.GpuLoad));
        if (ChkRam.IsChecked == true) series.Add(("RAM", CorRam, p => p.RamLoad));
        Desenhar(LoadChartHost, LoadLegend, pontos, series, 100, "%");

        var temps = new List<(string, Color, Func<HardwarePoint, double?>)>();
        if (ChkCpu.IsChecked == true) temps.Add(("CPU", CorCpu, p => p.CpuTemp));
        if (ChkGpu.IsChecked == true) temps.Add(("GPU", CorGpu, p => p.GpuTemp));
        Desenhar(TempChartHost, TempLegend, pontos, temps, 100, "°C");

        var watts = new List<(string, Color, Func<HardwarePoint, double?>)>();
        if (ChkCpu.IsChecked == true) watts.Add(("CPU", CorCpu, p => p.CpuWatts));
        if (ChkGpu.IsChecked == true) watts.Add(("GPU", CorGpu, p => p.GpuWatts));
        var maiorWatt = pontos.Max(p => Math.Max(p.CpuWatts ?? 0, p.GpuWatts ?? 0));
        Desenhar(WattsChartHost, WattsLegend, pontos, watts,
                 Math.Max(10, Math.Ceiling(maiorWatt / 50) * 50), "W");
    }

    // ------------------------------------------------------------------
    // Resumo
    // ------------------------------------------------------------------

    private void RenderSummary(List<HardwarePoint> pontos)
    {
        SummaryGrid.Children.Clear();

        void Cartao(string titulo, Color cor, Func<HardwarePoint, double?> valor, string unidade)
        {
            var valores = pontos.Select(valor).Where(v => v != null).Select(v => v!.Value).ToList();
            if (valores.Count == 0) return;

            var pilha = new StackPanel();
            pilha.Children.Add(new TextBlock
            {
                Text = titulo,
                FontSize = 12,
                Foreground = new SolidColorBrush(cor),
                FontWeight = FontWeights.SemiBold
            });
            pilha.Children.Add(new TextBlock
            {
                Text = $"média {valores.Average():0}{unidade}",
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 3, 0, 0)
            });
            pilha.Children.Add(new TextBlock
            {
                Text = $"mín {valores.Min():0}{unidade} · máx {valores.Max():0}{unidade}",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });

            SummaryGrid.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 10, 0),
                Child = pilha
            });
        }

        if (ChkCpu.IsChecked == true) Cartao("CPU", CorCpu, p => p.CpuLoad, "%");
        if (ChkGpu.IsChecked == true) Cartao("GPU", CorGpu, p => p.GpuLoad, "%");
        if (ChkRam.IsChecked == true) Cartao("RAM", CorRam, p => p.RamLoad, "%");
    }

    // ------------------------------------------------------------------
    // Gráfico
    // ------------------------------------------------------------------

    /// <summary>
    /// Desenha as séries num Canvas: grade horizontal com rótulos, uma polilinha por série e as
    /// horas no eixo de baixo. Lacunas na coleta (PC desligado, painel desligado) quebram a linha
    /// em vez de atravessar o buraco — atravessar inventaria dados que não existem.
    /// </summary>
    private void Desenhar(Grid host, StackPanel legenda, List<HardwarePoint> pontos,
                          List<(string Nome, Color Cor, Func<HardwarePoint, double?> Valor)> series,
                          double teto, string unidade)
    {
        host.Children.Clear();
        legenda.Children.Clear();
        if (series.Count == 0 || pontos.Count < 2) return;

        var canvas = new Canvas { ClipToBounds = true };
        host.Children.Add(canvas);

        void Redesenhar()
        {
            canvas.Children.Clear();
            var w = host.ActualWidth;
            var h = host.ActualHeight;
            if (w < 60 || h < 40) return;

            const double margemEsq = 38;
            const double margemBaixo = 20;
            var area = new Rect(margemEsq, 4, Math.Max(1, w - margemEsq - 6), Math.Max(1, h - margemBaixo - 8));

            var de = pontos[0].At;
            var ate = pontos[^1].At;
            var duracao = Math.Max(1, (ate - de).TotalSeconds);

            var linha = (Brush)FindResource("LineBrush");
            var mudo = (Brush)FindResource("MutedBrush");

            // grade horizontal em quartos
            for (var i = 0; i <= 4; i++)
            {
                var y = area.Bottom - area.Height * i / 4;
                canvas.Children.Add(new Line
                {
                    X1 = area.Left, X2 = area.Right, Y1 = y, Y2 = y,
                    Stroke = linha, StrokeThickness = i == 0 ? 1 : 0.5
                });
                canvas.Children.Add(Texto($"{teto * i / 4:0}{unidade}", 9.5, mudo,
                                          2, y - 7));
            }

            // horas no eixo de baixo, quatro marcas
            for (var i = 0; i <= 3; i++)
            {
                var quando = de + TimeSpan.FromSeconds(duracao * i / 3);
                var x = area.Left + area.Width * i / 3;
                var texto = Janela >= TimeSpan.FromDays(2)
                    ? quando.ToLocalTime().ToString("dd/MM HH:mm")
                    : quando.ToLocalTime().ToString("HH:mm");
                canvas.Children.Add(Texto(texto, 9.5, mudo,
                                          Math.Min(x - 14, w - 66), area.Bottom + 4));
            }

            // uma lacuna e mais que tres vezes o passo normal de gravacao
            var lacuna = TimeSpan.FromSeconds(35);

            foreach (var (_, cor, valor) in series)
            {
                var atual = new PointCollection();
                DateTimeOffset? anterior = null;

                void Fechar()
                {
                    if (atual.Count >= 2)
                    {
                        canvas.Children.Add(new Polyline
                        {
                            Points = atual,
                            Stroke = new SolidColorBrush(cor),
                            StrokeThickness = 1.6,
                            StrokeLineJoin = PenLineJoin.Round
                        });
                    }
                    atual = new PointCollection();
                }

                foreach (var p in pontos)
                {
                    var v = valor(p);
                    if (v == null) { Fechar(); anterior = null; continue; }
                    if (anterior != null && p.At - anterior.Value > lacuna) Fechar();

                    var x = area.Left + area.Width * (p.At - de).TotalSeconds / duracao;
                    var y = area.Bottom - area.Height * Math.Clamp(v.Value / teto, 0, 1);
                    atual.Add(new Point(x, y));
                    anterior = p.At;
                }
                Fechar();
            }
        }

        host.SizeChanged += (_, _) => Redesenhar();
        host.Dispatcher.BeginInvoke(Redesenhar, DispatcherPriority.Loaded);

        foreach (var (nome, cor, _) in series)
        {
            legenda.Children.Add(new Border
            {
                Width = 10, Height = 10,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(cor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            legenda.Children.Add(new TextBlock
            {
                Text = nome,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }

    private static TextBlock Texto(string t, double tamanho, Brush cor, double x, double y)
    {
        var tb = new TextBlock { Text = t, FontSize = tamanho, Foreground = cor };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        return tb;
    }
}
