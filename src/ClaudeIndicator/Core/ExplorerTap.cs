using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClaudeIndicator.Core;

/// <summary>
/// O lado do app do "tap" no Explorer: coloca a DLL nativa dentro do processo da barra de tarefas
/// e conversa com ela.
///
/// Por que injetar: no Windows 11 a barra é XAML, e o fundo dela é desenhado pelo próprio XAML por
/// cima de qualquer efeito que se peça ao compositor pela janela — testado, nenhum estado da
/// política de acento muda a barra nesta build. O fundo só se alcança de dentro do processo, pela
/// API de diagnóstico do XAML (a mesma da árvore visual ao vivo do Visual Studio). É o desenho do
/// TranslucentTB: um gancho de mensagens carrega a DLL no Explorer, e a DLL liga o diagnóstico.
///
/// A conversa é por memória compartilhada mais um aviso: o app escreve cor e modo num mapeamento
/// nomeado e posta uma mensagem registrada para a thread de cada barra; o gancho vê a mensagem e a
/// DLL relê. Mensagem com dados (WM_COPYDATA) não serve porque o app pode estar elevado e o Explorer
/// não, e a UIPI barra dados de baixo para cima — o mapeamento não tem essa restrição.
///
/// A DLL fica no Explorer até ele reiniciar: descarregá-la com o XAML ainda apontando para o objeto
/// dela derrubaria o Explorer. Sair do app manda "devolver", que restaura o fundo original.
/// </summary>
public sealed class ExplorerTap : IDisposable
{
    // ---- espelho de native/ExplorerTap/Shared.h: mudar lá é mudar aqui ----
    private const string NomeMemoria = @"Local\ClaudeIndicator.ExplorerTap.v1";
    private const string NomeMensagem = "ClaudeIndicator.ExplorerTap.Refresh";
    private const uint Versao = 1;

    private enum Modo : uint
    {
        Devolver = 0,
        Cor = 1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Estado
    {
        public uint Versao;
        public uint Sequencia;
        public uint Modo;
        public uint Argb;
        public uint Dump;
        public uint Reservado0, Reservado1, Reservado2;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string CaminhoDump;
    }

    private static readonly int TamanhoEstado = Marshal.SizeOf<Estado>();

    private readonly uint _mensagem = RegisterWindowMessage(NomeMensagem);
    private MemoryMappedFile? _memoria;
    private IntPtr _modulo;
    private IntPtr _proc;
    private readonly Dictionary<uint, IntPtr> _ganchos = new();
    private uint _sequencia;
    private Estado _estado;

    /// <summary>Onde a DLL fica no disco. Extraída do próprio executável, para funcionar também no portátil.</summary>
    public static string CaminhoDaDll => Path.Combine(AppSettings.DataDir, "ExplorerTap.dll");

    /// <summary>Último motivo de falha, para a tela de configurações dizer o que houve.</summary>
    public string? Erro { get; private set; }

    /// <summary>Há gancho instalado em pelo menos uma barra?</summary>
    public bool Instalado => _ganchos.Count > 0;

    // ------------------------------------------------------------------ instalar

