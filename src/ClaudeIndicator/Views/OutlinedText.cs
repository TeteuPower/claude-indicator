using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClaudeIndicator.Views;

/// <summary>
/// Texto com contorno escuro desenhado ao redor das letras.
///
/// Existe porque os painéis ficam sobre a barra de tarefas com fundo transparente — e, por baixo
/// dela, sobre o papel de parede. Papel claro apagava o texto. A saída óbvia seria uma sombra
/// desfocada, mas desfoque espalha a tinta: em texto de 10 px o contorno sai fraco justamente onde
/// mais precisa. Um traço sólido de 2 px em volta da letra resolve, é o que os medidores de FPS
/// fazem sobre cena de jogo, e o texto continua legível sobre qualquer fundo.
///
/// O contorno é desenhado com o dobro da espessura e a letra por cima, de modo que só a metade de
/// fora sobra — a metade de dentro seria comida pelo preenchimento de qualquer jeito, e assim a
/// forma da letra não engorda.
///
/// Com <see cref="OutlineEnabledProperty"/> desligada ele desenha texto comum, sem contorno: o
/// visual mais leve de antes, para quem tem papel de parede escuro e prefere assim.
/// </summary>
public sealed class OutlinedText : FrameworkElement
{
    /// <summary>
    /// Liga o contorno. É herdada: basta marcá-la na raiz da janela e todo texto lá dentro
    /// obedece, sem cada ponto de construção precisar saber da preferência.
    /// </summary>
    public static readonly DependencyProperty OutlineEnabledProperty =
        DependencyProperty.RegisterAttached(
            "OutlineEnabled", typeof(bool), typeof(OutlinedText),
            new FrameworkPropertyMetadata(true,
                FrameworkPropertyMetadataOptions.Inherits |
                FrameworkPropertyMetadataOptions.AffectsRender |
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static void SetOutlineEnabled(DependencyObject alvo, bool valor) =>
        alvo.SetValue(OutlineEnabledProperty, valor);

    public static bool GetOutlineEnabled(DependencyObject alvo) =>
        (bool)alvo.GetValue(OutlineEnabledProperty);

    private bool Contornar => GetOutlineEnabled(this);

    private FormattedText? _formatado;
    private Geometry? _geometria;

    private string _texto = string.Empty;
    private double _tamanho = 12;
    private FontWeight _peso = FontWeights.Normal;
    private Brush _preenchimento = Brushes.White;
    private double _espessura = -1;   // negativo = ainda não escolhida à mão

    /// <summary>Cor do contorno. Preto quase opaco lê bem sobre claro e some sobre escuro.</summary>
    public Brush Stroke { get; set; } = Congelado(Color.FromArgb(0xE6, 0, 0, 0));

    public string Text
    {
        get => _texto;
        set { if (_texto != value) { _texto = value ?? string.Empty; Refazer(); } }
    }

    public double FontSize
    {
        get => _tamanho;
        set { if (Math.Abs(_tamanho - value) > 0.01) { _tamanho = value; Refazer(); } }
    }

    public FontWeight FontWeight
    {
        get => _peso;
        set { if (_peso != value) { _peso = value; Refazer(); } }
    }

    public Brush Foreground
    {
        get => _preenchimento;
        set { _preenchimento = value; InvalidateVisual(); }
    }

    /// <summary>
    /// Espessura do contorno. Sem valor definido, ela acompanha a fonte: fixa, ficaria grossa
    /// demais no rótulo de 9 px e fina demais no número grande.
    /// </summary>
    public double StrokeThickness
    {
        get => _espessura >= 0 ? _espessura : Math.Max(1.1, _tamanho * 0.13);
        set { if (Math.Abs(_espessura - value) > 0.01) { _espessura = value; Refazer(); } }
    }

    private void Refazer()
    {
        _formatado = null;
        _geometria = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private FormattedText Formatar()
    {
        if (_formatado != null) return _formatado;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _formatado = new FormattedText(
            _texto,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, _peso, FontStretches.Normal),
            _tamanho,
            Brushes.White,
            dpi);
        return _formatado;
    }

    protected override Size MeasureOverride(Size disponivel)
    {
        var ft = Formatar();
        // a folga é o contorno, que transborda a caixa do texto para os dois lados
        var folga = Contornar ? StrokeThickness * 2 : 0;
        return new Size(ft.WidthIncludingTrailingWhitespace + folga, ft.Height + folga);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_texto.Length == 0) return;

        var ft = Formatar();

        // Sem contorno, o texto sai pelo caminho normal do WPF e não como forma preenchida: é o
        // que devolve a nitidez de antes, com o refinamento de subpixel que o desenho por
        // geometria não tem.
        if (!Contornar)
        {
            ft.SetForegroundBrush(_preenchimento);
            dc.DrawText(ft, new Point(0, 0));
            return;
        }

        _geometria ??= ft.BuildGeometry(new Point(StrokeThickness, StrokeThickness));

        // junta arredondada: sem isso, cantos agudos viram espinhos em fontes com serifa fina
        var caneta = new Pen(Stroke, StrokeThickness * 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        caneta.Freeze();

        dc.DrawGeometry(null, caneta, _geometria);
        dc.DrawGeometry(_preenchimento, null, _geometria);
    }

    private static Brush Congelado(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
