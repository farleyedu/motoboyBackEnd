# Integracao Front: Chat e Garagem

## Premissas
- Todas as rotas exigem `Authorization: Bearer <jwt>`, exceto as rotas publicas explicitamente marcadas.
- O estabelecimento ativo continua vindo do JWT apos `POST /api/auth/definir-estabelecimento`.
- O front nao deve mais mockar status, eventos, unread count, simulacoes ou arquivos nessas duas areas.

## Chat
### Status canonicos
- `com_bot`
- `em_andamento`
- `aguardando_cliente`
- `aguardando_interno`
- `encerrada_manual`
- `encerrada_inatividade`

### Identificadores
- `id` em `GET /conversas` continua sendo o item visual da lista.
- `idConversaOperacional` e o ID que o front deve usar para assumir, mudar status, enviar mensagem, fechar, reabrir, marcar como lida e voltar ao bot.
- `idConversaGrupo` identifica agrupamento por cliente/thread central.

### Listagem
- `GET /conversas`
- Query opcional: `estado`, `responsavel`, `incluirArquivadas`
- Campos principais por item:
  - `id`
  - `idCliente`
  - `idConversaOperacional`
  - `idConversaGrupo`
  - `clienteNome`
  - `clienteNumero`
  - `estado`
  - `idAgenteAtribuido`
  - `agenteNome`
  - `qtdNaoLidas`
  - `ultimaMensagemConteudo`
  - `ultimaMensagemData`

### Historico
- `GET /conversas/{id}/mensagens?page=1&pageSize=50`
- Resposta:
  - `conversa`
  - `controle`
  - `eventos`
  - `mensagens`
  - `page`
  - `pageSize`
  - `total`

### Controle
- `controle.conversationId` sempre aponta para a conversa operacional real.
- `controle.status` e a fonte oficial do backend.
- `controle.unreadCount` substitui qualquer contador calculado no front.

### Eventos
- Tipos usados:
  - `started`
  - `assigned`
  - `status_changed`
  - `closed`
  - `reopened`
  - `returned_to_bot`
- O backend devolve eventos persistidos e eventos base derivados da conversa.
- O front nao deve sintetizar timeline se `eventos` vier preenchido.

### Acoes operacionais
- `POST /conversas/{id}/assign`
  - body: `{ "idAgente": 1 }`
- `POST /conversas/{id}/back-to-bot`
- `PATCH /conversas/{id}/status`
  - body: `{ "status": "aguardando_cliente" }`
- `POST /conversas/{id}/close`
  - body: `{ "motivo": "...", "tipo": "manual|inatividade" }`
- `POST /conversas/{id}/reopen`
  - body: `{ "origem": "atendente|cliente|sistema", "motivo": "..." }`
- `POST /conversas/{id}/read`
- `POST /conversas/{id}/mensagens`
  - body: `{ "mensagem": "texto" }`
- `GET /conversas/agentes`
  - Campos principais por item:
    - `id`
    - `usuarioId`
    - `nome`

### Regras importantes para o front
- Ao abrir uma conversa, chamar `POST /read`.
- Envio manual so funciona em status humano aberto.
- `assign` muda a conversa para `em_andamento`.
- `back-to-bot` limpa atribuicao e volta status para `com_bot`.
- `reopen` volta a conversa para `com_bot`.
- O front nao precisa ter um seletor livre de status.
- `PATCH /status` fica como rota opcional para fluxos especificos como `aguardando_cliente` e `aguardando_interno`.
- `assign` e `handover` antigo convivem; o front novo deve preferir `POST /conversas/{id}/assign`.
- Rotas legadas mantidas por compatibilidade:
  - `POST /automation/agent/reply`
  - `POST /automation/conversation/{id}/back-to-bot`
  - `POST /automation/conversation/{id}/handover`

## Garagem
### Leads
- `GET /garagem/leads`
  - query: `busca`, `status`, `objetivo`, `pagina`, `tamanhoPagina`
- `GET /garagem/leads/{idLead}`
- `PATCH /garagem/leads/{idLead}/status`
  - body: `{ "status": "pendente|em_andamento|concluido|cancelado" }`

### Simulacoes
- `POST /garagem/leads/{idLead}/simulacoes`
- `PATCH /garagem/leads/{idLead}/simulacoes/{idSimulacao}`
- `DELETE /garagem/leads/{idLead}/simulacoes/{idSimulacao}`

### Campos suportados em criacao/edicao
- `titulo`
- `status`
- `tipoSimulacao`
- `veiculoMarca`
- `veiculoModelo`
- `veiculoVersao`
- `veiculoAno`
- `veiculoKm`
- `veiculoValor`
- `entradaValor`
- `saldoFinanciado`
- `parcelasQuantidade`
- `parcelaValor`
- `taxaJurosMensal`
- `observacoes`
- `validadeEm`

### Status de simulacao
- `rascunho`
- `enviada`
- `aprovada`
- `recusada`
- `expirada`

