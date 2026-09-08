#pragma once
#include <cstdint>

// O que o app (C#) manda para a DLL que roda dentro do Explorer, por memoria compartilhada.
// Espelhado em src/ClaudeIndicator/Core/ExplorerTap.cs: mudar aqui e mudar la.
//
// Memoria compartilhada em vez de mensagens com dados porque o pedido e simples (uma cor, um modo)
// e porque UIPI barra WM_COPYDATA de processo menos privilegiado para mais privilegiado - e o app
// pode estar rodando elevado enquanto o Explorer nao esta. Um mapeamento nomeado nao tem essa
// restricao, e a DLL le quando recebe o aviso.
namespace ClaudeIndicator::Tap
{
    // Nome do mapeamento. "Local\" = da sessao do usuario, que e onde o Explorer dele roda.
    constexpr wchar_t kNomeMemoria[] = L"Local\\ClaudeIndicator.ExplorerTap.v1";

    // Mensagem registrada que o app manda para a thread da barra pedindo releitura.
    constexpr wchar_t kNomeMensagem[] = L"ClaudeIndicator.ExplorerTap.Refresh";

    constexpr uint32_t kVersao = 1;

    enum class Modo : uint32_t
    {
        Devolver = 0,   // tirar o que a DLL colocou e restaurar o fundo original do Windows
        Cor = 1,        // fundo da barra = a cor pedida (alfa incluido: 0 e vidro limpo)
    };

    struct Estado
    {
        uint32_t versao;       // kVersao; a DLL ignora o resto se nao bater
        uint32_t sequencia;    // o app incrementa a cada mudanca; a DLL reaplica quando muda
        uint32_t modo;         // Modo
        uint32_t argb;         // cor do fundo, 0xAARRGGBB
        uint32_t dump;         // 1 = a DLL escreve a arvore visual da barra em kCaminhoDump e zera
        uint32_t reservado[3];
        wchar_t  caminhoDump[260];
    };
}