    /// <summary>
    /// Garante a DLL no disco e engancha a thread de cada barra de tarefas. Idempotente: barra já
    /// enganchada é ignorada, barra nova (tela que entrou, Explorer que reiniciou) é enganchada.
    /// </summary>
    public bool Instalar()
    {
        Erro = null;

        try
        {
            if (!ExtrairDll()) return false;
            if (_memoria == null) AbrirMemoria();

            if (_modulo == IntPtr.Zero)
            {
                _modulo = LoadLibrary(CaminhoDaDll);
                if (_modulo == IntPtr.Zero)
                {
                    Erro = "Não deu para carregar a DLL do tap: erro " + Marshal.GetLastWin32Error();
                    return false;
                }

                _proc = GetProcAddress(_modulo, "HookProc");
                if (_proc == IntPtr.Zero)
                {
                    Erro = "A DLL do tap não exporta HookProc.";
                    return false;
                }
            }

            var enganchou = 0;
            foreach (var bar in TaskbarInfo.Bars())
            {
                var thread = GetWindowThreadProcessId(bar.Handle, out _);
                if (thread == 0) continue;

                if (!_ganchos.ContainsKey(thread))
                {
                    var gancho = SetWindowsHookEx(WH_GETMESSAGE, _proc, _modulo, thread);
                    if (gancho == IntPtr.Zero)
                    {
                        Erro = "O Windows recusou o gancho na barra: erro " + Marshal.GetLastWin32Error();
                        continue;
                    }
                    _ganchos[thread] = gancho;
                }

                // a primeira mensagem processada é o que carrega a DLL dentro do Explorer
                PostThreadMessage(thread, _mensagem, IntPtr.Zero, IntPtr.Zero);
                enganchou++;
            }

            if (enganchou == 0 && Erro == null) Erro = "Nenhuma barra de tarefas encontrada para enganchar.";
            return enganchou > 0;
        }
        catch (Exception ex)
        {
            Erro = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Solta os ganchos de threads que não existem mais e engancha as novas — é o que se faz quando
    /// o shell avisa que recriou a barra. A DLL antiga continua no Explorer antigo (que morreu), e
    /// o novo recebe a sua.
    /// </summary>
    public bool Reinstalar()
    {
        var vivas = new HashSet<uint>();
        foreach (var bar in TaskbarInfo.Bars())
        {
            var thread = GetWindowThreadProcessId(bar.Handle, out _);
            if (thread != 0) vivas.Add(thread);
        }

        foreach (var (thread, gancho) in new List<KeyValuePair<uint, IntPtr>>(_ganchos))
        {
            if (!vivas.Contains(thread))
            {
                UnhookWindowsHookEx(gancho);
                _ganchos.Remove(thread);
            }
        }

        return Instalar();
    }

    // ------------------------------------------------------------------ pedidos

    /// <summary>Pinta o fundo da barra com a cor (alfa incluso — 0 é vidro limpo).</summary>
    public void Aplicar(uint argb)
    {
        _estado.Modo = (uint)Modo.Cor;
        _estado.Argb = argb;
        Enviar();
    }

    /// <summary>Devolve o fundo original do Windows.</summary>
    public void Devolver()
    {
        _estado.Modo = (uint)Modo.Devolver;
        Enviar();
    }

    /// <summary>
    /// Pede à DLL a árvore visual da barra num arquivo. É como se descobre, em cada build do Windows,
    /// como a barra se chama por dentro — os nomes que a DLL procura vieram daqui.
    /// </summary>
    public void PedirDump(string caminho)
    {
        _estado.Dump = 1;
        _estado.CaminhoDump = caminho;
        Enviar();
    }

    private void Enviar()
    {
        if (_memoria == null) AbrirMemoria();

        _estado.Versao = Versao;
        _estado.Sequencia = ++_sequencia;
        _estado.CaminhoDump ??= "";

        using var visao = _memoria!.CreateViewStream(0, TamanhoEstado);
        var buffer = new byte[TamanhoEstado];
        var ponteiro = Marshal.AllocHGlobal(TamanhoEstado);
        try
        {
            Marshal.StructureToPtr(_estado, ponteiro, false);
            Marshal.Copy(ponteiro, buffer, 0, TamanhoEstado);
        }
        finally
        {
            Marshal.FreeHGlobal(ponteiro);
        }
        visao.Write(buffer, 0, buffer.Length);
        visao.Flush();

        // o dump é pedido de uma vez: a DLL zera a bandeira depois de escrever
        _estado.Dump = 0;

        foreach (var thread in _ganchos.Keys)
            PostThreadMessage(thread, _mensagem, IntPtr.Zero, IntPtr.Zero);
    }

    private void AbrirMemoria()
    {
        _memoria = MemoryMappedFile.CreateOrOpen(NomeMemoria, TamanhoEstado, MemoryMappedFileAccess.ReadWrite);
    }

    // ------------------------------------------------------------------ a DLL no disco

    /// <summary>
    /// Escreve a DLL embutida no executável em %APPDATA%, se ainda não estiver lá igual. Comparação
    /// por hash, e não por data: a extração é rara e a DLL errada dentro do Explorer é o pior caso.
    /// </summary>
    private bool ExtrairDll()
    {
        using var recurso = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ClaudeIndicator.ExplorerTap.dll");

        if (recurso == null)
        {
            Erro = "Esta versão foi compilada sem a DLL do tap (falta o compilador C++ no build).";
            return false;
        }

        using var memoria = new MemoryStream();
        recurso.CopyTo(memoria);
        var conteudo = memoria.ToArray();

        try
        {
            if (File.Exists(CaminhoDaDll))
            {
                var atual = File.ReadAllBytes(CaminhoDaDll);
                if (atual.Length == conteudo.Length && SHA256.HashData(atual).AsSpan().SequenceEqual(SHA256.HashData(conteudo)))
                    return true;
            }

            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllBytes(CaminhoDaDll, conteudo);
            return true;
        }
        catch (IOException)
        {
            // a DLL está em uso pelo Explorer (versão anterior): a que está lá serve até ele reiniciar
            return File.Exists(CaminhoDaDll);
        }
        catch (Exception ex)
        {
            Erro = "Não deu para gravar a DLL do tap: " + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------

    public void Dispose()
    {
        // devolve o fundo antes de sair; os ganchos saem, a DLL fica no Explorer (ver a classe)
        try
        {
            if (_ganchos.Count > 0) Devolver();
        }
        catch
        {
            // encerrando
        }

        foreach (var gancho in _ganchos.Values) UnhookWindowsHookEx(gancho);
        _ganchos.Clear();

        _memoria?.Dispose();
        _memoria = null;
    }

    private const int WH_GETMESSAGE = 3;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
