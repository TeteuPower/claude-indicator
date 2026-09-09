# Claude Indicator

Indicador de consumo da assinatura Claude para Windows, com painel de análise e três formas de
acompanhar o consumo sem abrir nada — combináveis entre si:

| Onde | O que é |
|---|---|
| **Ícone na bandeja** | as barras desenhadas no próprio ícone, ao lado do relógio |
| **Painel na barra de tarefas** | faixa no espaço livre da barra, com rótulo, porcentagem e barra de cada limite |
| **Gadget flutuante** | janela arrastável que fica por cima dos outros aplicativos |

E, opcionalmente, um **segundo painel na barra com os sensores do computador** — CPU, GPU e
memória —, ancorado no lado oposto ao da IA. A seção *Sensores do computador* explica o que cada
um entrega e por que a CPU precisa de elevação.

O painel da barra e o gadget mostram também um **velocímetro do ritmo de consumo** (`0,15% p/min`):
o meio da escala é o ritmo que o limite aguenta até renovar, então ponteiro à esquerda significa
que dá para seguir assim e à direita que vai acabar antes. **Clicar nele troca o limite**
acompanhado, e o rótulo ao lado mostra qual está ativo. A média é calculada sobre uma janela
escolhida em Configurações — 5 min, 20 min, 1 h ou 24 h: curta reage rápido e oscila, longa é
estável e demora a perceber mudança. No ícone da bandeja o ritmo aparece ao passar o mouse — em
16 px não há espaço para desenhá-lo.

Cada barra tem uma **marca do tempo decorrido** até a renovação, atravessando o próprio trilho.
Comparar os dois é a leitura que interessa: preenchimento **antes** da marca é consumo mais devagar
que o relógio, e o limite chega inteiro ao fim da janela; preenchimento **além** dela é consumo
adiantado.

Antes isso era um segundo fio, mais fino, logo abaixo da barra — e ninguém achava onde ele estava:
cinza sobre cinza, 2 px de altura, competindo com a barra de verdade. Marcar dentro do trilho
resolveu as duas coisas de uma vez, visibilidade e comparação.

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
git tag v2.1.0
git push origin v2.1.0
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
  instalador anexado** (`ClaudeIndicator-Setup-2.1.0.exe`) — de propósito antes do título da
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
- `dist\ClaudeIndicator-Setup-2.1.0.exe` — instalador (só se o Inno Setup estiver instalado)

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

Não existe API pública de consumo de assinatura. São três fontes possíveis, escolhidas em
**Configurações › Conta**:

**1. O login que o Claude Code já fez neste computador** (padrão), o mesmo que alimenta o `/usage`:

1. Lê `%USERPROFILE%\.claude\.credentials.json` (arquivo do Claude Code — **nunca é alterado**).
2. Se o token estiver expirado, renova via OAuth e guarda a renovação em
   `%APPDATA%\ClaudeIndicator\token-cache.json`.
3. Consulta o endpoint de uso da conta e desenha as barras.

**2. Entrar com a conta Claude pela própria interface**, para quem não tem o Claude Code aqui: o
botão abre o site do Claude, você autoriza lá e cola o código de volta na tela.

1. O app monta a URL de autorização com PKCE (S256) e o mesmo `client_id` público do
   `claude setup-token`, pedindo os escopos `user:inference user:profile` — sem `user:profile` o
   endpoint de uso responde 403.
2. O redirect é a página oficial de código do console da Anthropic, que **exibe** o `code#state`.
   É por isso que o app não precisa abrir porta nenhuma para receber callback.
3. O que você colar (o código ou a URL inteira) é trocado pelo token, guardado em
   `%APPDATA%\ClaudeIndicator\login.json` e renovado sozinho pelo `refresh_token`.

O token fica só neste computador, e "Sair desta conta" apaga o arquivo.

**3. Um token colado à mão**, em **Configurações › Conta › Informar um token manualmente**:

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
última leitura boa.

### Sobre o HTTP 429

O limite de consultas é **da conta**, não do app: cada sessão do Claude Code aberta consulta o mesmo
endpoint de uso. Com várias sessões abertas, um intervalo curto no indicador estoura o limite mesmo
que o app sozinho pareça comportado. O endpoint não devolve cabeçalhos de rate-limit, então não há
como saber o teto — a única saída é consultar menos.

O intervalo escolhido nas configurações é a **cadência**, e o app a cumpre ao pé da letra: uma
consulta por intervalo, sempre — mesmo com o consumo parado, mesmo depois de erro.

Fechar e reabrir **não recomeça a contagem**. A última leitura boa e os últimos ciclos ficam
guardados em `session.json`; ao abrir, as barras já nascem preenchidas e a faixa de bolinhas
continua de onde parou. O relógio é adiantado para completar o ciclo que estava em curso, em vez de
começar um novo: reabrir trinta segundos depois de fechar dispara a consulta seguinte trinta
segundos depois, e não na hora. O consumo de trinta segundos atrás continua sendo o consumo, e o
limite de consultas é da conta inteira — não há nada a ganhar perguntando de novo.

Duas guardas nesse retrato: leitura com mais de duas horas é descartada (a sessão pode ter renovado
várias vezes desde então, e mostrar o número velho seria pior que mostrar "carregando"), e limite
cujo horário de renovação já passou não volta com a porcentagem antiga. Um relógio só dispara a consulta e desenha o ponto
da linha do tempo, então os dois nunca discordam.

Houve uma versão em que o app espaçava sozinho as consultas enquanto o consumo não mudava, até 10
minutos. Economizava chamadas, mas tornava o indicador imprevisível — a linha do tempo parava de
querer dizer "uma consulta por intervalo" e não dava mais para saber, olhando, se a conexão estava
de pé. Cadência fixa vale mais que a economia.

**Erro não gera espera.** Houve uma versão em que um 429 fazia o app pausar 5, 10 ou 15 minutos e só
voltar ao ritmo depois de três consultas boas seguidas. Na prática, isso fazia o indicador parecer
ter desistido: o consumo congelava na tela e a única saída era clicar em "Atualizar" — que, na
maioria das vezes, funcionava na primeira tentativa, justamente porque a pausa era autoimposta e não
uma recusa da API. A pausa saiu. Agora todo ciclo consulta, deu erro ou não, e todo ciclo desenha
o seu ponto.

O que sobrou é só a guarda contra atropelo: uma consulta em voo não é duplicada pelo ciclo seguinte —
esse ciclo vira ponto apagado. O mínimo aceito é 60 s e, se o 429 insistir, o remédio é aumentar o
intervalo: decisão de quem usa, não uma punição que o app aplica sozinho.

## Configurações disponíveis

- **Onde exibir**: bandeja, painel na barra de tarefas e gadget — cada um liga e desliga sozinho
- **Ícone da bandeja**: barras verticais (colunas lado a lado) ou horizontais (linhas empilhadas) —
  a horizontal costuma ser mais legível com duas ou três barras
- **Painel na barra de tarefas**: tela (automático ou um monitor específico), posição (à esquerda ou
  junto ao relógio), distância da borda, tamanho e opacidade do fundo
