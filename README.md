# Dora Explorer

Gerenciador de ficheiros para Windows com dois painéis lado a lado, inspirado no X-plore. Navegação em árvore unificada (pastas e ficheiros no mesmo nível), ícones do sistema, barra de caminho, menu central de operações, cliente e servidor de rede (FTP, SFTP, SCP, TFTP) e opções de visibilidade por painel.

## Requisitos

- Windows 10 ou superior
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — o **SDK** é necessário para compilar; ter apenas o runtime instalado não basta (`winget install Microsoft.DotNet.SDK.8`)

## Executar

Na raiz do projeto:

```bat
Start.bat
```

O script executa **stop** (encerra instâncias anteriores) e **start** (compila e inicia) na mesma sequência.

- Primeiro **pede ao app para fechar** (`scripts\stop-dora.ps1`): ele fecha como se você clicasse no X, então pergunta antes de abandonar uma cópia, mover ou compactar em andamento (responda na janela dele; o script espera até 10 s). Só o que continuar aberto depois disso é encerrado à força, e o mesmo vale para os processos `dotnet` associados à pasta `build\`
- Se o app estiver aberto **como administrador** (depois de "Reiniciar como administrador"), um terminal comum não consegue fechá-lo: o script avisa o PID e o `Start.bat` para sem compilar (código de saída 2). Feche o app pela janela dele ou rode o `Start.bat` como administrador
- Verifica se há um **.NET SDK 8+** instalado e para com uma mensagem clara se não houver
- Compila em modo **Release** para `build\compile\` e copia o resultado para `build\staging\`
- Se a compilação falhar, **não** inicia nenhuma versão antiga do executável
- Inicia `build\compile\DoraExplorer.App.exe` e confirma que o processo continua em execução
- Cria a pasta `logs\` se não existir
- Regenera `project.md` e `project-metodos.md` (mapa do código para agentes; lê arquivos, tipos, métodos, linhas e pacotes do próprio código, e avisa se o mapa funcional apontar para algo que não existe mais); os ficheiros têm data de geração e mudam a cada execução

### Compilação manual e testes

```bat
dotnet build src\DoraExplorer.App\DoraExplorer.App.csproj -c Release -o build\staging
build\staging\DoraExplorer.App.exe

