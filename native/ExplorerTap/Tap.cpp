#include "Tap.h"

// windows.h define GetCurrentTime como macro para GetTickCount, e o header de animacao do XAML
// tem um metodo com esse nome: sem o undef, a macro engole a declaracao e o header nao compila.
#undef GetCurrentTime

#include <roapi.h>
#include <winstring.h>
#include <windows.ui.h>
#include <windows.ui.xaml.h>
#include <windows.ui.xaml.media.h>
#include <windows.ui.xaml.controls.h>
#include <windows.ui.xaml.shapes.h>
#include <fstream>

using Microsoft::WRL::ComPtr;
namespace WUX = ABI::Windows::UI::Xaml;

namespace ClaudeIndicator::Tap
{
    // {5A8D4F6B-7C21-4D8E-9F3A-2B6C1E0D4A97}
    const CLSID CLSID_Tap = { 0x5A8D4F6B, 0x7C21, 0x4D8E, { 0x9F, 0x3A, 0x2B, 0x6C, 0x1E, 0x0D, 0x4A, 0x97 } };

    Tap* Tap::s_instancia = nullptr;

    // ------------------------------------------------------------------ memoria compartilhada

    bool LerEstado(Estado& estado)
    {
        HANDLE mapa = OpenFileMappingW(FILE_MAP_READ, FALSE, kNomeMemoria);
        if (!mapa) return false;

        auto* visao = static_cast<const Estado*>(MapViewOfFile(mapa, FILE_MAP_READ, 0, 0, sizeof(Estado)));
        bool ok = false;
        if (visao)
        {
            estado = *visao;
            ok = estado.versao == kVersao;
            UnmapViewOfFile(visao);
        }
        CloseHandle(mapa);
        return ok;
    }

    void EscreverEstado(const Estado& estado)
    {
        HANDLE mapa = OpenFileMappingW(FILE_MAP_WRITE, FALSE, kNomeMemoria);
        if (!mapa) return;

        auto* visao = static_cast<Estado*>(MapViewOfFile(mapa, FILE_MAP_WRITE, 0, 0, sizeof(Estado)));
        if (visao)
        {
            *visao = estado;
            UnmapViewOfFile(visao);
        }
        CloseHandle(mapa);
    }

    // ------------------------------------------------------------------ pinceis

    // Um SolidColorBrush novo, na cor pedida. Criado pelo ativador do WinRT porque estamos dentro
    // do processo do XAML, na thread de UI dele - e o unico lugar onde isso e permitido.
    static ComPtr<WUX::Media::IBrush> CriarPincel(uint32_t argb)
    {
        HSTRING_HEADER cabecalho{};
        HSTRING nome{};
        const wchar_t classe[] = L"Windows.UI.Xaml.Media.SolidColorBrush";
        if (FAILED(WindowsCreateStringReference(classe, static_cast<UINT32>(wcslen(classe)), &cabecalho, &nome)))
            return nullptr;

        ComPtr<IInspectable> insp;
        if (FAILED(RoActivateInstance(nome, &insp))) return nullptr;

        ComPtr<WUX::Media::ISolidColorBrush> solido;
        if (FAILED(insp.As(&solido))) return nullptr;

        ABI::Windows::UI::Color cor{};
        cor.A = static_cast<BYTE>((argb >> 24) & 0xFF);
        cor.R = static_cast<BYTE>((argb >> 16) & 0xFF);
        cor.G = static_cast<BYTE>((argb >> 8) & 0xFF);
        cor.B = static_cast<BYTE>(argb & 0xFF);
        solido->put_Color(cor);

        ComPtr<WUX::Media::IBrush> pincel;
        insp.As(&pincel);
        return pincel;
    }

    // Le o fundo atual de um elemento, seja ele painel, controle, borda ou forma. Devolve false
    // quando o elemento nao tem fundo que se possa trocar.
    static bool LerFundo(IInspectable* elemento, ComPtr<IUnknown>& fundo)
    {
        ComPtr<WUX::Controls::IPanel> painel;
        if (SUCCEEDED(elemento->QueryInterface(IID_PPV_ARGS(&painel))))
        {
            ComPtr<WUX::Media::IBrush> b;
            painel->get_Background(&b);
            fundo = b;
            return true;
        }

        ComPtr<WUX::Controls::IControl> controle;
        if (SUCCEEDED(elemento->QueryInterface(IID_PPV_ARGS(&controle))))
        {
            ComPtr<WUX::Media::IBrush> b;
            controle->get_Background(&b);
            fundo = b;
            return true;
        }

        ComPtr<WUX::Controls::IBorder> borda;
        if (SUCCEEDED(elemento->QueryInterface(IID_PPV_ARGS(&borda))))
        {
            ComPtr<WUX::Media::IBrush> b;
            borda->get_Background(&b);
            fundo = b;
            return true;
        }

        ComPtr<WUX::Shapes::IShape> forma;
        if (SUCCEEDED(elemento->QueryInterface(IID_PPV_ARGS(&forma))))
        {
            ComPtr<WUX::Media::IBrush> b;
            forma->get_Fill(&b);
            fundo = b;
            return true;
        }

        return false;
    }