- **Quais barras** mostrar e o rótulo de cada uma
- **Gadget**: disposição das barras (vertical, uma por linha; ou horizontal, lado a lado com
  separador), opacidade, tamanho, sempre por cima, travar posição, mostrar horário de renovação,
  reposicionar no canto inferior direito, e o que mais aparece nele — **CPU, GPU e memória** e o
  **velocímetro do ritmo**, cada um com seu interruptor
  - o bloco de sensores usa os mesmos componentes escolhidos para o painel do computador, e ligá-lo
    liga a leitura de sensores mesmo com aquele painel desligado
  - o interruptor do velocímetro é o mesmo que existe em *Ritmo*: mexer em um mexe no outro
- **Barra própria**: mostrar, borda (topo, esquerda ou direita), tela, reservar espaço na área útil,
  ficar sempre por cima, sair da frente de jogo em tela cheia, espessura, transparência e fundo
  fosco (desfoque do Windows),
  tamanho do conteúdo (com botão de restaurar padrões) e, para cada painel, se ele aparece nela e em
  qual lado — começo ou fim da barra, independente do lado que ele usa na barra do Windows
- **Disco**: indicador de disco ao lado da memória, e qual disco ele acompanha (um específico ou o
  somatório de todos)
- **Barra do Windows**: efeito (não mexer, transparente, desfocada, fosca ou opaca), tom do efeito e
  se a barra própria segue o mesmo estilo
- **Janelas**: deixar janelas de outros programas translúcidas, a opacidade (25% a 100%), o atalho
  que liga e desliga na janela em foco e a lista de aplicativos que ficam translúcidos sozinhos
- **Ritmo**: velocímetro no painel da barra e/ou no gadget, de qual limite ele acompanha, a janela
  da média (5 min a 24 h) e a marca do tempo decorrido nas barras
- **Histórico de consumo**: guardar tudo (padrão) ou apagar registros com mais de N dias
- **Atualizações**: procurar versão nova no GitHub automaticamente, repositório consultado, e
  botão para baixar e instalar sem sair do app
- **Conta**: login do Claude Code, entrar com a conta Claude pela própria interface (abre o
  site, você cola o código de volta) ou token manual, com botão "Testar conexão"
- **Sistema**: iniciar com o Windows, iniciar sem abrir a janela e intervalo mínimo entre consultas

As configurações são navegadas por um trilho lateral agrupado por assunto — **Claude** (barras,
ritmo, conta), **Computador** (painéis, janelas, no jogo) e **Aplicativo** (sistema, dados, avançado) — em
vez de uma fileira de abas genéricas — a barra própria fica em *Computador*. A troca de painel
desliza suavemente e volta ao topo, porque
cada painel é um assunto novo.

O painel **Desempenho do PC**, na navegação principal, mostra uso, temperatura e watts ao longo do
tempo (30 min a 7 dias), com resumo mín/média/máx por componente. O histórico é gravado num ponto
a cada dez segundos enquanto os sensores estiverem ligados, com retenção de 14 dias. Escalas
honestas: uso sempre 0–100%, temperatura sempre até 105 °C; lacunas na coleta quebram a linha em
vez de atravessar o buraco.
  (60 s a 15 min — veja a seção sobre HTTP 429)
- **Barras**: limites de atenção/alerta, notificação ao atingir o alerta e prévia ao vivo

As categorias ficam em abas: **Onde exibir · Barras · Ritmo · Conta · Sistema · Dados · Avançado**.
A barra de salvar só aparece quando existe alteração pendente, e "Descartar" volta tudo ao que está
gravado.

As preferências ficam em `%APPDATA%\ClaudeIndicator\settings.json`.

## Barra própria

Uma **barra de tarefas sua**, encostada numa borda que o Windows deixou livre — topo, esquerda ou
direita — com os painéis do app dentro. O rodapé não é oferecido de propósito: lá já está a barra do
Windows, e duas barras na mesma borda só reservam o dobro de espaço.

O que a torna uma barra de verdade, e não uma janela encostada na borda, é o registro como
**barra de aplicativo** (`SHAppBarMessage`) — a mesma API que a barra do Windows usa e que os docks
antigos usavam:

- **Reservar espaço na tela**: o shell tira aquela faixa da área útil e janela maximizada para nela,
  em vez de passar por baixo.
- **Ficar sempre por cima**: mantém a barra na frente de quem for arrastado para cima dela.

Os dois são independentes e podem valer ao mesmo tempo — que é justamente como a barra do Windows se
comporta. Só o segundo, e a barra flutua sem mexer na área útil; só o primeiro, e ela cede a frente
para janelas soltas mas continua com o seu espaço garantido.

Também dá para escolher a **tela** (qualquer uma — a barra própria não depende de haver barra do
Windows naquele monitor), a **espessura** (guardada em separado para a barra em pé e a deitada), a
**transparência do fundo** (0% deixa só o conteúdo, sobre o papel de parede) e o **tamanho do
conteúdo**.

O **fundo fosco** liga o desfoque acrílico do compositor do Windows atrás da barra — o mesmo efeito
da barra de tarefas do sistema. Ele fica *atrás* do fundo: o controle de transparência continua
sendo o tom da barra, agora sobre vidro, e é por isso que ligar o fosco baixa o tom junto se ele
estiver alto (em 100% o tom cobriria o efeito por completo). Duas coisas que o código precisa
garantir:

1. **Fosco e transparência por pixel do WPF são exclusivos.** Com `AllowsTransparency=True` o WPF
   torna a janela *layered* e a desenha inteira por conta própria; aí o compositor não tem onde
   compor o desfoque. Como isso se decide antes de a janela existir, trocar a preferência **recria**
   a barra.
2. **O tom vai no pedido ao compositor, não pintado pela janela.** Testado em três janelas lado a
   lado: acrílico com o tom no pedido dá vidro escuro; acrílico sem tom, com o tom pintado por cima
   pelo WPF, dá **preto**. O motivo é o mesmo do item anterior — a janela não é *layered*, então o
   alfa que ela desenha não tem o que compor.
3. **O Windows diz "sim" mesmo quando não compõe nada.** Aconteceu numa tela secundária: pedido
   aceito, efeito nenhum, barra preta. Não há como perguntar isso à API, então o app **olha o
   resultado**: vidro sobre qualquer fundo produz pelo menos o tom da barra, nunca preto puro, e
   preto puro em toda a amostra só acontece quando o efeito não entrou. Nesse caso o fundo volta a
   ser opaco e a tela de configurações passa a dizer que o Windows recusou — barra escura com
   explicação é melhor que barra preta sem nenhuma. A conferência ainda compara com uma referência
   ao lado da barra: existe display cuja captura vem preta com o conteúdo aparecendo na tela, e aí a
   conclusão seria sobre a captura, não sobre o efeito.

### Os painéis dentro dela

O **painel da assinatura** (limites), o **painel do computador** (sensores) e o **velocímetro do
ritmo** têm interruptor próprio na barra: os dois lugares convivem, e o mesmo painel pode estar na
barra própria e no espaço livre da barra do Windows ao mesmo tempo — numa tela cada. Quais limites e
quais sensores entram continua sendo o que está escolhido em *Barras* e no painel do computador; o
que se decide aqui é onde os blocos aparecem.