dotnet test DoraExplorer.sln -c Release
```

O **book de testes** (`tests\BOOKTEST.md`, gerado por `scripts\generate-booktest.ps1`) lista, por área do app, os testes que a protegem, o comando para rodar só aquela área, um índice por tipo de código e o que só foi conferido na tela. Toda mudança ou criação num sistema leva testes novos ou editados, salvos em `tests\DoraExplorer.App.Tests`, para que o próximo ajuste não quebre o que já funciona.

## Funcionalidades

| Área | Descrição |
|------|-----------|
| **Dois painéis** | Painéis esquerdo e direito independentes com a mesma estrutura |
| **Painel ativo** | O último painel com que interagiu define o destino de operações de pasta (nova pasta, localizar, ordenar) |
| **Árvore unificada** | Unidades, pastas e ficheiros num único `TreeView` |
| **Visão em colunas** | Cada linha mostra *Nome*, *Tipo* (contagem de pastas/arquivos ou aplicativo padrão), *Modificado* e *Tamanho* alinhados sob um cabeçalho. Em painéis estreitos o *Tipo* e depois o *Modificado* saem de cena para o nome nunca ficar cortado |
| **Ordenar pelo cabeçalho** | Clicar em *Nome*, *Tipo*, *Modificado* ou *Tamanho* ordena por essa coluna; clicar de novo inverte (▲/▼). A ordenação vale para os dois painéis, como em **Ordenar** |
| **Densidade das linhas** | *Exibir ▸ Linhas compactas em colunas* (ligado por padrão) alterna entre a visão em colunas (cerca de 20 itens por painel) e as linhas confortáveis de duas linhas |
| **Voltar, avançar e subir** | Botões ← → ↑ ao lado do caminho de cada painel, com histórico próprio por painel; funcionam também em arquivos compactados e caminhos remotos. A seta para cima fica desativada na raiz da unidade ou do servidor |
| **Metadados de ficheiros** | Data, hora e tamanho alinhados à extrema direita de cada arquivo |
| **Linhas de hierarquia** | Guias finas e discretas entre pastas, na cor de destaque, com cantos arredondados |
| **Separadores** | Linhas horizontais muito finas entre cada item da árvore |
| **Scroll automático** | Ao abrir uma pasta, o scroll posiciona essa pasta no topo da área visível |
| **Unidades** | Letras de disco (C:, D:, …) com barra de espaço livre; unidades bloqueadas (BitLocker) ou offline aparecem como indisponíveis |
| **Favoritos** | Botão **Favorito** marca pastas e ficheiros, que aparecem no nó *Favoritos* da árvore |
| **Lixeira** | Nó *Lixeira* da árvore com restauração e exclusão definitiva dos itens |
| **Acesso negado** | Quando o Windows nega acesso (excluir, renomear, criar pasta, copiar, mover, compactar, extrair ou abrir uma pasta protegida), a mensagem vem em português, com o caminho sem o prefixo `\\?\` do .NET, e o app pergunta se você quer **reiniciar como administrador**: o Windows pede a sua confirmação, o aplicativo fecha e abre de novo, e você repete a operação. Já como administrador, a mensagem só explica o motivo (por exemplo, um item do sistema que pertence ao TrustedInstaller). O app nunca se eleva sozinho |
| **Arquivos compactados** | ZIP, TAR, TAR.GZ (e TGZ), 7Z, GZ e RAR abrem como pastas; permitem extrair e abrir entradas e, em ZIP/TAR/TAR.GZ, remover entradas (7Z, GZ e RAR são somente leitura: use Copiar para extrair). Um `.gz` solto aparece como uma pasta com um arquivo dentro, que tem o nome do `.gz` sem a extensão; o tamanho vem do rodapé do gzip e só é mostrado até 4 MB comprimidos, porque acima disso o rodapé (32 bits) pode estar truncado. As pastas de dentro do arquivo mostram a seta de expandir e a contagem de pastas e arquivos desde o início, como as do disco (vêm da própria listagem do arquivo, que não passa pelo índice do disco). Um `.zip` com nomes repetidos (inclusive só trocando maiúsculas) abre normalmente, valendo a primeira entrada; um `.tar.gz` com arquivos muito pequenos também abre |
| **Rede (cliente)** | Nós FTP, SFTP, SCP e TFTP na árvore, com perfis guardados, indicador de saúde e transferência de ficheiro (Enviar/Receber) para SCP e TFTP |
| **Pasta Rede** | Os nós FTP, SFTP, SCP e TFTP ficam agrupados na pasta *Rede* da árvore |
| **Rede (servidor)** | Servidores FTP, SFTP, SCP e TFTP embutidos no menu central (ver *Segurança*). **FTP:** apagar, criar pasta, renomear, retomar envio/download (REST), MLSD/MLST, SIZE/MDTM, EPSV/PASV/PORT/EPRT (o modo ativo só volta ao próprio cliente, portas 1024+), faixa de portas passivas configurável, FTPS explícito (opcional, obrigatório ou desativado; certificado autoassinado criado uma vez e guardado protegido pelo Windows), bloqueio do endereço após 6 logins falhados em 10 min e opção somente leitura. **TFTP:** opções blksize, tsize e timeout (RFC 2347-2349), reenvio de pacotes perdidos, números de bloco acima de 65535 e opção somente leitura (o TFTP não tem senha). **SFTP e SCP:** servidor SSH 2 com um usuário e senha, confinado à pasta compartilhada e com opção somente leitura. O SFTP (versão 3) lista, baixa, envia, cria pasta, renomeia e apaga; o SCP recebe e envia arquivos e pastas (`-r`, `-p` e curinga no último nome). Testado com o `sftp` e o `scp` do OpenSSH 10.5 (SCP clássico `-O` e modo SFTP) e com o SSH.NET |
| **Pastas de servidores** | Em FTP, SFTP e SCP (o SCP usa o subsistema SFTP para listar e organizar) dá para **copiar, mover, enviar, baixar, criar pasta, renomear, excluir, compactar e abrir** arquivos e pastas como se fossem locais, inclusive entre dois servidores e arrastando entre os painéis, com a mesma janela de progresso por bytes e botão Cancelar. Cancelar derruba a conexão na hora e o que ficou pela metade é apagado (no servidor também). Nomes já existentes ganham numeração ("arquivo (1).txt"), e nomes perigosos que um servidor mande (com \\ (automático, explícito ou implícito; certificado desconhecido pede confirmação e é guardado, e avisa se mudar) e listagem automática (MLSD ou LIST em qualquer formato). SFTP/SCP com chave privada (OpenSSH, PEM, PuTTY) além de senha. TFTP negocia blocos de 1428 bytes e volta a 512 se o servidor não aceitar |
| **Cache de pastas** | Listagens já visitadas ficam num índice SQLite em `%AppData%\DoraExplorer\index.db` e são revalidadas em segundo plano. Os testes automáticos (`DirectoryIndexTests`) cobrem gravação, filtros de ocultos/sistema, validade pela data da pasta, invalidação, limpeza de pastas com mais de 90 dias, reabertura do banco e o modo sem cache quando o banco não pode ser criado |
| **Exibir (árvore)** | Menu no fundo da árvore para mostrar/ocultar *Favoritos*, *Lixeira*, *FTP*, *SFTP*, *SCP* e *TFTP* |
| **Exibir (menu superior)** | Arquivos ocultos, de sistema, unidades externas, partições não mapeadas, linhas compactas e aplicativo padrão (guardados em `%AppData%\DoraExplorer\settings.json`) |
| **Barra de status** | Rodapé de cada painel: nome e contagem de pastas/arquivos, tamanho e data do arquivo, espaço livre da unidade, ou quantidade e tamanho total da seleção múltipla. A contagem da pasta selecionada acompanha as mudanças que a revalidação em segundo plano encontra no disco |
| **Uso do disco** | A barra da unidade usa a cor de destaque até 80% de uso, fica âmbar a partir de 80% e vermelha a partir de 90%; nesses dois estados o texto "Livre ..." muda de cor junto (em negrito no crítico), para o alerta aparecer mesmo quando o destaque é laranja ou vermelho. Unidades indisponíveis mostram o motivo ("Bloqueada (BitLocker)", "Sem mídia"...) |
| **Lembrar caminho** | Menu *Ferramentas*: reabre cada painel na última pasta |
| **Breadcrumb** | Barra de caminho clicável no topo de cada painel; a pasta atual aparece em destaque |
| **Tema** | Tema claro, escuro ou automático do sistema, e cor de destaque configurável. A cor de destaque marca a seleção (barra lateral e fundo suave), a rolagem, as linhas de hierarquia e a barra de uso do disco |
| **Ícones** | Ícones nativos do Windows via shell API. Arquivos compactados (`.tar.gz`, `.tgz`, `.tar`, `.gz`, `.7z`...) ganham um ícone próprio (folha com zíper e faixa azul-esverdeada) quando o Windows só tem o genérico para eles, como acontece quando o WinRAR registra essas extensões sem ícone; se houver um ícone de verdade (como o do `.zip` e o do `.rar`), ele é mantido |
| **Prévia de imagem e PDF** | Deixar o mouse parado (cerca de 0,6 s) sobre uma imagem local (jpg, png, gif, bmp, webp, ico) ou um PDF mostra uma miniatura de até 320 px com o nome. A imagem é decodificada pelo próprio app; o PDF usa o provedor de miniaturas do Windows (o mesmo do Explorer), então só aparece se o Windows tiver um para PDF (por exemplo, com o Edge como leitor padrão). A prévia some ao mexer o mouse para outro item, clicar, rolar ou digitar. Itens de servidor e da Lixeira não têm prévia. Existe uma única dica para a lista inteira, então rolar e abrir pastas grandes não ficam mais lentos |
| **Aplicativo padrão** | O rótulo ao lado de cada arquivo mostra o aplicativo que o Windows abre para aquela extensão (o nome oficial, como "Bloco de notas", nunca "Notepad.exe"). Clicar nele abre a lista: os aplicativos cadastrados para o tipo, **Procurar aplicativo no computador…** (qualquer .exe, por exemplo o Notepad++; vale só no Dora Explorer, não muda o Windows, e fica guardado por extensão nas configurações, em `DefaultApplications`), **Voltar ao padrão do Windows** e **Escolher no Windows…** (a janela "Abrir com" do Windows, aberta pela API `SHOpenWithDialog` com o Dora Explorer como dono; escolher um programa abre o arquivo com ele e marcar "Sempre" muda o padrão do próprio Windows. O antigo `rundll32 shell32.dll,OpenAs_RunDLL` não mostra nada no Windows 11 recente). Ao trocar, todas as linhas da mesma extensão atualizam o nome. Se o programa escolhido for desinstalado, volta a valer o padrão do Windows |

### Atalhos de teclado

| Tecla | Ação |
|-------|------|
| **Enter** | Abre o item selecionado |
| **F2** | Renomeia o item selecionado |
| **F5** | Atualiza a pasta |
| **Delete** (**Shift+Delete**) | Exclui a seleção (escolha entre Lixeira e exclusão definitiva) |
| **Ctrl+D** | Marca ou desmarca como favorito |
| **Ctrl+Shift+N** | Nova pasta |
| **Ctrl+C**, **Ctrl+X**, **Ctrl+V** | Marca a seleção para copiar ou mover e a cola na pasta aberta no painel oposto, como os botões **Copiar** e **Mover** (também em *Colar*, no menu de contexto) |
| **Alt+←**, **Alt+→** | Voltar e avançar no histórico do painel (também os botões laterais do mouse) |
| **Alt+↑** | Sobe uma pasta |
| **↑ ↓ ← →** | Navega pela árvore |

Todos os botões do menu central têm dica de ferramenta (com o atalho, quando existe) e nome acessível para leitores de tela, assim como os itens da árvore.

### Menu central

O menu central mostra todas as seções em duas colunas, sem rolagem, a partir da altura mínima da janela (580 px).

#### PASTA

| Botão | Função |
|-------|--------|
| **Localizar** | Pesquisa ficheiros e pastas (com opção de incluir subpastas); duplo clique abre o resultado |
| **Nova pasta** | Cria uma pasta na localização atual do painel ativo, escolhendo um nome livre ("Nova pasta", "Nova pasta (2)", ...) que pula tanto pastas quanto arquivos com esse nome. Pedir um nome que já existe (pasta ou arquivo) é recusado com "Já existe um item chamado ...", em vez de parecer que criou |
| **Ordenar** | Define a ordenação dos itens da pasta atual (nome, tipo, tamanho, data) |
| **Atualizar** | Relê a pasta atual |

#### ARQUIVOS

As operações de ficheiro usam **dois painéis em conjunto**:

- **Origem** — painel onde há um ficheiro ou pasta selecionado
- **Destino** — pasta aberta no painel oposto

| Botão | Função |
|-------|--------|
| **Copiar** | Copia o item selecionado para a pasta aberta no outro painel |
| **Mover** | Move o item selecionado para a pasta aberta no outro painel |
| **Renomear** | Renomeia o ficheiro ou pasta selecionado (em qualquer painel com seleção) |
| **Compactar** | Cria um arquivo `.zip` ou `.tar.gz` a partir dos itens selecionados |
| **Excluir** | Envia para a Lixeira ou exclui definitivamente |
| **Favorito** | Marca ou desmarca o item como favorito |

**Exemplo:** selecione um ficheiro no painel esquerdo, navegue até à pasta de destino no painel direito e clique em **Copiar**.

#### REDE SERVER

| Botão | Estado |
|-------|--------|
| **FTP**, **SFTP**, **SCP**, **TFTP** | Abrem o diálogo do servidor embutido (endereço local, porta, pasta compartilhada, usuário/senha, exceto no TFTP) com *Iniciar/Parar*, opção somente leitura e aba de logs |

### Hierarquia «Exibir» (árvore)

```
Exibir
├── Favoritos
├── Lixeira
├── FTP
├── SFTP
├── SCP
└── TFTP
```

## Estrutura do projeto

```
DoraAdventure/
├── Start.bat                 # Compilar e executar
├── README.md
├── AGENTS.md                 # Guia para agentes: regras de trabalho (perguntar antes, documentar, testar), comandos e armadilhas medidas (CLAUDE.md apenas o importa)
├── project.md                # Mapa do código (gerado por scripts/generate-project-map.ps1)
├── project-metodos.md        # Métodos com número de linha dos arquivos grandes (gerado junto)
├── DoraExplorer.sln          # Solução (app + testes); DoraExplorer.slnx é o formato novo
├── logs/                     # Logs de execução (ver secção abaixo)
├── scripts/                  # stop-dora.ps1, generate-project-map.ps1, generate-booktest.ps1
│   └── dev/                  # ui-capture.ps1 (roteiros na tela), bench.ps1 (medição de pasta grande), make-test-folder.ps1
├── build/                    # Saída gerada: compile/ (compilação), staging/ (cópia), app/
├── tests/
│   ├── BOOKTEST.md           # Book de testes por área (gerado por scripts/generate-booktest.ps1)
│   └── DoraExplorer.App.Tests/   # Testes xUnit
└── src/DoraExplorer.App/
    ├── App.xaml.cs
    ├── MainWindow.xaml
    ├── Assets/               # Ícones
    ├── Contracts/            # Interfaces dos serviços
    ├── Controls/             # FilePaneControl, CenterToolbarControl, TreeHierarchyGuides
    ├── Converters/
    ├── Infrastructure/       # Registo de serviços, caminhos seguros, timeouts, cifragem de senhas, enxugador de memória, coleção observável em bloco
    ├── Models/
    ├── Resources/            # Theme.xaml, estilos, temas claro/escuro
    ├── Services/             # Ficheiros, árvore, ícones, rede, tema, logs, arquivos compactados
    ├── ViewModels/           # MVVM (CommunityToolkit.Mvvm)
    └── Views/Dialogs/        # Diálogos do menu central
