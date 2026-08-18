# Claude Indicator

Indicador de consumo da assinatura Claude para Windows, com painel de análise e três formas de
acompanhar o consumo sem abrir nada — combináveis entre si:

| Onde | O que é |
|---|---|
| **Ícone na bandeja** | as barras desenhadas no próprio ícone, ao lado do relógio |
| **Painel na barra de tarefas** | faixa no espaço livre da barra, com rótulo, porcentagem e barra de cada limite |
| **Gadget flutuante** | janela arrastável que fica por cima dos outros aplicativos |

O painel da barra e o gadget mostram também um **velocímetro do ritmo de consumo** (`0,15% p/min`):
o meio da escala é o ritmo que o limite aguenta até renovar, então ponteiro à esquerda significa
que dá para seguir assim e à direita que vai acabar antes. **Clicar nele troca o limite**
acompanhado, e o rótulo ao lado mostra qual está ativo. A média é calculada sobre uma janela
escolhida em Configurações — 5 min, 20 min, 1 h ou 24 h: curta reage rápido e oscila, longa é
estável e demora a perceber mudança. No ícone da bandeja o ritmo aparece ao passar o mouse — em
16 px não há espaço para desenhá-lo.

Sob cada barra corre um **fio do tempo decorrido** até a renovação. Comparar os dois é a leitura
que interessa: se o fio está à frente do consumo, você gasta mais devagar que o relógio e o limite
chega inteiro até o fim da janela.

A legenda do velocímetro leva a renovação em conta. Só diz "acaba em X" quando o limite realmente
se esgota **antes** de renovar; caso contrário mostra quanto deve sobrar na renovação, que é a
informação que existe. No gadget horizontal aparece um velocímetro por limite, lado a lado, e
clicar em um deles passa a acompanhá-lo nos outros indicadores.

![ícone](docs/icon-preview.png)

## O painel

Uma janela só, com navegação à esquerda:

- **Visão geral** — quanto resta de cada limite, ritmo de consumo (última hora, últimas 24 h),
  projeção até a renovação no ritmo atual, consumo por hora do último dia e os projetos que mais
  gastaram na semana.
- **Histórico** — nível de cada barra ao longo do tempo e consumo por hora ou por dia.
- **Projetos** — repartição do consumo entre os projetos do Claude Code, em cartões, e os prompts
  de cada um.
- **Configurações** — agrupadas por assunto, com a barra de salvar aparecendo só quando há
  alteração pendente.