    static void EscreverFundo(IInspectable* elemento, IUnknown* fundo)
    {
        ComPtr<WUX::Media::IBrush> pincel;
        if (fundo) fundo->QueryInterface(IID_PPV_ARGS(&pincel));

        ComPtr<WUX::Controls::IPanel> painel;
        if (SUCCEEDED(elemento->QueryInterface(IID_PPV_ARGS(&painel)))) { painel->put_Background(pincel.Get()); return; }

        ComPtr<WUX::Controls::IControl> controle;
        if (SUCCEEDED(elemento->QueryInterface(IID_PPV_ARGS(&controle)))) { controle->put_Background(pincel.Get()); return; }

        ComPtr<WUX::Controls::IBorder> borda;
        if (SUCCEEDED(elemento->QueryInterface(IID_PPV_ARGS(&borda)))) { borda->put_Background(pincel.Get()); return; }

        ComPtr<WUX::Shapes::IShape> forma;
        if (SUCCEEDED(elemento->QueryInterface(IID_PPV_ARGS(&forma)))) { forma->put_Fill(pincel.Get()); return; }
    }

    // ------------------------------------------------------------------ Tap

    Tap::Tap()
    {
        s_instancia = this;
    }

    Tap::~Tap()
    {
        if (s_instancia == this) s_instancia = nullptr;
    }

    Tap* Tap::Instancia() { return s_instancia; }

    HRESULT Tap::QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;

        if (riid == __uuidof(IUnknown) || riid == __uuidof(IObjectWithSite))
            *ppv = static_cast<IObjectWithSite*>(this);
        else if (riid == __uuidof(IVisualTreeServiceCallback) || riid == __uuidof(IVisualTreeServiceCallback2))
            *ppv = static_cast<IVisualTreeServiceCallback2*>(this);
        else
            return E_NOINTERFACE;