```

## Logs e diagnóstico de falhas

A aplicação regista eventos na pasta **`logs/`** na raiz do repositório.

| Ficheiro | Conteúdo |
|----------|----------|
| `logs/doraexplorer-AAAAMMDD.log` | Um ficheiro por dia de execução (10 MB por ficheiro; os 14 mais recentes são mantidos) |

### O que é registado

- Arranque e encerramento da aplicação
- Operações de ficheiros e ligações de rede
- Erros ao ler diretórios (acesso negado, caminho inválido, etc.)
- Exceções não tratadas na interface (UI) e no domínio da aplicação

### Formato

```
[2026-06-17 14:30:45.123] [INF] Mensagem
[2026-06-17 14:30:46.456] [WRN] Aviso
[2026-06-17 14:30:47.789] [ERR] Erro + stack trace
```

Consulte também `logs/LEIA-ME.txt` para um guia rápido de análise.

> Se executar o `.exe` fora da estrutura do projeto, os logs podem ser gravados em `%AppData%\DoraExplorer\logs\`.

## Comportamento das operações

- **Mover pasta entre unidades:** copia, e só apaga a origem depois que a cópia termina. Se algo falhar, o destino parcial é removido e a origem fica intacta. Pastas com links simbólicos/junções não podem ser movidas entre unidades; ao **copiar**, esses links são ignorados (evita ciclos) e o aviso vai para o log.
- **Copiar e mover** (botões, Ctrl+C/X/V e arrastar e soltar) rodam em segundo plano, sem travar a janela, e usam a cópia nativa do Windows (a mesma do Explorer): preservam datas, atributos e fluxos alternativos (como a marca "baixado da internet") e arquivos de 512 MB ou mais não enchem o cache do sistema. Se demorar mais de 0,6 s, abre uma janela com a barra de progresso por bytes, arquivo atual, velocidade e tempo restante, e um botão **Cancelar** que vale no meio de um arquivo grande. O que ficou pela metade é apagado e a origem continua intacta. Quando falha, a mensagem diz o motivo (disco cheio, acesso negado...) em vez de um aviso genérico.
- **Compactar** (ZIP e TAR.GZ) também roda com a janela de progresso: lê os arquivos em blocos, mostra os bytes lidos, a velocidade e o tempo restante, e o **Cancelar** vale no meio de um arquivo grande. Um ZIP ou TAR pela metade seria um arquivo corrompido, então o parcial é sempre apagado. Links simbólicos e junções são ignorados (uma junção que aponta para a própria pasta fazia a compactação girar para sempre), e arquivos com data anterior a 1980 não quebram o ZIP.
- **Tempo limite por inatividade:** copiar, mover e compactar só são interrompidos se **nenhum byte andar por 2 minutos** (disco ou rede parou). Uma operação de 100 GB pode levar horas e não expira. Ao cancelar, a operação espera o trabalho apagar o que ficou pela metade (até 5 s) antes de devolver o controle, para o painel nunca mostrar um arquivo parcial.
- **Cliente TFTP** (transferência de um arquivo): retransmite pacotes perdidos, mostra o erro devolvido pelo servidor, tem tempo limite e grava o download num arquivo temporário (o arquivo local existente só é substituído no fim). O "Testar conexão" pede um arquivo inexistente e considera qualquer resposta do servidor como sucesso.
- **Tempos limite de rede:** listagens e testes SFTP/SCP/FTP têm tempo limite real (ligação 15 s, operação 30 s); transferências SCP e TFTP param ao cancelar ou ao estourar o limite de 30 minutos.
- **Porta ocupada ao iniciar um servidor:** o app só recusa se a porta estiver em uso por outro processo **no mesmo transporte** do servidor (TCP para FTP, SFTP e SCP; UDP para TFTP). Um programa usando UDP na porta 2121 não impede mais um servidor FTP (TCP) de usar a 2121.
- **Indicador de saúde dos perfis de rede:** a cada 5 minutos verifica só se o servidor atende na porta (sem enviar credenciais) e faz o login real; depois de 2 logins recusados seguidos, o login automático é suspenso até um teste manual ou até mudar as credenciais. O TFTP não tem verificação automática.
- **Pastas enormes:** uma pasta com 100 mil arquivos abre em cerca de 7 s e a tela não congela (a medição era de mais de 3 minutos, com a tela congelada por mais de 2). Os nós são criados em lotes, sem travar a janela, e entram na árvore de uma só vez: adicionar item a item à coleção já ligada à tela custava de 0,8 a 4 ms por item e crescia com o tamanho da pasta. A tela só mantém os itens visíveis (algumas dezenas) e, em pastas muito grandes, os containers são percorridos sem procurar cada item na lista inteira.
- **Memória após pastas enormes:** depois de uma carga de 10 mil itens ou mais, e quando a tela já assentou (2 s sem nova carga), o app faz uma coleta compactante e devolve ao Windows a memória reservada que não tem mais dono. Com 100 mil arquivos o app em repouso ficou em cerca de 280 MB de memória de trabalho (eram cerca de 540 MB), 190 MB de memória privada. A revalidação em segundo plano também deixou de ordenar de novo a lista inteira só para contar subpastas.
- **Recarregar pasta:** uma pasta já carregada não é lida de novo por estar sendo expandida por código (restaurar o caminho, seguir um caminho até a pasta, recolher e expandir em acordeão sozinho). Ela só é recarregada ao ser **aberta de novo depois de fechada pelo usuário** (clique na seta ou teclado), o que também mostra arquivos criados fora do app. A listagem que já estava na tela continua lá e o disco é relido por trás: só o que mudou entra, sai ou muda de lugar, então os itens não piscam (medido: antes sumiam por ~100 ms e reapareciam; agora o conteúdo só cresce até o final). Na abertura, cada pasta é carregada uma única vez por painel.
- **Acesso negado:** a falta de permissão é reconhecida em excluir (Lixeira e exclusão definitiva), renomear, criar pasta, copiar, mover, compactar, extrair de arquivo compactado e restaurar da Lixeira; a janela de aviso troca o **OK** por **Reiniciar como administrador** / **Agora não** (só quando o app ainda não é administrador). Copiar e mover citam a origem **e** o destino, porque o Windows só responde "acesso negado" sem dizer qual dos dois recusou. Um arquivo **somente leitura** é explicado como tal e não oferece reiniciar (ser administrador não remove esse atributo). Outras falhas comuns também saem em português: arquivo em uso por outro programa, disco cheio, caminho longo demais, pasta que não está vazia, item que não existe mais. Ao abrir uma pasta que o Windows não deixa listar, antes ela aparecia **vazia** sem aviso (e essa listagem vazia ficava gravada no índice); agora o app avisa uma vez por pasta e por sessão, no momento em que a pasta é aberta (duplo clique, Enter ou seta para a direita; um clique só seleciona). Na linha, um **cadeado** toma o lugar das contagens "0 pastas, 0 arquivos" assim que a pasta pai é listada, nas linhas compactas e nas confortáveis, e a dica diz "Sem permissão para abrir esta pasta"; a barra de status da pasta selecionada diz "sem permissão para abrir" em vez de "0 pastas · 0 arquivos"; o cadeado e esse texto somem quando a pasta passa a poder ser listada (por exemplo depois de reiniciar como administrador). O app não grava a listagem e esquece, ao abrir, as listagens vazias antigas, que podem ter sido causadas por falta de permissão. Se o usuário recusar a confirmação do Windows (ou o aviso de um programa que exige administrador ao abrir um arquivo), o app continua aberto e não mostra erro
- **Abertura do app:** as unidades são lidas em paralelo, com limite de 3 segundos; uma unidade de rede offline aparece como indisponível em vez de travar a inicialização.
- **Configurações:** gravadas de forma atômica; um `settings.json` ilegível é guardado como `settings.json.corrupt-AAAAMMDD-HHMMSS` antes de repor os padrões.

## Segurança

- **Senhas** (perfis de cliente e servidores) são guardadas em `%AppData%\DoraExplorer\settings.json` cifradas com DPAPI, no escopo do usuário Windows. O arquivo copiado para outro usuário ou máquina perde as senhas (serão pedidas de novo). Arquivos antigos em texto puro são convertidos na primeira execução.
- **SFTP/SCP (cliente):** na primeira ligação a um servidor é mostrada a impressão digital da chave para confirmação; ela fica em `%AppData%\DoraExplorer\known_hosts.json`. Se a chave mudar, a ligação é recusada, a menos que o usuário confirme a substituição. A verificação periódica em segundo plano nunca abre diálogos.
- **Servidor FTP embutido:** exige usuário e senha, limita tentativas de login (3), clientes simultâneos (8) e tempo ocioso, e só aceita a conexão de dados vinda do mesmo IP do cliente. Aceita FTPS explícito (AUTH TLS, RFC 4217) e a criptografia é opcional por padrão: um cliente que não pede TLS (ou o modo "Desativado") trafega em texto puro, senha inclusive; o modo "Obrigatório" recusa o login antes de a sessão ser criptografada.
- **Servidor TFTP embutido:** não tem autenticação (limitação do protocolo). Grava via arquivo temporário, só substitui o destino ao concluir, aceita até 256 MB por envio e 8 transferências simultâneas.
- **Pasta compartilhada:** é obrigatório escolher uma pasta dedicada; a raiz de uma unidade e a pasta do usuário (ou um ancestral dela) são recusadas.
- **Servidor SFTP/SCP embutido:** o protocolo SSH 2 (troca de chaves, criptografia e autenticação) vem da biblioteca Microsoft.DevTunnels.Ssh; nada de criptografia foi escrito à mão. Só autenticação por senha, comparada em tempo constante, com pausa de 1 s a cada erro, 3 tentativas por conexão e bloqueio do endereço por 10 min depois de 6 falhas em 10 min. Só aceita canais de sessão: nada de shell, terminal, execução de comandos ou túneis de porta, e cada servidor atende só o seu protocolo (o SFTP não responde ao `scp` e vice-versa). A chave do servidor é ECDSA P-384, criada uma vez e guardada com DPAPI em `%AppData%\DoraExplorer\ssh-host-key.bin` (a impressão digital SHA256 aparece no log ao iniciar), para o cliente não estranhar a identidade a cada início; o modo de cifra CBC fica desativado. Os caminhos que o cliente pede são confinados à pasta compartilhada (`..` não passa da raiz; letra de unidade, `:`, nomes reservados como CON, NUL e COM1, e links ou junções que apontem para fora são recusados). Limites: 16 clientes (8 por endereço), 64 arquivos abertos por sessão, pacotes de até 1 MiB, 5 min de inatividade e 60 s para se autenticar. Um envio interrompido apaga o arquivo que ele criou (SFTP) ou o temporário (SCP). No SCP um arquivo que já existia nunca fica pela metade; no SFTP, sobrescrever grava direto no arquivo (como a maioria dos servidores), então uma queda no meio deixa o arquivo parcial. O log nunca recebe a senha.
- **Arquivos compactados:** entradas de ZIP/TAR/7Z/RAR com caminho fora da pasta de destino (`..`, caminho absoluto, `:`) bloqueiam a extração da pasta inteira.
- **Dependências:** os pacotes NuGet são conferidos com `dotnet list DoraExplorer.sln package --vulnerable --include-transitive` (resultado atual: nenhum vulnerável). O SQLite embutido (índice de pastas) precisa ser 3.50.2 ou mais novo por causa da CVE-2025-6965 (corrupção de memória); o `Microsoft.Data.Sqlite` 10.0.9 trazia o SQLite 3.49.1, e a atualização para 10.0.14 resolveu. Um teste (`SqliteSecurityTests`) falha se a versão voltar a ser antiga.

## Tecnologias

- **C#** / **.NET 8**
- **WPF** (Windows Presentation Foundation)
- **CommunityToolkit.Mvvm** 8.4 — MVVM
- **Microsoft.Extensions.Hosting / DependencyInjection** — injeção de dependências
- **FluentFTP** — cliente FTP
- **SSH.NET** — cliente SFTP e SCP
- **Microsoft.DevTunnels.Ssh** — servidor SFTP e SCP (protocolo SSH 2)
- **SharpCompress** — leitura de RAR e 7Z
- **Microsoft.Data.Sqlite** — índice de pastas
- **Serilog** — logs em ficheiro
- **System.Management** — partições não mapeadas
- **System.Security.Cryptography.ProtectedData** — DPAPI para senhas
- **xUnit** e **FluentAssertions** — testes

## Limitações conhecidas

- TFTP transfere um ficheiro de cada vez, sem listagem de diretório: as operações em pastas remotas valem para FTP, SFTP e SCP
- Em servidores, **Nova pasta** cria com um nome livre ("Nova pasta (1)", ...) e não entra em renomeação em linha: use **Renomear** depois
- **Abrir** um arquivo de servidor baixa uma cópia temporária e a abre no programa do Windows; alterações feitas nela não voltam ao servidor
- **Compactar** com itens ou destino em servidor baixa/envia por uma pasta temporária, e a barra de progresso mostra uma etapa de cada vez
- Um `.zip` criado com método LZMA ou BZip2 (opção do 7-Zip) não abre: o .NET só lê Deflate e Deflate64. A mensagem de erro diz qual é o método
- Arquivos `.7z` e `.rar` sólidos: extrair uma pasta inteira lê o arquivo uma vez só e é rápido (43 arquivos de 11,6 MiB: 0,7 s); já copiar arquivos soltos de dentro dele lê o bloco desde o começo até cada arquivo, então demora mais quanto maior o arquivo e quanto mais arquivos forem selecionados (o mesmo 7z, um arquivo por vez: 13 s no total). Um `.7z` com PPMd também usa muita memória (829 MiB medidos nesse mesmo arquivo)
- Servidor SFTP/SCP: só um usuário e só senha (sem chave pública), sem links simbólicos, sem shell nem túneis; o SFTP é a versão 3, com a extensão `posix-rename@openssh.com`
- Ao reabrir uma pasta que você fechou, as pastas do disco são revalidadas sem piscar; pastas dentro de arquivo compactado, a Lixeira, os Favoritos e os caminhos de rede ainda são recarregados do zero, e a lista pode piscar
- Pastas com mais de 1000 itens trocam a lista inteira de uma vez quando o disco mudou (pode piscar uma vez); sem mudança, nada é tocado
- Reiniciar como administrador fecha o app e abre outro: a operação que falhou não é refeita sozinha (repita-a), e o Windows pode impedir **arrastar e soltar** vindo de programas que não são administradores (como o Explorer) para o Dora Explorer (regra do Windows; não foi conferido nesta máquina); copiar e colar entre os painéis continua valendo
- Mesmo como administrador alguns itens do sistema (por exemplo, pastas de `C:\Windows` ou `C:\inetpub`, que pertencem ao TrustedInstaller) continuam sem permissão para excluir ou mover: o app explica, mas não toma posse dos itens
- A pesquisa (**Localizar**) ignora, sem avisar, as pastas em que não tem permissão para entrar
- Ao abrir uma pasta pelo teclado, as pastas irmãs que o acordeão recolhe não são marcadas para recarregar na próxima abertura (fechar uma pasta pelo teclado ou pelo clique marca)
- Ao abrir uma pasta, a lista dos filhos cresce de altura 0 até a altura dos que cabem na tela, no ritmo em que eles entram em cascata, e as linhas abaixo da pasta descem junto. Antes elas sumiam de uma vez e voltavam durante a animação (a árvore perdia de 41% a 83% do conteúdo num único quadro, o "pisca"); agora a maior queda em um quadro é de 3% a 15%. Em troca, a primeira mudança na tela vem de 10 a 60 ms depois, porque a lista é medida antes de crescer. Numa pasta com centenas de itens, o resto da lista (fora da tela) entra de uma vez no fim da animação, e a barra de rolagem muda de tamanho nesse momento
- Prévia de PDF depende de o Windows ter um provedor de miniaturas de PDF: num computador só com o Acrobat, por exemplo, o Explorer também não mostra miniatura e o Dora Explorer não mostra prévia (imagens sempre funcionam)

## Licença

Projeto em desenvolvimento interno — sem licença pública definida.
