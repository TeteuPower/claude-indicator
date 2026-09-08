using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeIndicator.Core;

/// <summary>
/// Deixa janelas de outros programas translúcidas, com a opacidade escolhida.
///
/// É o efeito dos utilitários de antigamente (Glass2k e parentes): a janela <b>inteira</b> fica
/// translúcida, conteúdo incluído, e dá para ver o que está atrás dela. Não é o acrílico da barra —
/// lá só o fundo desfoca e o conteúdo continua nítido. Aqui o conteúdo desbota junto, e é por isso
/// que a opacidade é ajustável: a 100% de transparência a janela vira um fantasma inutilizável.
///
/// Ao contrário da barra do Windows, isto <b>não injeta nada</b>: <c>SetLayeredWindowAttributes</c>
/// é um atributo de janela que se aplica de fora do processo. Sem DLL, sem gancho, sem driver — e
/// portanto sem o assunto do antivírus.
///
/// Três guardas que este tipo existe para garantir:
///
/// 1. <b>Nem toda janela do explorer é uma janela de pastas.</b> A barra de tarefas e a área de
///    trabalho pertencem ao mesmo processo; deixá-las translúcidas por engano seria desastroso — e
///    a barra já tem tratamento próprio, que este não pode atropelar. Por isso a lista de classes
///    proibidas, e não a de processos.
/// 2. <b>Janela que já é <i>layered</i> fica de fora.</b> Programas que desenham com transparência
///    por pixel usam o mesmo bit; trocar para alfa uniforme corromperia o desenho deles.
/// 3. <b>Devolver o que pegou.</b> Cada janela tocada guarda se o bit de transparência foi posto
///    por nós, para tirá-lo na saída sem mexer em quem já o tinha.
/// </summary>
public sealed class WindowGlass
{
    /// <summary>
    /// Classes de janela que nunca recebem vidro. São peças do shell que pertencem ao explorer mas
    /// não são "janelas de aplicativo": a barra de tarefas (que tem tratamento próprio), a área de
    /// trabalho, e as superfícies XAML do menu Iniciar, da busca e da central de notificações.
    /// </summary>
    private static readonly HashSet<string> ClassesProibidas = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Shell_InputSwitchTopLevelWindow",
        "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
        "Windows.UI.Composition.DesktopWindowContentBridge", "XamlExplorerHostIslandWindow",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "MultitaskingViewFrame", "ForegroundStaging", "TaskListThumbnailWnd"
    };

    /// <summary>Janelas que recebemos: o valor diz se fomos nós que pusemos o bit de transparência.</summary>
    private readonly Dictionary<IntPtr, bool> _tocadas = new();

    /// <summary>Ligadas na mão pelo atalho, mesmo sem o aplicativo estar na lista.</summary>
    private readonly HashSet<IntPtr> _ligadasNaMao = new();

    /// <summary>Desligadas na mão pelo atalho, mesmo com o aplicativo na lista — a mão manda.</summary>
    private readonly HashSet<IntPtr> _desligadasNaMao = new();

    /// <summary>Quantas janelas estão com vidro agora, para a tela de configurações dizer.</summary>
    public int Ativas => _tocadas.Count;

    // ------------------------------------------------------------------ varredura

    /// <summary>
    /// Percorre as janelas abertas e acerta cada uma conforme as regras: as dos aplicativos
    /// listados (e as ligadas na mão) ganham vidro, as demais perdem. Chamada quando outra janela
    /// vai para a frente — que é quando janela nova aparece — e ao salvar as configurações.
    /// </summary>
    public void Varrer(AppSettings s)
    {
        LimparFechadas();

        if (!s.GlassEnabled)
        {
            DevolverTudo();
            return;
        }

        var alfa = AlfaDe(s);
        var apps = new HashSet<string>(s.GlassApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        EnumWindows((h, _) =>
        {
            try
            {
                if (!Elegivel(h, out var processo)) return true;

                var deve = _ligadasNaMao.Contains(h)
                           || (apps.Contains(processo) && !_desligadasNaMao.Contains(h));

                if (deve) Aplicar(h, alfa);
                else if (_tocadas.ContainsKey(h)) Devolver(h);
            }
            catch
            {
                // janela fechou no meio da varredura: segue para a próxima
            }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// Liga ou desliga o vidro na janela em foco — o atalho. Devolve o que aconteceu, para o app
    /// poder avisar. Janela do próprio app e peças do shell não entram.
    /// </summary>
    public string AlternarAtiva(AppSettings s)
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "Nenhuma janela em foco.";
        if (!Elegivel(h, out var processo)) return "Esta janela não aceita transparência.";

        if (_tocadas.ContainsKey(h))
        {
            Devolver(h);
            _ligadasNaMao.Remove(h);
            _desligadasNaMao.Add(h);
            return $"{processo}: transparência desligada.";
        }

        Aplicar(h, AlfaDe(s));
        _desligadasNaMao.Remove(h);
        _ligadasNaMao.Add(h);
        return $"{processo}: transparência ligada.";
    }

    /// <summary>Devolve todas as janelas ao estado original. Chamada ao sair e ao desligar a opção.</summary>
    public void DevolverTudo()
    {
        foreach (var h in new List<IntPtr>(_tocadas.Keys)) Devolver(h);
        _ligadasNaMao.Clear();
        _desligadasNaMao.Clear();
    }

    /// <summary>
    /// As janelas que aceitariam vidro agora, para a lista de escolha das configurações.
    ///
    /// Usa a mesma <see cref="Elegivel"/> da varredura de propósito: se a lista mostrasse uma
    /// janela que a varredura recusa, o usuário escolheria um aplicativo e nada aconteceria. E é
    /// por isso que não dá para reaproveitar a lista do seletor de jogos — aquela esconde o
    /// explorer e as janelas pequenas, que aqui são justamente as mais pedidas.
    /// </summary>
    public static List<WindowCandidate> Candidatas()
    {
        var lista = new List<WindowCandidate>();

        EnumWindows((h, _) =>
        {
            try
            {
                if (!Elegivel(h, out var processo)) return true;
                if (!GetWindowRect(h, out var r)) return true;

                var titulo = TituloDe(h);
                if (titulo.Length == 0) return true;

                GetWindowThreadProcessId(h, out var pid);

                lista.Add(new WindowCandidate
                {
                    Handle = h,
                    ProcessId = (int)pid,
                    ProcessName = processo,
                    Title = titulo,
                    Bounds = new GameInfo.Rect(r.Left, r.Top, r.Right, r.Bottom),
                    Monitor = WindowScanner.MonitorOf(h)
                });
            }
            catch
            {
                // janela fechou no meio da varredura: segue para a próxima
            }
            return true;
        }, IntPtr.Zero);

        lista.Sort((a, b) =>
        {
            var porApp = string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
            return porApp != 0 ? porApp : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        });
        return lista;
    }

    // ------------------------------------------------------------------ uma janela

    private void Aplicar(IntPtr hwnd, byte alfa)
    {
        if (_tocadas.TryGetValue(hwnd, out _))
        {
            // já é nossa: só acerta o valor, que pode ter mudado nas configurações
            SetLayeredWindowAttributes(hwnd, 0, alfa, LwaAlpha);
            return;
        }

        var estilo = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();

        // Já era translúcida antes de nós: provavelmente desenha com transparência por pixel, e
        // trocar para alfa uniforme estragaria o desenho. Fica como está.
        if ((estilo & WsExLayered) != 0) return;

        if (SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(estilo | WsExLayered)) == IntPtr.Zero
            && Marshal.GetLastWin32Error() != 0)
            return;

        if (!SetLayeredWindowAttributes(hwnd, 0, alfa, LwaAlpha))
        {
            // não deu: desfaz o bit para não deixar a janela num estado que não pedimos
            SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(estilo));
            return;
        }

        _tocadas[hwnd] = true;
    }

    private void Devolver(IntPtr hwnd)
    {
        if (!_tocadas.TryGetValue(hwnd, out var puseramosOBit)) return;
        _tocadas.Remove(hwnd);

        if (!IsWindow(hwnd)) return;

        try
        {
            // opaca de novo antes de mexer no estilo, senão sobra um quadro translúcido
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);

            if (puseramosOBit)
            {
                var estilo = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
                SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(estilo & ~WsExLayered));
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwAllChildren | RdwFrame);
            }
        }
        catch
        {
            // janela morrendo: nada a devolver
        }
    }

    private void LimparFechadas()
    {
        List<IntPtr>? mortas = null;
        foreach (var h in _tocadas.Keys)
        {
            if (!IsWindow(h)) (mortas ??= new List<IntPtr>()).Add(h);
        }

        if (mortas == null) return;
        foreach (var h in mortas)
        {
            _tocadas.Remove(h);
            _ligadasNaMao.Remove(h);
            _desligadasNaMao.Remove(h);
        }
    }

    // ------------------------------------------------------------------ elegibilidade

    /// <summary>
    /// A janela pode receber vidro? Precisa ser uma janela de aplicativo visível, de outro
    /// processo, e não ser uma das peças do shell da lista proibida.
    /// </summary>
    private static bool Elegivel(IntPtr hwnd, out string processo)
    {
        processo = "";

        if (!IsWindowVisible(hwnd)) return false;

        var estilo = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        if ((estilo & WsExToolWindow) != 0) return false;

        // aplicativo de loja suspenso: a janela existe mas não desenha nada
        if (DwmGetWindowAttribute(hwnd, DwmaCloaked, out var oculta, sizeof(int)) == 0 && oculta != 0)
            return false;

        if (!GetWindowRect(hwnd, out var r)) return false;
        if (r.Right - r.Left < 200 || r.Bottom - r.Top < 120) return false;

        if (ClassesProibidas.Contains(ClasseDe(hwnd))) return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;   // as nossas janelas não

        processo = ProcessoDe(pid);
        return processo.Length > 0;
    }

    private static string TituloDe(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private static byte AlfaDe(AppSettings s) =>
        (byte)Math.Clamp(Math.Round(s.GlassOpacity * 255), 64, 255);

    private static string ClasseDe(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// Nome do processo sem abrir um objeto gerenciado: a varredura roda a cada troca de janela em
    /// primeiro plano, e criar dezenas de <c>Process</c> a cada alt-tab seria desperdício.
    /// </summary>
    private static string ProcessoDe(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return "";

        try
        {
            var buffer = new StringBuilder(512);
            var tamanho = buffer.Capacity;
            if (!QueryFullProcessImageNameW(h, 0, buffer, ref tamanho)) return "";

            var caminho = buffer.ToString();
            var barra = caminho.LastIndexOf('\\');
            var nome = barra >= 0 ? caminho[(barra + 1)..] : caminho;
            return nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? nome[..^4] : nome;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    // ------------------------------------------------------------------

    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExToolWindow = 0x00000080;
    private const uint LwaAlpha = 0x00000002;
    private const int DwmaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwFrame = 0x0400;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