Abre com duplo clique no ícone da bandeja, clique no painel da barra de tarefas ou pelo menu de
qualquer um dos indicadores.

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
git tag v1.7.6
git push origin v1.7.6
```

Cada run também guarda o instalador e o exe portátil como artefatos (**Actions › run › Artifacts**),
úteis para builds de pull request — exigem login e expiram em 90 dias.

## Atualização pelo próprio app

O app consulta as releases do repositório **ao abrir** e, enquanto fica aberto, a cada 6 horas.
Havendo versão mais nova, avisa no painel e com um balão na bandeja; o botão **Baixar e instalar**
(em **Configurações › Avançado › Atualizações**) pega o instalador anexado à release e roda em modo
silencioso — o instalador fecha o app, troca o executável e o inicia de volta, mantendo suas
preferências. Se a instalação estiver em `Program Files`, o app pede elevação ao Windows, porque em
modo silencioso o instalador não tem como pedir sozinho.

Dois detalhes do GitHub que o app precisa contornar:

- O endpoint `/releases/latest` **ignora pré-releases**, e a build de cada push é exatamente uma
  pré-release. Por isso o app lista as releases e escolhe a maior versão, com uma opção para
  considerar ou não as pré-releases.
- A pré-release usa a tag fixa `latest`, que não é uma versão. A versão sai então do **nome do
  instalador anexado** (`ClaudeIndicator-Setup-1.7.6.exe`) — de propósito antes do título da
  release, porque o instalador é o arquivo que será realmente instalado e o título é texto que
  pode ficar defasado se a chamada que o atualiza falhar.

No workflow, anexar o instalador é o único passo obrigatório: atualizar título e remover os
instaladores antigos são acabamento e apenas registram um aviso quando falham. A API do GitHub tem
devolvido `503` de forma persistente nas chamadas que alteram essa release, e não faz sentido
perder a publicação inteira por causa do texto do título.

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

- `publish\ClaudeIndicator.exe` — executável único, roda sozinho (portátil, ~63 MB)
- `dist\ClaudeIndicator-Setup-1.7.6.exe` — instalador (só se o Inno Setup estiver instalado)

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
tolerante e tudo é ajustável sem recompilar, em **Configurações › Avançado**:

- **Endpoints**: uma URL por linha, tentadas em ordem até uma responder 200.
- **Palavras-chave**: como cada barra é localizada dentro do JSON (ex.: `five_hour` → Sessão).
- **Resposta bruta**: o JSON exato que a API devolveu, com o caminho de onde cada barra saiu.

A busca casa a palavra-chave contra o caminho do campo no JSON somado aos rótulos que estiverem em
volta dele, inclusive em objetos aninhados. É assim que o limite semanal por modelo é encontrado: ele
vem em `limits[]` com o nome do modelo em `scope.model.display_name`, e não numa chave própria.

Ou seja: se o formato mudar, basta olhar a resposta bruta e ajustar as palavras-chave.

Quando uma consulta falha (rede, HTTP 429 de limite de consultas etc.), o app **mantém na tela os
últimos valores obtidos** e indica no rodapé do gadget que são dados antigos.

O arquivo de credenciais é reescrito pelo Claude Code quando ele renova o token, e ler exatamente
nesse instante devolve JSON incompleto — o que aparecia como "credenciais não encontradas" até a
consulta seguinte. A leitura agora tenta de novo antes de desistir e, no pior caso, reaproveita a
última leitura boa. Uma falha também devolve o intervalo de consulta ao valor base, senão ela
poderia ficar visível pelos dez minutos do espaçamento máximo.

### Sobre o HTTP 429

O limite de consultas é **da conta**, não do app: cada sessão do Claude Code aberta consulta o mesmo
endpoint de uso. Com várias sessões abertas, um intervalo curto no indicador estoura o limite mesmo
que o app sozinho pareça comportado. O endpoint não devolve cabeçalhos de rate-limit, então não há
como saber o teto — a única saída é consultar menos.

Por isso o intervalo escolhido nas configurações é um **mínimo**, e não uma cadência fixa:

- Enquanto o consumo não muda, o app espaça sozinho as consultas (`intervalo × (1 + rodadas
  paradas)`, até 10 minutos) e volta ao intervalo escolhido assim que algo muda. Ocioso, isso leva
  60 consultas/hora para 11.
- Depois de um 429 ele espera 5, 10 ou 15 minutos e só volta ao ritmo normal após três consultas
  bem-sucedidas seguidas — voltar na primeira é o caminho de bater no limite de novo.
- O mínimo aceito é 60 s.

## Configurações disponíveis

- **Onde exibir**: bandeja, painel na barra de tarefas e gadget — cada um liga e desliga sozinho
- **Ícone da bandeja**: barras verticais (colunas lado a lado) ou horizontais (linhas empilhadas) —
  a horizontal costuma ser mais legível com duas ou três barras
- **Painel na barra de tarefas**: posição (à esquerda ou junto ao relógio), distância da borda,
  tamanho e opacidade do fundo
- **Quais barras** mostrar e o rótulo de cada uma
- **Gadget**: disposição das barras (vertical, uma por linha; ou horizontal, lado a lado com
  separador), opacidade, tamanho, sempre por cima, travar posição, mostrar horário de renovação,
  reposicionar no canto inferior direito
- **Ritmo**: velocímetro no painel da barra e/ou no gadget, de qual limite ele acompanha, a janela
  da média (5 min a 24 h) e o fio do tempo decorrido sob as barras
- **Histórico de consumo**: guardar tudo (padrão) ou apagar registros com mais de N dias
- **Atualizações**: procurar versão nova no GitHub automaticamente, repositório consultado, e
  botão para baixar e instalar sem sair do app
- **Conta**: login do Claude Code ou token manual, com botão "Testar conexão"
- **Sistema**: iniciar com o Windows, iniciar sem abrir a janela e intervalo mínimo entre consultas
  (60 s a 15 min — veja a seção sobre HTTP 429)
- **Barras**: limites de atenção/alerta, notificação ao atingir o alerta e prévia ao vivo

As categorias ficam em abas: **Onde exibir · Barras · Ritmo · Conta · Sistema · Dados · Avançado**.
A barra de salvar só aparece quando existe alteração pendente, e "Descartar" volta tudo ao que está
gravado.

As preferências ficam em `%APPDATA%\ClaudeIndicator\settings.json`.

## Uso no dia a dia

- **Ícone da bandeja**: as barras são desenhadas no próprio ícone (uma coluna ou uma linha por
  barra, conforme a orientação escolhida; com uma única barra ativa ele mostra a porcentagem).
  Passar o mouse mostra as porcentagens e o ritmo de consumo. Duplo clique abre o painel; botão
  direito tem atualizar, as seções do painel, mostrar/ocultar gadget e sair.
- **Painel na barra de tarefas**: clique abre o painel; botão direito tem o mesmo menu, incluindo
  ocultá-lo. Passar o mouse sobre cada indicador mostra quanto resta e quando renova.
- **Gadget**: arraste com o botão esquerdo; passe o mouse para ver os botões de atualizar,
  configurar e ocultar; botão direito abre o menu. No rodapé, uma **linha do tempo das últimas dez
  consultas à API**: verde respondeu, âmbar recusou por limite (HTTP 429), vermelho falhou, e os
  pontos apagados são consultas que ainda não aconteceram. A mais recente fica à direita e empurra
  a mais antiga para fora; o tooltip lista as dez com horário e motivo. Só entram consultas que
  realmente aconteceram — enquanto o app espera o fim de uma pausa por limite, nada é registrado.

O **histórico** é gravado em `%APPDATA%\ClaudeIndicator\history.jsonl` enquanto o app está aberto e,
por padrão, **nada é apagado** — dá para ligar uma retenção em *Configurações › Dados*. Não há como
importar consumo anterior: a API devolve só o estado atual dos limites, sem série histórica, então o
gráfico começa vazio e enche a partir do primeiro uso.

## Consumo por projeto

A API informa **porcentagem do limite**; ela não diz em que você gastou. Quem sabe disso são as
transcrições que o Claude Code guarda em `%USERPROFILE%\.claude\projects\**\*.jsonl`: cada resposta
traz `message.usage` (tokens de entrada, saída e cache), `message.model` e o `cwd` do projeto.

O app lê esses arquivos e reparte o consumo:

- **Fatia do consumo do período** de cada projeto, pelo custo estimado dos tokens: somadas dão
  100%. É a mesma régua dos prompts, então "este projeto foi 27% do que gastei" e "este prompt foi
  0,8%" se comparam direto.
- **Só Fable 5**, filtrando pelos modelos que contam no limite próprio dele.
- **Prompts de cada projeto**, com horário, texto e a fatia do consumo que cada um disparou —
  ordenáveis por mais recentes ou mais caros.

Duas unidades convivem no app e não devem ser confundidas: em **Projetos** as porcentagens são do
*consumo* (quanto do que você gastou foi ali); em **Visão geral** e **Histórico** são do *limite*
(quanto da sua cota foi consumido no intervalo).

Os arquivos só crescem no fim, então o índice guarda o offset já lido de cada um: a primeira
varredura leva alguns segundos (~3 s para 280 MB) e as seguintes, milissegundos. O texto dos prompts
não é copiado para o índice — fica só o arquivo e o offset, e a linha é lida quando a tela precisa.
Nada sai da máquina.

Três coisas que o número **não** é:

1. **A repartição é proporcional, não medida.** A conversão de tokens para porcentagem do limite não
   é publicada, então usamos pesos aproximados (ajustáveis em Configurações › Diagnóstico). Eles
   mudam as fatias, nunca o total.
2. **O que você consome fora do Claude Code** (claude.ai, outra máquina, outro app) não está nas
   transcrições e acaba diluído entre os projetos.
3. **Turnos de subagentes** entram no projeto onde rodaram. Um projeto pode aparecer com consumo e
   nenhum prompt digitado quando o prompt de origem está em outro.

O caminho mostrado é o `cwd` de quando o consumo aconteceu, então projeto movido ou renomeado
aparece com o caminho antigo. Nesse caso o app procura para onde ele foi: candidatos são pastas
cujo caminho **termina igual** ao antigo, e o desempate usa as subpastas que o projeto
comprovadamente tinha (os subprojetos registrados no próprio índice). Havendo vencedor isolado, o
cartão mostra "hoje em: …"; havendo empate, ele diz só que a pasta não existe mais — casar pastas
no chute atribuiria consumo ao projeto errado, o que é pior que um caminho antigo declarado como
antigo.

Clicar num cartão filtra os prompts daquele projeto; clicar nele de novo, ou no espaço vazio ao
lado dos cartões, volta para os prompts recentes de **todos** os projetos. Cada prompt abre em uma
janela com o texto completo, a fatia do consumo e os tokens do turno que ele disparou.

Duas armadilhas do formato que o parser trata, e que sem tratamento dobrariam os números: sessões
retomadas **copiam o histórico** para o arquivo novo (48% de turnos repetidos no acervo testado, por
isso a deduplicação por `uuid`), e um mesmo `requestId` emite vários registros **repetindo o mesmo
`usage`** (vale um por request, o maior).

## Estrutura do código

O painel na barra de tarefas merece uma nota: o Windows 11 **removeu o suporte a deskbands**, as
antigas barras de ferramentas que podiam ser embutidas na barra de tarefas. Não existe API para
colocar um componente lá dentro. O que o app faz é posicionar uma janela sem borda sobre o espaço
livre da barra (à esquerda do botão Iniciar, ou entre os ícones e o relógio), acompanhando mudanças
de tamanho, posição e DPI, e se escondendo quando um aplicativo em tela cheia está na frente.

```
src/ClaudeIndicator/
  App.xaml(.cs)          tema escuro, estilos do painel + instância única
  Core/
    AppHost.cs           orquestra timer, bandeja, painel da barra, gadget e janela
    AppSettings.cs       preferências (JSON em %APPDATA%)
    CredentialStore.cs   leitura do login do Claude Code + refresh OAuth
    UsageService.cs      HTTP + parser tolerante do JSON de consumo
    UsageHistory.cs      grava/lê o histórico de consumo (history.jsonl)
    TranscriptIndex.cs   índice incremental das transcrições do Claude Code
    TrayIconRenderer.cs  desenha o ícone da bandeja em tempo real
    TaskbarInfo.cs       geometria da barra de tarefas e espaço livre nela
    StartupManager.cs    inicialização automática (HKCU\...\Run)
    AppInfo.cs           versão exibida na interface
  Views/
    MainWindow.xaml      painel: navegação lateral + página escolhida
    GadgetWindow.xaml    gadget transparente, arrastável, sempre por cima
    TaskbarBarWindow.xaml  faixa ancorada no espaço livre da barra de tarefas
    BarRenderer.cs       desenho das barras (gadget e prévia)
    Pages/
      OverviewPage.xaml  visão geral: restante, ritmo, projeção e top projetos
      HistoryPage.xaml   gráficos do histórico (nível e consumo por hora/dia)
      ProjectsPage.xaml  consumo por projeto e prompts de cada um
      SettingsPage.xaml  configurações por categoria, com salvar sob demanda
```

## Aviso

Projeto pessoal, não oficial e sem vínculo com a Anthropic. Ele apenas lê o consumo da sua própria
conta com o seu próprio token.