Cada painel escolhe também o seu **lado**: começo ou fim da barra. Na barra deitada isso é esquerda
e direita; na em pé, topo e rodapé. Dá para deixar os limites do Claude numa ponta e os sensores na
outra, e esse lado é separado do que o painel usa na barra do Windows, justamente porque os dois
podem estar valendo juntos. O botão de trocar o tema do Windows vem junto com os sensores, como
vem lá.

### Em pé, os indicadores também ficam em pé

Barra vertical pede medidor vertical. Cada limite e cada sensor vira uma **coluna que enche de baixo
para cima**, com o rótulo em cima e a porcentagem embaixo, e as colunas ficam **empilhadas**,
dividindo toda a altura que a barra tem para dar — trilhos deitados, um sobre o outro, gastariam a
altura e desperdiçariam a largura, que é o contrário do que uma faixa estreita e alta tem de sobra.
O velocímetro e o botão do tema ficam numa linha de tamanho próprio no fim do bloco, para não
valerem uma coluna cada.

Do medidor em pé, o que é **novo é só a orientação**: o texto com contorno, a trilha escura com
borda clara, a régua de cor e a marca de tempo são os mesmos das células deitadas — o desenho mora
num lugar só (`PanelStyle`), e o painel que aparece na barra própria é o mesmo painel, não um
parecido. Duas cópias do mesmo elemento divergem, e aqui divergir tem consequência: o usuário
reconhece o painel pelo desenho.

Nessas colunas o horário de renovação não vira texto: ele já está na marca que atravessa o trilho na
altura do tempo decorrido, dizendo a mesma coisa sem ocupar linha nenhuma (e "reseta em 6d 4h" não
caberia numa coluna de 60 px). O número exato continua no balão. A marca acima do enchimento
significa limite sobrando; abaixo, consumo correndo na frente do relógio.

A régua de cor dos sensores é **recortada no valor lido**: no trilho deitado o gradiente é medido
sobre a trilha inteira, então o preenchimento mostra só o pedaço da régua que alcançou. Em pé a
trilha estica com a barra e não há largura absoluta para medir — sem o recorte, todo sensor
apareceria verde embaixo e vermelho em cima, mesmo a 20%.

**CPU e GPU ganham o termômetro ao lado do trilho de uso**, o mesmo do indicador no jogo. São duas
perguntas diferentes e por isso duas formas diferentes: o trilho diz quanto do total está em uso, o
termômetro diz quão perto do limite físico a peça está — e ficam juntos porque a pergunta que se faz
é sobre um componente ("como está a GPU?"), não sobre uma grandeza. A régua do termômetro não é a de
carga: 50% de uso é meio caminho e sai amarelo, 50 °C é temperatura confortável e sai verde. Sem
leitura de temperatura o termômetro não é desenhado em vez de aparecer vazio — é o caso da RAM, que
não tem sensor, e da CPU sem elevação.

Uma **linha separa os dois painéis**: são assuntos diferentes — assinatura e computador — e sem ela
as colunas viram uma lista só, onde a Fable 5 e a CPU parecem do mesmo grupo. E a espessura mínima
da barra em pé é de **72 unidades**: abaixo de 110 ela entra em modo compacto — fontes um degrau
abaixo, vãos e bolinhas menores — e o piso passa a ser o rótulo, "Semanal", o elemento mais largo
que sobra. Coluna sem nome não diz de que limite ela fala, então é aí que a barra para de encolher.

A **linha do tempo das consultas desce pela lateral direita**, na altura das três colunas de limite,
em vez de correr no rodapé. Aproveita a largura que sobra numa faixa alta, e mantém as bolinhas
junto do dado que elas explicam: cada uma é uma consulta que trouxe (ou não) aqueles números. No
rodapé, depois de um bloco de sensores, a faixa parecia falar da CPU.

A barra deitada segue com as células lado a lado, no mesmo desenho do painel da barra de tarefas.
Atalhos de aplicativos ficam para depois.

Duas coisas que o código garante, e que faltando quebram a experiência de forma difícil de entender:

1. **Toda medida é em pixels de tela.** O shell fala em pixels e a espessura é escolhida em unidades
   de tela: 46 unidades viram 46 px no monitor de 100% e 80 px no de 175%, mantendo a mesma altura
   aparente nos dois.
2. **Quem reserva, devolve.** Uma faixa reservada por uma janela que morreu fica presa até o
   Explorer reiniciar. A remoção acontece ao fechar a barra, ao sair do app e no encerramento do
   processo — e desligar a barra fecha a janela, em vez de apenas escondê-la.

## A barra do Windows

O app deixa a **barra de tarefas do Windows** translúcida — o que o TranslucentTB faz —, agora pelo
mesmo caminho que ele usa. Cinco opções em *Configurações › Barra do Windows*: não mexer,
transparente, desfocada, fosca e opaca, com um tom ajustável por cima (que não vale na opaca — opaca
com tom pela metade não seria opaca). Aplica nas barras de **todas as telas**, reafirma a cada 2 s,
reengancha quando o shell recria a barra e devolve tudo ao sair.

### São duas metades, e só juntas funcionam

Custou várias rodadas descobrir. No Windows 11 a barra é XAML, e são **duas** coisas:

1. O **acento** na janela da barra (`SetWindowCompositionAttribute` — transparente, desfoque,
   acrílico). Sozinho não muda nada: o XAML da barra pinta o próprio fundo por cima.
2. O **tap**: uma DLL nativa dentro do Explorer que deixa esse fundo XAML transparente. Sozinha,
   deixa a barra **preta** — sem fundo, o compositor mostra preto.

Com as duas, o fundo XAML sai da frente e o acento aparece. Medido nesta máquina (build 26200), a
faixa da barra contra o papel de parede logo acima:

| estado | cor da barra |
|---|---|
| nativo | (34,36,36) |
| só o tap (XAML transparente) | (3,4,5) — preto |
| tap + acrílico na raiz | (48,41,90) — vidro |
| tap + transparente na raiz | (89,79,189) — o mais aberto |

O acento vai na **raiz** `Shell_TrayWnd`, não na ilha XAML filha (`DesktopWindowContentBridge`) — na
filha o resultado volta a preto. E a ordem importa: acento **antes** de transparentizar o XAML, senão
há um instante de barra preta ao ligar. Confirmado que o vidro se mantém com a reafirmação de 2 s (o
Explorer desfaz o acento sozinho em alguns segundos) e que o `Restore` devolve o fundo original.

### A DLL nativa (`native/ExplorerTap`)

É um projeto C++ à parte, embutido no executável e extraído para `%APPDATA%` em tempo de execução. Um
gancho de mensagens (`SetWindowsHookEx`) na thread de cada barra carrega a DLL no Explorer; ela liga
o diagnóstico do XAML (`InitializeXamlDiagnosticsEx`), recebe a árvore visual da barra e troca o
fundo dos elementos-alvo, guardando o original para devolver. App e DLL conversam por memória
compartilhada mais uma mensagem registrada (não `WM_COPYDATA`: o app pode estar elevado e a UIPI
barraria os dados de baixo para cima).