        AddRef();
        return S_OK;
    }

    ULONG Tap::AddRef() { return ++_refs; }

    ULONG Tap::Release()
    {
        const ULONG restam = --_refs;
        if (restam == 0) delete this;
        return restam;
    }

    // O XAML nos entrega o IXamlDiagnostics aqui. E o momento de pedir a arvore inteira: o Advise
    // enumera o que ja existe e passa a avisar de cada mudanca dai em diante.
    HRESULT Tap::SetSite(IUnknown* site)
    {
        if (!site)
        {
            if (_servico) _servico->UnadviseVisualTreeChange(this);
            _servico.Reset();
            _diagnostics.Reset();
            _site.Reset();
            return S_OK;
        }

        _site = site;
        if (FAILED(site->QueryInterface(IID_PPV_ARGS(&_diagnostics)))) return E_NOINTERFACE;
        if (FAILED(site->QueryInterface(IID_PPV_ARGS(&_servico)))) return E_NOINTERFACE;

        return _servico->AdviseVisualTreeChange(this);
    }

    HRESULT Tap::GetSite(REFIID riid, void** ppvSite)
    {
        if (!_site) return E_FAIL;
        return _site->QueryInterface(riid, ppvSite);
    }

    // Os nomes vem do dump feito nesta maquina; se um build novo do Windows renomear, o dump e
    // o caminho para descobrir de novo.
    bool Tap::EhAlvo(const wchar_t* tipo, const wchar_t* nome) const
    {
        if (!tipo) return false;

        if (wcscmp(tipo, L"Taskbar.TaskbarFrame") == 0) return true;
        if (wcscmp(tipo, L"Taskbar.TaskbarBackground") == 0) return true;
        if (wcscmp(tipo, L"SystemTray.SystemTrayFrame") == 0) return true;

        // formas nomeadas dentro do TaskbarBackground, nos builds em que o fundo e um retangulo
        if (nome && (wcscmp(nome, L"BackgroundFill") == 0 || wcscmp(nome, L"BackgroundStroke") == 0))
            return true;

        return false;
    }

    void Tap::Registrar(const VisualElement& element, const ParentChildRelation& relation)
    {
        std::lock_guard<std::mutex> guarda(_trava);

        for (const auto& a : _alvos)
            if (a.handle == element.Handle) return;

        Alvo alvo;
        alvo.handle = element.Handle;
        alvo.thread = GetCurrentThreadId();
        alvo.tipo = element.Type ? element.Type : L"";
        _alvos.push_back(std::move(alvo));
        (void)relation;
    }

    HRESULT Tap::OnVisualTreeChange(ParentChildRelation relation, VisualElement element, VisualMutationType mutationType)
    {
        if (mutationType == Add)
        {
            {
                std::lock_guard<std::mutex> guarda(_trava);
                _vistos.push_back({ element.Type ? element.Type : L"", element.Name ? element.Name : L"",
                                    element.Handle, relation.Parent });
            }

            if (EhAlvo(element.Type, element.Name))
            {
                Registrar(element, relation);

                // elemento que nasce depois de o app ter pedido uma cor ja nasce pintado: e assim que
                // a barra continua igual depois de o Explorer refazer partes dela
                Estado estado{};
                if (LerEstado(estado) && estado.modo == static_cast<uint32_t>(Modo::Cor))
                {
                    std::lock_guard<std::mutex> guarda(_trava);
                    for (auto& a : _alvos)
                        if (a.handle == element.Handle) AplicarEm(a, estado);
                }
            }
        }
        else if (mutationType == Remove)
        {
            std::lock_guard<std::mutex> guarda(_trava);
            for (auto it = _alvos.begin(); it != _alvos.end(); ++it)
            {
                if (it->handle == element.Handle) { _alvos.erase(it); break; }
            }
        }

        return S_OK;
    }

    HRESULT Tap::OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR)
    {
        return S_OK;
    }

    // So mexe em elementos da thread atual: propriedade do XAML so aceita a propria thread de UI, e
    // cada barra (principal e secundarias) tem a sua. O app manda o aviso para cada uma.
    void Tap::AplicarEm(Alvo& alvo, const Estado& estado)
    {
        if (alvo.thread != GetCurrentThreadId() || !_diagnostics) return;

        ComPtr<IInspectable> elemento;
        if (FAILED(_diagnostics->GetIInspectableFromHandle(alvo.handle, &elemento)) || !elemento) return;

        if (!alvo.originalCapturado)
        {
            if (!LerFundo(elemento.Get(), alvo.original)) return;   // sem fundo trocavel: ignora
            alvo.originalCapturado = true;
        }

        if (estado.modo == static_cast<uint32_t>(Modo::Cor))
        {
            auto pincel = CriarPincel(estado.argb);
            if (pincel) EscreverFundo(elemento.Get(), pincel.Get());
        }
        else
        {
            EscreverFundo(elemento.Get(), alvo.original.Get());
        }
    }

    void Tap::Aplicar()
    {
        Estado estado{};
        if (!LerEstado(estado)) return;

        if (estado.dump)
        {
            EscreverDump(estado.caminhoDump);
            estado.dump = 0;
            EscreverEstado(estado);
        }

        std::lock_guard<std::mutex> guarda(_trava);
        for (auto& alvo : _alvos) AplicarEm(alvo, estado);
    }

    void Tap::Encerrar()
    {
        Estado devolver{};
        devolver.versao = kVersao;
        devolver.modo = static_cast<uint32_t>(Modo::Devolver);

        {
            std::lock_guard<std::mutex> guarda(_trava);
            for (auto& alvo : _alvos) AplicarEm(alvo, devolver);
        }

        SetSite(nullptr);
    }

    // A arvore inteira, um elemento por linha: tipo, nome, handle, pai. E o mapa para achar como a
    // barra se chama por dentro em cada build do Windows.
    void Tap::EscreverDump(const wchar_t* caminho)
    {
        if (!caminho || !*caminho) return;

        std::wofstream arquivo(caminho, std::ios::out | std::ios::trunc);
        if (!arquivo) return;

        std::lock_guard<std::mutex> guarda(_trava);
        arquivo << L"tipo\tnome\thandle\tpai\tthread=" << GetCurrentThreadId() << L"\n";
        for (const auto& v : _vistos)
            arquivo << v.tipo << L"\t" << v.nome << L"\t" << v.handle << L"\t" << v.pai << L"\n";

        arquivo << L"\n# alvos registrados: " << _alvos.size() << L"\n";
        for (const auto& a : _alvos)
            arquivo << L"# " << a.tipo << L"\t" << a.handle << L"\tthread " << a.thread << L"\n";
    }
}
