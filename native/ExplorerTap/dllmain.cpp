// A DLL que entra no Explorer.
//
// Dois papeis no mesmo arquivo, porque o Windows exige que sejam o mesmo modulo:
//
// 1. Gancho. O app chama SetWindowsHookEx(WH_GETMESSAGE) apontando para HookProc, na thread da
//    barra de tarefas. O Windows carrega esta DLL dentro do Explorer para poder chamar HookProc
//    la - e e esse carregamento que nos coloca no processo certo. Na primeira chamada, iniciamos
//    o diagnostico do XAML; nas seguintes, ficamos de olho na mensagem registrada que o app manda
//    pedindo para reler a memoria compartilhada.
//
// 2. Servidor COM do TAP. InitializeXamlDiagnosticsEx pede ao XAML que crie um objeto com o nosso
//    CLSID, e o XAML faz isso pelo DllGetClassObject padrao do COM. Esse objeto (Tap) e quem recebe
//    a arvore visual da barra e troca o fundo dela.
//
// E o mesmo desenho do TranslucentTB (ExplorerHooks + ExplorerTAP), que e o que funciona no
// Windows 11: a barra e XAML, e o fundo dela so se alcanca de dentro do processo, pela API de
// diagnostico do XAML - a mesma que a arvore visual ao vivo do Visual Studio usa.

#include <windows.h>
#include <ocidl.h>
#include <atomic>

#include "Tap.h"

using ClaudeIndicator::Tap::Tap;
using ClaudeIndicator::Tap::CLSID_Tap;

namespace
{
    HMODULE g_modulo = nullptr;
    std::atomic<bool> g_iniciado{ false };
    UINT g_msgRefresh = 0;

    // Assinatura do InitializeXamlDiagnosticsEx, exportada por Windows.UI.Xaml.dll. Resolvida em
    // tempo de execucao porque nao ha lib de importacao para ela no SDK.
    using InitializeXamlDiagnosticsExFn = HRESULT(WINAPI*)(
        LPCWSTR endPointName, DWORD pid, LPCWSTR wszDllXamlDiagnostics,
        LPCWSTR wszTAPDllName, CLSID tapClsid, LPCWSTR wszInitializationData);

    // Fixa esta DLL no processo. Quando o app remove o gancho, o Windows descarregaria a DLL do
    // Explorer - mas o XAML ainda tem uma referencia ao nosso Tap, e codigo descarregado com
    // referencia viva e uma queda do Explorer esperando para acontecer. Uma referencia extra
    // mantem a DLL ate o Explorer reiniciar, que e o unico momento seguro de ela sair.
    void Fixar()
    {
        wchar_t caminho[MAX_PATH]{};
        if (GetModuleFileNameW(g_modulo, caminho, MAX_PATH) > 0)
            LoadLibraryW(caminho);
    }

    void Iniciar()
    {
        Fixar();

        HMODULE xaml = GetModuleHandleW(L"Windows.UI.Xaml.dll");
        if (!xaml) xaml = LoadLibraryW(L"Windows.UI.Xaml.dll");
        if (!xaml) return;

        auto init = reinterpret_cast<InitializeXamlDiagnosticsExFn>(
            GetProcAddress(xaml, "InitializeXamlDiagnosticsEx"));
        if (!init) return;

        wchar_t caminho[MAX_PATH]{};
        GetModuleFileNameW(g_modulo, caminho, MAX_PATH);

        // O nome do ponto de conexao e o mesmo que o Visual Studio usa; nao ha registro, e so um
        // nome. O XAML carrega a DLL apontada (nos mesmos) e cria o TAP pelo CLSID.
        init(L"VisualDiagConnection1", GetCurrentProcessId(), L"", caminho, CLSID_Tap, L"");
    }

    // ------------------------------------------------------------------ fabrica COM

    class Fabrica final : public IClassFactory
    {
    public:
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        ULONG STDMETHODCALLTYPE AddRef() override { return ++_refs; }

        ULONG STDMETHODCALLTYPE Release() override
        {
            const ULONG restam = --_refs;
            if (restam == 0) delete this;
            return restam;
        }

        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
        {
            if (outer) return CLASS_E_NOAGGREGATION;
            auto* tap = new Tap();
            const HRESULT hr = tap->QueryInterface(riid, ppv);
            tap->Release();
            return hr;
        }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }

    private:
        std::atomic<ULONG> _refs{ 1 };
    };
}

// As tres exportacoes estao no ExplorerTap.def: as duas do COM ja vem declaradas pelo SDK sem
// dllexport, e redeclarar com dllexport e erro de vinculo.
extern "C"
{
    // Chamado pelo XAML quando o InitializeXamlDiagnosticsEx pede o TAP.
    HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (rclsid != CLSID_Tap) return CLASS_E_CLASSNOTAVAILABLE;

        auto* fabrica = new Fabrica();
        const HRESULT hr = fabrica->QueryInterface(riid, ppv);
        fabrica->Release();
        return hr;
    }

    // Nunca: enquanto estivermos no Explorer, o XAML pode ter referencia ao Tap.
    HRESULT STDAPICALLTYPE DllCanUnloadNow()
    {
        return S_FALSE;
    }

    // O gancho de mensagens da thread da barra. E aqui que a DLL "acorda" dentro do Explorer.
    LRESULT CALLBACK HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code == HC_ACTION)
        {
            if (!g_iniciado.exchange(true)) Iniciar();

            auto* msg = reinterpret_cast<MSG*>(lParam);
            if (msg && g_msgRefresh != 0 && msg->message == g_msgRefresh)
            {
                if (auto* tap = Tap::Instancia()) tap->Aplicar();
            }
        }

        return CallNextHookEx(nullptr, code, wParam, lParam);
    }
}

BOOL APIENTRY DllMain(HMODULE modulo, DWORD motivo, LPVOID)
{
    if (motivo == DLL_PROCESS_ATTACH)
    {
        g_modulo = modulo;
        DisableThreadLibraryCalls(modulo);

        // registrado nos dois processos com a mesma string: o Windows devolve o mesmo numero
        g_msgRefresh = RegisterWindowMessageW(ClaudeIndicator::Tap::kNomeMensagem);
    }
    return TRUE;
}