Duas consequências de mexer no Explorer, ditas sem rodeio: a DLL fica no processo dele até o próximo
reinício (descarregá-la com o XAML ainda apontando para ela derrubaria o Explorer), e injeção por
gancho é padrão de coisa maliciosa — então **o antivírus pode reclamar**, como já reclama da medição
de FPS. É o preço de fazer o que o TranslucentTB faz, dentro do próprio app. Sem o compilador C++ no
build, a DLL não é embutida e a tela avisa que a barra do Windows fica indisponível.

## Janelas translúcidas

O efeito daqueles utilitários de antigamente (Glass2k e parentes): a janela **inteira** de outro
programa fica translúcida — conteúdo incluído — e dá para ver o que está atrás dela. É diferente do
vidro da barra, onde só o fundo desfoca e o texto continua nítido; aqui o conteúdo desbota junto, e
por isso a opacidade é ajustável. A 100% de transparência a janela vira um fantasma inutilizável.

### Dois modos, para dois problemas diferentes

**Só o fundo** (padrão) deixa passar apenas o fundo da janela; texto, ícones e miniaturas continuam
nítidos. É o que se pede quando se diz "quero o Explorador transparente".

**A janela inteira** desbota tudo junto, conteúdo incluído, com opacidade ajustável. É o efeito dos
utilitários antigos, e vale em qualquer programa.

Duas formas de escolher em quem vale, e elas se somam:

- **Atalho** (padrão `Ctrl+Alt+T`): liga e desliga na janela que estiver na frente, sem gravar nada.
- **Lista de aplicativos**: os que estiverem nela ficam translúcidos sozinhos, inclusive nas janelas
  que abrirem depois. Guardado pelo nome do executável. O botão *Adicionar* abre a lista de janelas
  abertas — e aqui o Explorador de Arquivos aparece, ao contrário da lista do jogo, que o esconde de
  propósito.

Quando as duas discordam, a mão manda: desligar pelo atalho uma janela de aplicativo listado a
mantém opaca até ela fechar.

Ao contrário da barra do Windows, isto **não injeta nada** e não tem DLL, gancho nem driver: os dois
modos são atributos de janela que se aplicam de fora do processo. Nada aqui tem o assunto do
antivírus.

### Como "só o fundo" funciona, e por que é uma chamada só

No Windows 11 a janela já tem um **material de fundo** (Mica) que o DWM desenha; o programa é que
pinta por cima dele. `DwmExtendFrameIntoClientArea` com margens de −1 faz o DWM preencher a área de
cliente inteira com esse material, e o que o programa desenha continua por cima, nítido. Nada entra
no processo alheio.

Medido numa janela de pastas nesta máquina, a cor média do fundo:

| estado | cor do fundo |
|---|---|
| original | (25, 25, 25) |
| quadro estendido | (38, 25, 80) |
| quadro estendido + acento | (179, 25, 162) |
| devolvido | (25, 25, 25) |

Daí os dois níveis do interruptor "abrir mais o fundo", e daí também não haver controle contínuo: o
tom da política de acento não muda nada aqui — testado com alfa 0x40 e 0x80, a cor medida foi a
mesma. Oferecer um controle contínuo seria oferecer um botão que não faz nada.

O efeito é **reposto por um relógio de 1 s** enquanto houver janela nesse modo. Não é enfeite: numa
execução real uma janela apareceu sem o efeito depois de ter sido restaurada e movida por outro
processo. Num teste controlado depois, o efeito sobreviveu a maximizar, restaurar e redimensionar,
então a causa não foi isolada — e repor custa 0,077 ms por passagem com duas janelas, medido, sem
percorrer as janelas do sistema. Barato o bastante para não valer a pena descobrir.

O caminho que **não** funcionou, para quem for mexer nisto depois: trocar o material com
`DWMWA_SYSTEMBACKDROP_TYPE`. Sem estender o quadro não muda nada, e com o quadro estendido qualquer
valor posto de fora (inclusive o mesmo que já estava) troca o material por transparência crua — dá
para ler a janela de trás através desta. O acerto é estender o quadro e **não tocar** no material.

Também não serve o caminho da barra de tarefas. O Explorador de Arquivos roda num `explorer.exe`
separado do da barra e desenha em WinUI (`Microsoft.UI.Xaml`), que não exporta
`InitializeXamlDiagnosticsEx` — confirmado pelas exportações da DLL. A árvore visual dele veio
vazia pela API de diagnóstico do XAML do sistema. Ou seja: para o Explorador o TAP não alcança, e
felizmente não precisa.

### O que fica de fora, e por quê

- **A barra de tarefas e a área de trabalho**, mesmo sendo janelas do mesmo `explorer.exe`. A barra
  tem tratamento próprio em *Barra do Windows* e este não pode atropelar aquele; a área de trabalho
  translúcida não mostraria nada atrás. O filtro é por **classe de janela**, não por processo — é a
  única forma de separar uma janela de pastas do resto do shell. Também ficam de fora o menu
  Iniciar, a busca, a central de notificações e a visão de tarefas.
- **Janelas que já são *layered***: programas que se desenham com transparência por pixel usam o
  mesmo bit, e trocar para alfa uniforme estragaria o desenho deles.
- Janelas menores que 200×120, invisíveis, de ferramenta, ocultas (aplicativo de loja suspenso) e
  as do próprio app.
- No modo "só o fundo", janela **sem material de fundo** do Windows 11. Sem material, estender o
  quadro deixaria a área de cliente transparente de verdade, com o texto quase sumindo.

A varredura roda quando outra janela vai para a frente — que é quando janela nova aparece — e não
por relógio. Cada janela tocada guarda se foi o app que pôs o bit de transparência, para devolvê-lo
ao sair sem mexer em quem já o tinha; desligar a opção ou fechar o app devolve todas ao normal.

## Uso no dia a dia

- **Ícone da bandeja**: as barras são desenhadas no próprio ícone (uma coluna ou uma linha por
  barra, conforme a orientação escolhida; com uma única barra ativa ele mostra a porcentagem).
  Passar o mouse mostra as porcentagens e o ritmo de consumo. Duplo clique abre o painel; botão
  direito tem atualizar, as seções do painel, mostrar/ocultar gadget e sair.
- **Painel na barra de tarefas**: clique abre o painel; botão direito tem o mesmo menu, incluindo
  ocultá-lo. Passar o mouse sobre cada indicador mostra quanto resta e quando renova.
- **Gadget**: arraste **pelo cabeçalho** (a faixa com o nome, no topo); passe o mouse para ver os
  botões de atualizar, configurar e ocultar; botão direito abre o menu.

A **linha do tempo da comunicação** aparece no rodapé do gadget, ao lado dos indicadores no painel
da barra de tarefas e descendo pela lateral da barra própria: dez pontos, o mais recente à direita
(embaixo, na barra em pé), **um por ciclo do intervalo configurado** — e não um por chamada. É essa
cadência fixa que mostra a saúde da conexão.