### Arquivos de simulacao
- `POST /garagem/leads/{idLead}/simulacoes/{idSimulacao}/arquivos`
  - `multipart/form-data`
  - campo: `file`
- `DELETE /garagem/leads/{idLead}/simulacoes/{idSimulacao}/arquivos/{idArquivo}`

### Fluxo recomendado no front
1. Criar ou editar a simulacao via JSON.
2. Receber `id` da simulacao.
3. Subir anexos um por vez em `multipart/form-data`.
4. Refazer `GET /garagem/leads/{idLead}`.
5. Renderizar anexos reais com `url`, `contentType`, `tamanho` e `data`.

### Regras de arquivo
- Extensoes permitidas:
  - `.pdf`
  - `.png`
  - `.jpg`
  - `.jpeg`
  - `.webp`
  - `.doc`
  - `.docx`
  - `.xls`
  - `.xlsx`
  - `.txt`
- Limite: `15 MB`
- Os arquivos ficam servidos pelo proprio backend via `UseStaticFiles()`.

### Veiculos
- `GET /api/garagem/veiculos`
  - query: `estabelecimentoId`, `status`, `categoria`, `search`, `page`, `pageSize`
  - resposta:
    - `items`
    - `total`
    - `page`
    - `pageSize`
- `GET /api/garagem/veiculos/{id}`
- `POST /api/garagem/veiculos`
  - criacao minima:
    - `idEstabelecimento` quando o JWT nao trouxer estabelecimento ativo
    - `marca`
    - `modelo`
    - `categoria`
    - `anoModelo`
    - `preco`
    - `tipoVeiculo`
    - `km` apenas para `tipoVeiculo = seminovo`
  - defaults aplicados no backend:
    - `titulo = marca + modelo + anoModelo`
    - `anoFabricacao = anoModelo` quando omitido
    - `status = disponivel` quando omitido
    - `codigoEstoque` gerado automaticamente quando omitido
    - `destaque = false`
  - nomes corretos do body:
    - `idEstabelecimento` em vez de `estabelecimentoId`
    - `preco` em vez de `precoVenda`
    - `status` em vez de `statusVenda`
    - `condicoes[].item` em vez de `condicoes[].label`
    - `condicoes[].nota` em vez de `condicoes[].note`
- `PUT /api/garagem/veiculos/{id}`
  - continua exigindo payload completo
- `DELETE /api/garagem/veiculos/{id}`
- `PATCH /api/garagem/veiculos/{id}/status`
  - body: `{ "status": "disponivel|indisponivel|vendido" }`
- `PATCH /api/garagem/veiculos/{id}/destaque`
  - body: `{ "destaque": true, "label": "Mais procurado" }`
- `GET /api/garagem/metricas`
  - query: `estabelecimentoId`
  - resposta:
    - `total`
    - `disponiveis`
    - `emDestaque`
    - `ticketMedio`

### Vitrine publica da garagem
- `GET /api/garagem/vitrine`
  - publico, sem token
  - query: `estabelecimentoId`, `categoria`, `search`
  - resposta:
    - `items`
    - `destaques`

### Contrato do veiculo retornado
- Campos do JSON:
  - `id`
  - `slug`
  - `title`
  - `brand`
  - `model`
  - `category`
  - `year`
  - `modelYear`
  - `price`
  - `oldPrice`
  - `km`
  - `fuel`
  - `transmission`
  - `color`
  - `city`
  - `body`
  - `doors`
  - `seats`
  - `status`
  - `condition`
  - `featured`
  - `spotlightLabel`
  - `stockCode`
  - `driveType`
  - `description`
  - `optionals`
  - `gallery`
  - `conditionItems`

### Regras de negocio dos veiculos
- `slug` e gerado no backend a partir de `marca-modelo-anoModelo` em kebab-case sem acento.
- Se o `slug` base ja existir, o backend adiciona sufixo numerico.
- `codigoEstoque` e unico por estabelecimento e passa a ser gerado automaticamente quando omitido no `POST`.
- `gallery` respeita a ordem do array `fotos` enviado no `POST` e `PUT`.
- `PUT` substitui completamente `fotos`, `opcionais` e `condicoes`.
- Permissoes aceitas no backend para esse modulo:
  - `Garagem.visualizar`
  - `Garagem.cadastrar` ou `Garagem.criar`
  - `Garagem.editar`
  - `Garagem.excluir` ou `Garagem.deletar`

## Erros esperados
- `401`: token invalido ou expirado
- `403`: usuario sem permissao ou tentando forcar outro estabelecimento
- `404`: conversa, lead, simulacao ou arquivo nao encontrado
- `409`: transicao invalida no chat
- `422`: regra de negocio ou validacao funcional falhou

## Checklist de integracao do front
- Trocar qualquer operacao de chat para usar `idConversaOperacional`.
- Remover mocks de timeline, unread, autoclose e controle de atendimento.
- Considerar `controle.status` como fonte da verdade.
- Na garagem, separar criacao/edicao da simulacao do upload dos arquivos.
- Exibir anexos usando `url` retornada pelo backend.
