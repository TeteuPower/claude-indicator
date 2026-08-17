# Claude Indicator

Indicador de consumo da assinatura Claude para Windows: ícone na barra de tarefas **ou** gadget
flutuante arrastável que fica sempre por cima de qualquer aplicativo.

![ícone](docs/icon-preview.png)

## O que ele mostra

Até três barras, escolhidas na tela de configuração:

| Barra | O que é |
|---|---|
| **Sessão** | janela de 5 horas do seu plano |
| **Semanal** | consumo semanal somando todos os modelos |
| **Fable 5** | consumo semanal do modelo mais avançado (Fable/Opus) |

Cores: verde até o limite de atenção, amarelo a partir dele, vermelho a partir do limite de alerta
(ambos configuráveis, padrão 75% / 90%).

## Baixar pronto

Todo push na `main` compila no GitHub Actions e atualiza a pré-release **latest**, que aparece na
caixa **Releases** da página inicial do repositório — é só clicar nela e baixar o
`ClaudeIndicator-Setup-*.exe` dos assets. Sem login, link sempre no mesmo lugar.

Versões estáveis são marcadas com tag `v*`: o workflow cria a release numerada com o instalador
anexado e a promove a "Latest" na página.

```powershell
git tag v1.2.0
git push origin v1.2.0
```

Cada run também guarda o instalador e o exe portátil como artefatos (**Actions › run › Artifacts**),
úteis para builds de pull request — exigem login e expiram em 90 dias.

## Como compilar

Pré-requisitos: **Windows 10/11 x64** e **.NET SDK 8**.

```powershell
winget install Microsoft.DotNet.SDK.8
# opcional, para gerar o instalador:
winget install JRSoftware.InnoSetup

cd C:\Trabalho\claude-indicator
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Resultado:

- `publish\ClaudeIndicator.exe` — executável único, roda sozinho (portátil, ~150 MB)
- `dist\ClaudeIndicator-Setup-1.2.0.exe` — instalador (só se o Inno Setup estiver instalado)

Variações:

```powershell
.\build.ps1 -Run                  # compila e abre
.\build.ps1 -NoInstaller          # só o exe
.\build.ps1 -FrameworkDependent   # exe de ~2 MB, exige o .NET 8 Desktop Runtime instalado
```

O instalador não pede administrador (instala para o usuário atual) e tem a opção
"iniciar junto com o Windows".

Rodar o instalador com o app já instalado é uma **atualização**: ele fecha a instância que está na
bandeja, reaproveita a pasta e as opções da instalação anterior sem perguntar de novo, troca o
executável e mantém as suas preferências (`%APPDATA%\ClaudeIndicator\settings.json`). Não é preciso
desinstalar antes.

## Login / de onde vêm os dados

Não existe API pública de consumo de assinatura. O app reutiliza o **login que o Claude Code já fez
neste computador**, o mesmo que alimenta o comando `/usage`:

1. Lê `%USERPROFILE%\.claude\.credentials.json` (arquivo do Claude Code — **nunca é alterado**).
2. Se o token estiver expirado, renova via OAuth e guarda a renovação em
   `%APPDATA%\ClaudeIndicator\token-cache.json`.
3. Consulta o endpoint de uso da conta e desenha as barras.

Se você não usa o Claude Code, gere um token e cole em **Configurações › Conta › Informar um token
manualmente**:

```powershell
claude setup-token
```

O app também aceita a variável de ambiente `CLAUDE_CODE_OAUTH_TOKEN`.

### Se as barras não aparecerem

O endpoint de uso é interno da Anthropic e pode mudar de nome ou de formato. Por isso o parser é
tolerante e tudo é ajustável sem recompilar, em **Configurações › Diagnóstico (avançado)**:

- **Endpoints**: uma URL por linha, tentadas em ordem até uma responder 200.
- **Palavras-chave**: como cada barra é localizada dentro do JSON (ex.: `five_hour` → Sessão).
- **Resposta bruta**: o JSON exato que a API devolveu, com o caminho de onde cada barra saiu.

A busca casa a palavra-chave contra o caminho do campo no JSON somado aos rótulos que estiverem em
volta dele, inclusive em objetos aninhados. É assim que o limite semanal por modelo é encontrado: ele
vem em `limits[]` com o nome do modelo em `scope.model.display_name`, e não numa chave própria.

Ou seja: se o formato mudar, basta olhar a resposta bruta e ajustar as palavras-chave.

Quando uma consulta falha (rede, HTTP 429 de limite de consultas etc.), o app **mantém na tela os
últimos valores obtidos** e indica no rodapé do gadget que são dados antigos. No caso do 429 ele
respeita o intervalo pedido pela API (ou espera com backoff) antes de tentar de novo.

## Configurações disponíveis

- **Como exibir**: bandeja, gadget flutuante ou os dois
- **Barras dentro do ícone da bandeja**: verticais (colunas lado a lado) ou horizontais (linhas
  empilhadas) — a horizontal costuma ser mais legível com duas ou três barras
- **Quais barras** mostrar e o rótulo de cada uma
- **Gadget**: disposição das barras (vertical, uma por linha; ou horizontal, lado a lado com
  separador), opacidade, tamanho, sempre por cima, travar posição, mostrar horário de renovação,
  reposicionar no canto inferior direito
- **Conta**: login do Claude Code ou token manual, com botão "Testar conexão"
- **Sistema**: iniciar com o Windows, iniciar sem abrir a janela, intervalo de atualização
  (15 s a 15 min), limites de atenção/alerta e notificação ao atingir o alerta

As preferências ficam em `%APPDATA%\ClaudeIndicator\settings.json`.

## Uso no dia a dia

- **Ícone da bandeja**: as barras são desenhadas no próprio ícone (uma coluna ou uma linha por
  barra, conforme a orientação escolhida; com uma única barra ativa ele mostra a porcentagem).
  Duplo clique abre as configurações; botão direito tem atualizar, configurações, mostrar/ocultar
  gadget e sair.
- **Gadget**: arraste com o botão esquerdo; passe o mouse para ver os botões de atualizar,
  configurar e ocultar; botão direito abre o menu.
- **Histórico**: menu da bandeja ou do gadget › "Histórico de consumo…". Mostra o nível de cada
  barra ao longo do tempo, o consumo por hora (últimas 24 h) ou por dia (7/30 dias) e os totais da
  última hora e das últimas 24 h, em pontos percentuais do limite. O histórico é gravado em
  `%APPDATA%\ClaudeIndicator\history.jsonl` enquanto o app está aberto.

## Estrutura do código

```
src/ClaudeIndicator/
  App.xaml(.cs)          tema escuro + instância única
  Core/
    AppHost.cs           orquestra timer, bandeja, gadget e configurações
    AppSettings.cs       preferências (JSON em %APPDATA%)
    CredentialStore.cs   leitura do login do Claude Code + refresh OAuth
    UsageService.cs      HTTP + parser tolerante do JSON de consumo
    UsageHistory.cs      grava/lê o histórico de consumo (history.jsonl)
    TrayIconRenderer.cs  desenha o ícone da bandeja em tempo real
    StartupManager.cs    inicialização automática (HKCU\...\Run)
  Views/
    GadgetWindow.xaml    gadget transparente, arrastável, sempre por cima
    SettingsWindow.xaml  tela de configuração
    HistoryWindow.xaml   gráficos do histórico (nível e consumo por hora/dia)
    BarRenderer.cs       desenho das barras (gadget e prévia)
```

## Aviso

Projeto pessoal, não oficial e sem vínculo com a Anthropic. Ele apenas lê o consumo da sua própria
conta com o seu próprio token.