| Ponto | O que aconteceu naquele ciclo |
|---|---|
| **Verde** | a consulta respondeu |
| **Âmbar** | a API respondeu com limite de consultas atingido (HTTP 429) |
| **Vermelho** | a consulta falhou (rede, credencial, formato inesperado) |
| **Apagado** | o ciclo fechou sem resposta: a consulta demorou mais que o intervalo |

Passar o mouse sobre cada ponto mostra o horário e o motivo daquele ciclo. Dá para desligar a faixa
em Configurações › Sistema.

O **histórico** é gravado em `%APPDATA%\ClaudeIndicator\history.jsonl` enquanto o app está aberto e,
por padrão, **nada é apagado** — dá para ligar uma retenção em *Configurações › Dados*. Não há como
importar consumo anterior: a API devolve só o estado atual dos limites, sem série histórica, então o
gráfico começa vazio e enche a partir do primeiro uso.

## Sensores do computador

Um segundo painel na barra de tarefas mostra **CPU, GPU e memória**, com uso, temperatura, watts e
memória usada. Ele fica no lado oposto ao painel da IA: a ideia é que cada bloco tenha lugar fixo,
em vez de ícones que trocam de posição e não se identificam. A tela dele também é escolhida à
parte — dá para deixar a IA num monitor e os sensores em outro.

