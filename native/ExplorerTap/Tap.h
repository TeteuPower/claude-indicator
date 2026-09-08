#pragma once

#include <windows.h>
#include <ocidl.h>
#include <XamlOM.h>
#include <wrl/client.h>
#include <atomic>
#include <mutex>
#include <string>
#include <vector>

#include "Shared.h"

namespace ClaudeIndicator::Tap
{
    // CLSID do TAP. E o que o XAML pede ao nosso DllGetClassObject quando o
    // InitializeXamlDiagnosticsEx e chamado - qualquer GUID serve, desde que bata dos dois lados.
    // {5A8D4F6B-7C21-4D8E-9F3A-2B6C1E0D4A97}
    extern const CLSID CLSID_Tap;

    // Um elemento da barra cujo fundo a gente troca. Guardamos o fundo ORIGINAL para poder devolver:
    // efeito deixado para tras numa DLL que morreu so sai reiniciando o Explorer.
    struct Alvo
    {
        InstanceHandle handle = 0;
        DWORD thread = 0;                             // a thread de UI do elemento
        std::wstring tipo;
        Microsoft::WRL::ComPtr<IUnknown> original;    // o IBrush original (pode ser nulo)
        bool originalCapturado = false;
    };

    // O "tap" no XAML da barra. O XAML cria este objeto (via DllGetClassObject) e chama SetSite com
    // o IXamlDiagnostics; a partir dai recebemos a arvore visual inteira e cada mudanca nela.
    class Tap final : public IObjectWithSite, public IVisualTreeServiceCallback2
    {
    public:
        Tap();

        // IUnknown
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override;
        ULONG STDMETHODCALLTYPE AddRef() override;
        ULONG STDMETHODCALLTYPE Release() override;

        // IObjectWithSite
        HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override;
        HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppvSite) override;

        // IVisualTreeServiceCallback / 2
        HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation relation, VisualElement element,
                                                     VisualMutationType mutationType) override;
        HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle element, VisualElementState elementState,
                                                        LPCWSTR context) override;

        // Chamado pelo gancho, na thread da barra, quando o app avisa que mudou algo.
        void Aplicar();

        // Devolve tudo e para de escutar. Chamado antes de o app desligar.
        void Encerrar();

        static Tap* Instancia();

    private:
        ~Tap();

        bool EhAlvo(const wchar_t* tipo, const wchar_t* nome) const;
        void AplicarEm(Alvo& alvo, const Estado& estado);
        void Registrar(const VisualElement& element, const ParentChildRelation& relation);
        void EscreverDump(const wchar_t* caminho);

        std::atomic<ULONG> _refs{ 1 };
        Microsoft::WRL::ComPtr<IUnknown> _site;
        Microsoft::WRL::ComPtr<IXamlDiagnostics> _diagnostics;
        Microsoft::WRL::ComPtr<IVisualTreeService3> _servico;

        std::mutex _trava;
        std::vector<Alvo> _alvos;

        // registro de tudo que passou pela arvore, para o dump: e assim que se descobre, em cada
        // build do Windows, como a barra se chama por dentro
        struct Visto { std::wstring tipo, nome; InstanceHandle handle; InstanceHandle pai; };
        std::vector<Visto> _vistos;

        uint32_t _ultimaSequencia = 0xFFFFFFFF;

        static Tap* s_instancia;
    };

    // Le o estado compartilhado. Devolve false se o app nunca criou a memoria.
    bool LerEstado(Estado& estado);

    // Escreve de volta (usado para zerar o pedido de dump depois de atende-lo).
    void EscreverEstado(const Estado& estado);
}