A leitura usa a [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
numa thread própria — abrir os sensores leva alguns segundos e cada leitura, dezenas de
milissegundos, então nada disso acontece na interface. Com o painel desligado, nenhuma leitura
roda e nenhum driver fica aberto.

Quase tudo é lido **sem driver e sem elevação**:

| Métrica | Como é lida | Precisa de driver? |
|---|---|---|
| GPU: uso, temperatura, watts, VRAM | NVAPI/NVML, pela biblioteca | não |
| Memória: uso e GB | API do sistema | não |
| CPU: uso | contador de desempenho do Windows | não |
| CPU: temperatura | zona térmica ACPI, por contador de desempenho | não |
| **CPU: watts** | registradores do processador | **sim** |
| Disco: tempo ativo e taxas | contadores `PhysicalDisk` | não |

### O disco, em paralelo à memória

CPU e GPU têm um par: o trilho de uso e, ao lado, o termômetro. A memória não tem sensor de
temperatura e a vaga ficava vazia. É onde entra o **disco**, na barra própria em pé.

Uma trilha só, com o **zero na linha do meio**: leitura cresce para cima, gravação para baixo.
Ler e gravar disputam o mesmo aparelho, e saindo da mesma linha o equilíbrio entre os dois se lê
sem comparar alturas em lugares diferentes. A régua de cor é a das outras trilhas, espelhada:
verde encostado no centro, vermelho nas pontas.

O disco é escolhido em *Configurações › Painéis*, na mesma numeração do Gerenciador de Tarefas
("Disco 1 (D:)"), ou o somatório de todos. Com a memória desligada o disco ganha coluna própria,
em vez de sumir junto com a anfitriã.

No **painel da barra de tarefas**, que é deitado, o mesmo par aparece na horizontal: CPU e GPU
ganham o termômetro com o bulbo à esquerda, e a memória ganha a barra de disco com o zero no meio —
leitura para a direita, gravação para a esquerda. Direita para leitura porque numa fila deitada
"mais" já é para a direita nas barras de uso, e inverter só para o disco confundiria os vizinhos.

As duas trilhas de cada par ficam **uma sobre a outra**, não lado a lado. Lado a lado elas viravam
uma fita comprida só e não se lia onde uma acabava e a outra começava; empilhadas, partem da mesma
margem esquerda e a comparação é imediata. De quebra a célula encurta bastante, que é o que a barra
de tarefas tem de sobra em altura e não em largura.

#### Não existe 0 a 100% da velocidade de um disco

A pergunta natural é "quanto por cento da capacidade do disco isso é?", e ela **não tem resposta**.
O Windows não sabe o teto do aparelho, e esse teto nem é um número fixo: o mesmo NVMe entrega
alguns GB/s em leitura sequencial e algumas dezenas de MB/s em aleatória de 4 KB, e ainda
desacelera quando o cache SLC enche no meio de uma gravação longa. Não há régua contra a qual
dividir.

O que existe e é porcentagem de verdade é o **tempo ativo**: a fração do tempo em que o disco teve
pelo menos um pedido em andamento. É o número que o Gerenciador de Tarefas mostra como "Tempo de
atividade", e é o que a barra desenha e o número exibe.

O contador que *parece* ser o certo não serve. Medido nesta máquina:

| contador | leitura num mesmo instante |
|---|---|
| `% Disk Time` | 392,3% |
| `100 - % Idle Time` | 27,9% |

O primeiro conta fila, não tempo, e passa de 100% sem esforço — uma barra alimentada por ele
estaria cheia quase sempre e não diria nada. Os irmãos dele por direção têm o mesmo defeito, mas a
**razão** entre `% Disk Read Time` e `% Disk Write Time` continua honesta, e é dela que sai a
repartição do tempo ativo entre as duas metades da barra.

Vale saber que num disco rápido a barra é discreta: 826 MB/s de leitura sequencial medidos aqui
mantiveram o tempo ativo bem abaixo da metade, porque o disco passou a maior parte do tempo ocioso
entre rajadas. Não é a barra falhando, é o disco sendo rápido demais para o trabalho pedido.

Os **MB/s** ficam no balão, com quem está lendo e gravando mais.

### Quem está consumindo mais

Passar o mouse sobre CPU, GPU ou memória — no painel da barra ou no gadget — mostra, além dos
totais, os **cinco programas que mais consomem aquele componente agora**, do maior para o menor.
Processos com o mesmo nome são somados: o Chrome abre dezenas deles, e vinte linhas de `chrome.exe`
não responderiam à pergunta que se faz ao passar o mouse.

De onde saem os números, e por que não pela via óbvia:

| Lista | Fonte | Custo | Por que não o caminho comum |
|---|---|---|---|
| Memória e CPU | `NtQuerySystemInformation` | ~7 ms, todos os processos | `Process.TotalProcessorTime` abre um handle por processo; sem elevação, 187 de 377 negaram acesso aqui — e são justamente os do sistema, que às vezes lideram a lista |
| GPU | contadores `GPU Engine`, por consulta PDH com curinga | ~2 ms por leitura | pela classe `PerformanceCounter` a mesma leitura levava **6,3 s** (773 instâncias, uma consulta cada) |
| Disco | os mesmos bytes da chamada acima, nos contadores de E/S da estrutura | zero, vem de carona | os contadores `Process` por PDH custariam outra varredura para dados que o kernel já entregou; os deslocamentos foram conferidos contra `GetProcessIoCounters` e bateram em 161 de 161 processos |

A lista do disco tem uma ressalva que a coluna "Disco" do Gerenciador de Tarefas também tem: ela
conta **toda** a E/S de cada programa — arquivo, rede e dispositivo —, e não só o disco escolhido.
Ela também não distingue leitura que veio do disco de leitura que veio do cache do Windows, então
um programa pode aparecer lendo GB/s enquanto o disco físico mal se mexe. São medidas de coisas
diferentes, e as duas estão certas.

Tudo isso roda na thread de leitura, junto dos sensores: **5–12 ms por leitura**, medidos, e nada
na interface — o balão só formata o que já está pronto. Uso de CPU é diferença entre duas leituras,
então a primeira amostra sai sem essa lista. Máquina sem os contadores de GPU por processo
simplesmente não mostra a lista da GPU, e o resto segue igual. O balão é o retrato do momento em
que ele abriu: com o mouse parado sobre o indicador, o app deixa de redesenhar de propósito, para
não fechar o balão que está sendo lido.

A temperatura da CPU vem da **zona térmica ACPI** (`Thermal Zone Information`), que custa ~2 ms
por leitura e não exige nada. Ela mede o conjunto ao redor do processador, não o sensor interno
dele: acompanha o aquecimento de perto, mas pode diferir alguns graus do número que o Afterburner
mostra — e o tooltip diz isso, em vez de fingir equivalência. Quando o sensor interno está
disponível (com driver), ele tem preferência.

Sobra uma única medida atrás do driver: **os watts da CPU**, que só existem nos registradores do
processador. O que a biblioteca usa é o **WinRing0**, o mesmo que está
por trás de vários utilitários de monitoramento — e ele dá acesso direto a memória física e portas
de E/S a qualquer processo que consiga falar com ele. Por isso:

- o **Windows Defender o classifica como `VulnerableDriver`** e o remove — não é um engano de
  assinatura, é a classificação correta para o que o driver permite;
- com a **Integridade de Memória (HVCI) ligada** — padrão no Windows 11 —, o Windows aplica a
  lista de drivers vulneráveis da Microsoft e **o driver não carrega nem como administrador**.

Por isso essa leitura é uma **opção desligada por padrão** (*Configurações › Onde exibir › Ler
também temperatura e watts da CPU*). Com ela desligada, nenhum driver é extraído e nenhum alerta
aparece. Ligada, o app avisa o que está faltando: elevação, quando é só isso, com um botão para
reabrir elevado; ou a Integridade de Memória, quando nem elevar resolve — e nesse caso a saída
seria desligá-la, o que reduz a proteção do sistema contra ataques que abusam exatamente desse
tipo de driver. A recomendação é não desligar.

## Indicadores por cima do jogo

Quando um jogo está em primeiro plano, o app desenha um bloco discreto por cima dele com **FPS,
tempo de quadro, 1% low** e os mesmos sensores do painel da barra — a ideia é não precisar de um
segundo programa só para isso.

### Como se mede FPS sem entrar no jogo

Há dois caminhos no mercado, e eles são opostos:

| | **Injeção** (RTSS/MSI Afterburner, Steam, Discord) | **Rastreamento** (PresentMon, NVIDIA FrameView) |
|---|---|---|
| Como funciona | injeta uma DLL no processo do jogo e engancha `Present`/`vkQueuePresentKHR` | escuta os eventos que o próprio Windows emite a cada quadro |
| Desenha dentro do jogo | sim, no buffer do jogo | não, é uma janela por cima |
| Tela cheia exclusiva | funciona | não sobrepõe |
| Risco com anticheat | real — é o mesmo gesto de um cheat | nenhum: passivo, nada é injetado nem lido da memória |

O RTSS injeta `RTSSHooks64.dll` via *CBT hook* em processos que fazem gerenciamento de janela e,
achando um runtime D3D/OpenGL/Vulkan carregado, instala os ganchos de API. É o que permite a ele
desenhar dentro de tela cheia exclusiva — e também o que faz alguns anticheats reclamarem.

**Este app usa o segundo caminho.** O runtime gráfico do Windows já anuncia cada quadro
apresentado por Event Tracing for Windows; basta escutar. É o mesmo mecanismo do PresentMon da
Intel e do FrameView da NVIDIA. Os provedores usados:

| Provedor | GUID | Evento | Cobre |
|---|---|---|---|
| `Microsoft-Windows-DXGI` | `ca11c036-…` | `PresentStart` (42) | Direct3D 10/11/12 |
| `Microsoft-Windows-D3D9` | `783aca0a-…` | `PresentStart` (1) | jogos antigos |

Só o **cabeçalho** de cada evento é lido — qual processo apresentou e quando. O corpo é ignorado,
o que dispensa decodificar manifesto e deixa o custo em quase nada.

Três detalhes desse caminho custaram caro para descobrir, e valem estar escritos:

- **O nível é filtro, não etiqueta.** O ETW entrega apenas eventos de nível menor ou igual ao
  pedido, e o Present é "detalhado" (5). Pedindo "informativo" (4), a sessão sobe, não dá erro
  nenhum e não recebe um único evento.
- **O carimbo vem em hora do sistema**, não no contador de alta resolução — mesmo pedindo o
  contador. Comparar carimbo de um relógio com "agora" de outro dá tempo negativo em toda conta.
- **A entrega é em lote.** Os eventos chegam com cerca de dois segundos de atraso, então a
  contagem é feita sobre os próprios carimbos, e não contra o relógio de parede; este último
  responde só a uma pergunta, se o processo ainda está apresentando. É por isso que o FPS mostrado
  tem alguns segundos de defasagem — o preço de não injetar nada no jogo.

Sem FPS, o bloco diz por quê ali mesmo — "precisa de administrador" ou "este jogo não passa pelo
DirectX" —, em vez de mostrar um traço mudo. Quem está no meio de uma partida não vai abrir as
configurações para descobrir o motivo.

Criar uma sessão de rastreamento **exige administrador** (a mesma exigência do PresentMon e do
FrameView). Sem elevação o bloco ainda aparece com os sensores, e o FPS fica em branco com o
motivo explicado nas configurações. Duas saídas: usar *Reiniciar como administrador*, ou — uma vez
só — colocar seu usuário no grupo **Usuários do log de desempenho**, que concede exatamente esse
privilégio sem elevar o resto:

```
net localgroup "Performance Log Users" "%USERNAME%" /add
```

(precisa de um prompt como administrador e de sair e entrar na sessão do Windows)

### Como ele sabe qual é o jogo

**Você aponta.** Abra o jogo, vá em *Configurações › Indicadores por cima do jogo › Escolher
janela…* e clique na janela dele. A lista mostra tamanho, se a janela cobre o monitor e o FPS
quando há medição — os três sinais que identificam o jogo sem precisar reconhecer o executável.
A escolha fica guardada pelo nome do processo, então da próxima vez que o jogo abrir o indicador
volta sozinho.

Escolher à mão é o caminho principal por um motivo simples: **em janela sem bordas um jogo é
indistinguível de qualquer outra janela**. Não há sinal confiável para adivinhar, e adivinhar
errado significa o indicador não aparecer sem explicação.

Deixando o campo vazio, o app tenta adivinhar: janela em primeiro plano, fora da lista de
exceções, cobrindo o monitor **ou** apresentando mais de 10 quadros por segundo. Repare no **ou**:
os quadros são um sinal a favor, nunca um veto — um jogo em Vulkan ou OpenGL não passa pelos
provedores DXGI e D3D9, e recusá-lo por isso seria trocar "sem FPS" por "sem indicador nenhum".

### Exceções

Adivinhar erra para os dois lados, e o erro de mostrar demais incomoda mais: uma planilha
maximizada cobre o monitor exatamente como um jogo. Há uma lista embutida (navegador, editor,
Office, leitor de PDF, OBS, Wallpaper Engine, o próprio Explorer) e uma **sua**, em *Configurações
› Indicadores por cima do jogo › Nunca mostrar nestes*, montada pela mesma lista de janelas
abertas. Fica guardada por nome de executável, então vale nas próximas vezes que o programa abrir.

A exceção só vale para a adivinhação. Um processo apontado à mão é uma decisão explícita, e vetá-la
seria desobedecer.

### Onde ele aparece

Por padrão o bloco só aparece com o jogo em primeiro plano — alt-tabou, ele some. A opção
*"Mostrar mesmo quando o jogo não está em primeiro plano"* muda isso, e vale nos dois modos: com o
jogo escolhido à mão, ele é seguido pelo nome do processo; na adivinhação, o app lembra qual foi o
último jogo reconhecido e continua nele enquanto a janela existir. Sem essa memória a opção não
teria efeito no modo automático — sem foco não há o que olhar em primeiro plano.

A posição é escolhida numa grade de nove cantos, com distância da borda, tamanho e opacidade
ajustáveis. O bloco se ancora na **janela do jogo**, não no monitor, para continuar certo quando o
jogo roda em janela menor que a tela. A janela do indicador é *click-through*: o mouse não a
enxerga, o clique vai direto para o jogo. Por padrão ele só aparece com o jogo em primeiro plano;
há uma opção para mostrar mesmo sem foco.

O texto sai com **contorno escuro desenhado ao redor das letras**, e não com sombra desfocada.
Desfoque espalha a tinta: em texto de 10 px o contorno fica fraco justamente onde mais precisa. Um
traço sólido em volta da letra mantém a leitura sobre cena clara de jogo — e sobre papel de parede
claro, no painel da barra de tarefas, que era onde o texto sumia.

O contorno é uma **escolha**, em *Configurações › Painel na barra de tarefas › Estilo*, e vale para
os dois painéis da barra e para o indicador no jogo:

| Estilo | Texto | Trilha da barra | Quando usar |
|---|---|---|---|
| **Com contorno** | traço escuro em volta da letra | escura, com borda clara | papel de parede claro, ou cena de jogo clara |
| **Sem contorno** | texto comum | branca translúcida | papel de parede escuro, para um visual mais leve |

Sem contorno o texto volta a ser desenhado pelo caminho normal do WPF, e não como forma
preenchida — é o que devolve a nitidez de subpixel que o desenho por geometria não tem.

### O que aparece, e por quê

Cada sensor mostra o valor, a cor da carga e o **traçado das últimas leituras** — cerca de dois
minutos de história a 2 s por leitura:

```
 48 FPS   21,1 ms   1% 20
CPU  26%  ▁▁▂▁▁  66°
GPU  48%  ▄▆▅▇▄  55°  15 W
RAM  19,8 GB  ▃▃▃▃▃  62%
```

O traçado responde ao que o número sozinho não responde: aquele 78% é um pico ou um platô? A
escala é **fixa de 0 a 100%**, e não ajustada ao maior valor da amostra — com escala automática uma
variação de 3% ocuparia a altura toda e pareceria drama, quando o que se quer saber é a distância
até o teto.

A cor sai da **mesma régua das barras do painel** — verde no começo, amarela na metade, vermelha no
fim. Se o mesmo 70% aparecesse laranja num lugar e amarelo no outro, a cor deixaria de ser
informação e viraria decoração.

Cada linha é opcional, incluindo o traçado e os limites da assinatura Claude — que vêm desligados,
já que eles têm lugar próprio na barra de tarefas e no gadget.

### Dois layouts: compacto e medidores

O bloco tem dois desenhos, e `Ctrl+Alt+L` alterna entre eles sem sair do jogo. O **compacto** é o
de cima: uma linha por sensor, com o traçado. Ocupa pouco e é o que mostra história.

Os **medidores** trocam a história pela leitura de relance. Cada componente vira um bloco só, com o
anel de uso e o **seu** termômetro ao lado:

```
   ╭─────╮   ▯                ╭─────╮   ▮                ╭─────╮
  (  28% )   ▮  67°          (  63% )   ▮  54°          (  62% )
   ╰─────╯   ●                ╰─────╯   ●                ╰─────╯
     CPU                        GPU                        RAM
```

São duas perguntas diferentes, por isso duas formas diferentes: o anel responde *quanto do total
está em uso*, o termômetro responde *quão perto do limite físico*. E o termômetro é de **cada**
componente. Antes havia um só, o da CPU, plantado entre os dois primeiros anéis — de canto de olho
o número tanto podia ser do anel da esquerda quanto do da direita, e a GPU, que é justamente a peça
que esquenta em jogo, ficava sem o dela.

A temperatura é o texto maior do bloco de propósito, e sai na cor da faixa térmica. Essa régua
**não** é a da carga: 50% de uso é metade do caminho e sai amarelo, mas 50 °C é temperatura
confortável e tem que sair verde. O amarelo entra aos 70 °C e o vermelho aos 90 °C, que é onde
processadores de verdade começam a reduzir clock.

Os blocos se espalham pela largura da caixa, e a linha do FPS encosta as medidas de apoio na borda
direita: a sobra vira vão entre os medidores, e não faixa morta num canto. Sensor sem leitura não
ganha lugar reservado — sem elevação a CPU não tem temperatura para ler, e o termômetro dela
simplesmente não é desenhado.

### Atalhos, para usar dentro da partida

Durante uma partida não dá para alternar até as configurações — e é justamente ali que se quer
tirar o bloco da frente ou mudá-lo de canto. Três atalhos globais resolvem, e funcionam com o jogo
em primeiro plano:

| Padrão | O que faz |
|---|---|
| `Ctrl+Alt+O` | liga e desliga o bloco |
| `Ctrl+Alt+M` | passa para o próximo dos nove cantos, em volta |
| `Ctrl+Alt+L` | alterna entre os dois layouts, compacto e medidores |

Ambos são configuráveis em *Configurações › Indicadores por cima do jogo*: clique no botão e
pressione a combinação que quiser (Esc cancela, Backspace desliga o atalho). Quando o Windows
recusa uma combinação — porque outro programa já a tomou —, a tela avisa em vez de deixar o atalho
mudo.

O próprio bloco mostra os atalhos num rodapé discreto, e isso resolve um problema concreto: **uma
vez oculto, ele não tem como lembrar a combinação que o traz de volta**. Ela precisa estar à vista
antes. Combinação recusada pelo Windows não aparece ali — anunciar um atalho que não funciona é
pior que não anunciar nada. O rodapé se desliga como todo o resto da caixa.

A escolha fica guardada: desligar o bloco pelo atalho é uma decisão, não um sumiço temporário, e
ela continua valendo na próxima vez que o app abrir.

| Modo do jogo | Aparece? |
|---|---|
| Janela sem bordas | sim |
| Tela cheia com otimizações do Windows (padrão no Windows 11) | sim |
| Tela cheia **exclusiva de verdade** | não — o jogo é dono do buffer da tela |

A terceira linha é o limite honesto desta abordagem: nenhuma janela sobrepõe tela cheia exclusiva,
e sair disso exigiria injetar no processo do jogo. Como o Windows 11 roda quase todo jogo em tela
cheia com otimizações, na prática o caso raro é o terceiro; quando acontecer, alternar o jogo para
"tela cheia em janela" resolve.

## Trocar o tema do Windows

No fim do painel do computador há um botão que **troca o tema claro/escuro do Windows** num clique.
Ele mostra o tema de destino — sol quando está escuro, lua quando está claro —, porque é isso que o
clique faz; o balão diz com todas as letras, para não sobrar dúvida.

Por baixo não há mágica: a página *Personalização › Cores* escreve duas chaves em
`HKCU\...\Themes\Personalize` (`AppsUseLightTheme` e `SystemUsesLightTheme`) e avisa o sistema. São
duas porque o Windows separa aplicativos de sistema — a barra de tarefas e o Iniciar seguem a
segunda —, e a caixa "Escolher seu modo" muda as duas juntas. O botão faz o mesmo, incluindo o
`WM_SETTINGCHANGE`: sem esse aviso os aplicativos já abertos ficariam com o tema antigo até serem
reiniciados.

Com o painel do computador desligado, o botão vai para o painel da IA em vez de desaparecer.

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

**Com várias telas**, cada painel escolhe em qual monitor se encaixa. O Windows desenha uma barra
por tela — a principal é a janela `Shell_TrayWnd`, as outras são `Shell_SecondaryTrayWnd` —, e é a
barra da tela escolhida que define o espaço livre, o rodapé e o teste de tela cheia. Nas secundárias
do Windows 11 o relógio não tem janela própria para medir, então uma faixa proporcional à altura da
barra fica reservada para ele.

O posicionamento é feito em **pixels de tela**, com `SetWindowPos`, e não pelas propriedades
`Left`/`Top` do WPF: com telas em escalas diferentes (100% e 175% ao mesmo tempo, por exemplo) o WPF
converte essas propriedades por um DPI que não é necessariamente o do monitor de destino, e o painel
parava longe do lugar — às vezes na tela errada, sem nunca convergir. Uma trava em
`WM_WINDOWPOSCHANGING` devolve qualquer movimento ao alvo, inclusive os que o próprio WPF faz quando
o conteúdo muda de largura ou o Topmost é reafirmado. Ancorado à direita, a borda fixa é a direita,
para o painel não invadir o relógio quando o texto cresce.

Manter-se por cima exige atenção: a barra de tarefas também é topmost, e fechar ou ativar qualquer
janela remexe a ordem-Z. Em vez de reafirmar a posição de tempos em tempos — o que derruba o
tooltip aberto —, o painel pergunta ao Windows quem atende no seu ponto central; se a resposta não
for ele mesmo, alguém passou na frente e ele volta na hora.

```
src/ClaudeIndicator/
  App.xaml(.cs)          tema escuro, estilos do painel + instância única
  Core/
    AppHost.cs           orquestra timer, bandeja, painel da barra, gadget e janela
    AppSettings.cs       preferências (JSON em %APPDATA%)
    CredentialStore.cs   leitura do login do Claude Code + refresh OAuth
    UsageService.cs      HTTP + parser tolerante do JSON de consumo
    ProcessUsage.cs      quem consome mais CPU, GPU e memória (kernel + PDH)
    UsageHistory.cs      grava/lê o histórico de consumo (history.jsonl)
    SessionState.cs      retrato da última leitura e dos últimos ciclos, para reabrir sabendo
    TranscriptIndex.cs   índice incremental das transcrições do Claude Code
    TrayIconRenderer.cs  desenha o ícone da bandeja em tempo real
    TaskbarInfo.cs       geometria da barra de tarefas, dos monitores e espaço livre nela
    DesktopAppBar.cs     registra a barra própria como appbar do Windows (reserva a faixa)
    WindowBackdrop.cs    fundo fosco pelo compositor do Windows (o acrílico da barra)
    TaskbarStyler.cs     aparência da barra de tarefas do Windows: acento + tap, em todas as telas
    ExplorerTap.cs       injeta a DLL nativa no Explorer e conversa com ela (memória compartilhada)
    ShellWatcher.cs      avisos do shell: barra recriada, telas, compositor, tema
    WindowGlass.cs       deixa janelas de outros programas translúcidas (só o fundo ou tudo)
    DiskMonitor.cs       tempo ativo e direção de um disco, pelos contadores PhysicalDisk
    EtwSession.cs        sessão de rastreamento do Windows: eventos de quadro apresentado
    FrameRateMonitor.cs  carimbos de quadro -> FPS, tempo de quadro e 1% low por processo
    GameDetector.cs      resolve qual janela recebe o indicador (escolhida ou adivinhada)
    WindowScanner.cs     lista as janelas abertas para você apontar qual é o jogo
    StartupManager.cs    inicialização automática (HKCU\...\Run)
    WindowsTheme.cs      lê e troca o tema claro/escuro do Windows
    Hotkey.cs            uma combinação de teclas, em texto legível
    HotkeyManager.cs     atalhos globais, que funcionam com o jogo em foco
    AppInfo.cs           versão exibida na interface
  Views/
    MainWindow.xaml      painel: navegação lateral + página escolhida
    GadgetWindow.xaml    gadget transparente, arrastável, sempre por cima
    TaskbarBarWindow.xaml  faixa ancorada no espaço livre da barra de tarefas
    DockWindow.xaml        a barra própria: borda livre da tela, com os painéis dentro
    GameOverlayWindow.xaml indicadores por cima do jogo, sem foco e sem receber clique
    GamePickerWindow.xaml  lista de janelas abertas: o jogo, uma exceção ou o vidro
    OutlinedText.cs        texto com contorno, legível sobre qualquer fundo
    BarRenderer.cs       desenho das barras (gadget e prévia) + o trilho em pé
    PanelStyle.cs        o estilo dos painéis: células deitadas e colunas em pé, uma cópia só
    HardwareRenderer.cs  linhas e células de CPU/GPU/memória no gadget, e os textos dos balões
    Pages/
      OverviewPage.xaml  visão geral: restante, ritmo, projeção e top projetos
      HistoryPage.xaml   gráficos do histórico (nível e consumo por hora/dia)
      ProjectsPage.xaml  consumo por projeto e prompts de cada um
      SettingsPage.xaml  configurações por categoria, com salvar sob demanda

native/ExplorerTap/      a DLL C++ que entra no Explorer: gancho + TAP do XAML da barra
```

## Aviso

Projeto pessoal, não oficial e sem vínculo com a Anthropic. Ele apenas lê o consumo da sua própria
conta com o seu próprio token.

