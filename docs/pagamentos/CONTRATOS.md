# Pagamentos Mercado Pago — contratos (fonte de verdade)

Este documento é o contrato que as sessões 2 a 7 seguem. Quem precisar mudar um contrato muda **aqui primeiro** e avisa as outras sessões.
Código do núcleo: `Payments/MercadoPago/` e `Payments/Controllers/PagamentosControllers.cs`. Migration: `Migrations/Delivery/20261031_01_mercadopago_nucleo.sql`.

| Sessão | Escopo | Usa deste documento |
|---|---|---|
| 1 | Núcleo (feito): tabelas, credenciais, cliente MP, Pix, cartão, estorno, webhook, conciliador, outbox, rotas públicas do cardápio | — |
| 2 | Gestão `/api/gestao/pagamentos/*` + OAuth da loja `/api/v2/integracoes/mercadopago*` (feito: `Payments/Gestao`, `Payments/Conexao`) | §1, §2.1–2.4, §2.16–2.18, §4.1, §8.1, §8.2 |
| 3 | Cardápio web (Pix + cartão com Payment Brick) | §4.2, §4.3, §6, §8.3 |
| 4 | Assinaturas Zippy `/api/v2/comercial/*` | §2.5, §4.2, §5.3, §8.4 |
| 5 | Cobrança Pix do atendente no chat (backend) | §2.6, §3.3, §4.2, §4.4, §6, §8.5 |
| 6 | Interface do chat, balão animado, página Pix pública (frontend) | §3.3, §6, §7, §8.5, §8.6 |
| 7 | Homologação | §9, §10 |

Fora do escopo (não implementar): robô fechando a venda inteira no WhatsApp, outros gateways, cobrança de comissão, carteira/custódia.
Errata do dono: o cardápio web aceita **Pix e cartão**; a cobrança do atendente no WhatsApp é **só Pix** e usa o **mesmo** `IPixChargeService` do cardápio (sem checkout separado, sem cartão no chat — o serviço de cartão recusa `origem=chat`).

---

## 1. Contas, credenciais e ambientes

Três contas que **nunca** se misturam:

| Conta | Tabela | O que guarda | Para que serve | Contexto (`ContextoCredencial`) |
|---|---|---|---|---|
| (a) **Aplicação MP da Zippy** | `mp_aplicacao` (1 linha por ambiente) | client_id, client_secret (cifrado), public_key, segredo do webhook (cifrado), redirect_uri | **Só** OAuth das lojas e validação do webhook. Nunca cria/consulta pagamento. | `Aplicacao` → tipo `CredencialAplicacao` |
| (b) **Recebedora Zippy** | `mp_conta_zippy` (1 por ambiente) | mp_user_id, access_token (cifrado), public_key | Recebe mensalidades e extras (receita da plataforma) | `RecebedorZippy` → `CredencialRecebedora` |
| (c) **Recebedora da loja** | `mp_conta_loja` (≤ 1 viva por loja+ambiente) | mp_user_id (+ mascarado), access/refresh token (cifrados), public_key, expiração, estado | Recebe vendas do cardápio e cobranças do chat | `Loja` → `CredencialRecebedora` |

Regra origem → conta (no código `CredencialRegras.ContextoDaOrigem` e no banco `ck_pagamento_intent_conta`):

| origem | conta |
|---|---|
| `cardapio`, `chat` | loja (`id_conta_loja`) |
| `assinatura`, `compra_extra` | Zippy (`id_conta_zippy`) |

- O cliente HTTP (`IMercadoPagoClient`) só aceita `CredencialRecebedora` para pagamento e `CredencialAplicacao` para OAuth: usar o token errado não compila.
- `IMercadoPagoCredencialResolver.ParaIntentAsync(intent)` devolve **exatamente** a conta gravada na intenção (mesmo que a loja tenha conectado outra conta depois) e chama `CredencialRegras.Exigir` (loja, conta e origem conferidos).
- **Ambientes** `sandbox` e `producao` são linhas separadas em todas as tabelas. O ambiente ativo do servidor é `Payments:MercadoPago:Ambiente`. Pagamento/aviso com `live_mode` diferente do ambiente é rejeitado (`ExigirLiveModeDoAmbiente`, padrão `true`).
- **Cifragem**: `IPagamentoSegredoProtector` (AES-256-GCM). Chave mestra em `Payments:ChaveMestra` (env `Payments__ChaveMestra`) ou `PAGAMENTOS_CHAVE_MESTRA`: **base64 de exatamente 32 bytes** (`openssl rand -base64 32`). Rotação: chave antiga em `Payments:ChaveMestraAnterior` (só abre). O contexto (AAD) é obrigatório e padronizado em `SegredoContexto`:

  | Coluna | Contexto |
  |---|---|
  | `mp_aplicacao.client_secret_cifrado` | `SegredoContexto.AplicacaoClientSecret(id)` |
  | `mp_aplicacao.webhook_secret_cifrado` | `SegredoContexto.AplicacaoWebhookSecret(id)` |
  | `mp_conta_zippy.access_token_cifrado` | `SegredoContexto.ZippyAccessToken(id)` |
  | `mp_conta_loja.access_token_cifrado` | `SegredoContexto.LojaAccessToken(id)` |
  | `mp_conta_loja.refresh_token_cifrado` | `SegredoContexto.LojaRefreshToken(id)` |
  | `mp_oauth_tentativa.code_verifier_cifrado` | `SegredoContexto.OAuthCodeVerifier(id)` |

- **Nunca** devolver token/segredo em GET, nunca logar (os tipos `Credencial*` têm `ToString()` sem segredo; o cliente loga só operação, HTTP e `x-request-id`). Respostas mostram no máximo `mpUserIdMascarado` (`CredencialRecebedora.MascararId`) e `temSegredo: true/false`.
- Bootstrap: enquanto `mp_aplicacao` não tem a linha do ambiente ativo, valem `Payments:MercadoPago:Bootstrap{ClientId,ClientSecret,PublicKey,WebhookSecret,RedirectUri}` (só para subir o sandbox antes da tela da Gestão). Conta Zippy e contas de loja são **só banco**.
- Token da loja: o resolvedor renova sozinho quando faltam < 7 dias (`IContaLojaTokenRenovador`, linha travada, refresh gira). Falha de refresh 400/401/403 → conta `expirada` e erro `PAG_CONTA_LOJA_REAUTORIZAR`.
- Split/comissão: `SplitHabilitado=false`, `ComissaoPercentual=0`. O campo `application_fee` só é enviado com split ligado e origem de loja. **Não ligar.**

---

## 2. Modelo de dados

Todas as tabelas: migration `20261031_01_mercadopago_nucleo.sql` (aditiva, idempotente; rollback em `rollback/20261031_01_mercadopago_nucleo.down.sql`; verificação em `20261031_02_mercadopago_nucleo_verify.sql`). As tabelas `checkout_*` do Asaas ficam como histórico (não apagar, não usar).

### 2.1 `mp_aplicacao`
`id uuid`, `ambiente`, `client_id` (numérico), `client_secret_cifrado`, `public_key`, `webhook_secret_cifrado`, `redirect_uri`, `ativo`, `atualizado_por`, timestamps. Único por `ambiente`.

### 2.2 `mp_conta_zippy`
`id`, `ambiente` (único), `mp_user_id`, `mp_user_id_mascarado`, `access_token_cifrado`, `public_key`, `estado` (`ativa|inativa|erro`), `ultimo_erro`, `validada_em`, `atualizado_por`.

### 2.3 `mp_conta_loja`
`id`, `id_estabelecimento`, `ambiente`, `mp_user_id`, `mp_user_id_mascarado`, `access_token_cifrado`, `refresh_token_cifrado`, `public_key`, `escopos text[]`, `live_mode`, `token_expira_em`, `estado` (`ativa|expirada|revogada|desconectada|erro`), `ultimo_erro`, `ultimo_refresh_em`, `conectado_por/em`, `desconectado_por/em`.
- Índice único parcial `ux_mp_conta_loja_viva (id_estabelecimento, ambiente) WHERE estado IN ('ativa','expirada','erro')`.
- `desconectada`/`revogada` **apagam os tokens** (CHECK `ck_mp_conta_loja_tokens`) mas a linha fica (pagamentos antigos apontam para ela).
- Reconectar = desconectar a linha viva e inserir uma nova (nunca sobrescrever `mp_user_id` de uma linha com pagamentos).

### 2.4 `mp_oauth_tentativa`
`id`, `id_estabelecimento`, `ambiente`, `usuario_id`, `state_hash` (SHA-256 do state; único), `code_verifier_cifrado` (PKCE), `estado` (`iniciada|concluida|cancelada|expirada|erro`), `erro_codigo`, `id_conta_loja`, `expira_em`, `concluida_em`.

### 2.5 `pagamento_preferencia_loja`
PK `id_estabelecimento`; `pix_ativo` (padrão **false**), `cartao_ativo` (false), `presencial_ativo` (true: dinheiro/maquininha, fora do MP), `pix_validade_minutos` (5–1440, padrão 30), `cartao_parcelas_max` (1–12). Sem linha = tudo desligado menos presencial.

### 2.6 `pagamento_intent` (o "quanto, por quê e para quem")
`id`, `id_estabelecimento`, `ambiente`, `origem` (`cardapio|chat|assinatura|compra_extra`), `conta_tipo` (`loja|zippy`), `id_conta_loja`, `id_conta_zippy`, `pedido_id int`, `pre_pedido_id uuid → cardapio_pedido_publico`, `conversa_id → conversas`, `cliente_id → clientes`, `origem_ref_id` (assinatura → id do ciclo; compra_extra → id da compra), `valor numeric(12,2) > 0`, `moeda='BRL'`, `descricao`, `status`, `versao`, `external_reference` (único), `idempotency_key` (único por loja), `valor_pago`, `valor_estornado (≤ valor_pago)`, `token_publico_hash` (link público), `expira_em`, `pago_em`, `criado_por`.
- `external_reference` = `zp_{s|p}_{origem}_{intentId sem hífens}`. Nada de dado do cliente. O webhook só aceita pagamento que volta com ele.

### 2.7 `pagamento_tentativa`
Cada chamada de criação ao MP: `id`, `id_intent`, `numero`, `metodo` (`pix|cartao`), `idempotency_key` (único; vai no header `X-Idempotency-Key`), `status` (`criando|criada|falhou`), `mp_payment_id`, `erro_codigo/mensagem`. Tentativa `criando` = chamada sem resposta: repetir usa a **mesma** chave; o conciliador recupera pelo `external_reference`.

### 2.8 `pagamento`
Retrato do MP: `id`, `id_intent`, `id_tentativa`, `id_estabelecimento`, `ambiente`, `mp_payment_id` (único por ambiente), `mp_collector_id`, `metodo`, `payment_method_id`, `payment_type_id`, `status` (ver §3.2), `mp_status`, `mp_status_detail`, `valor`, `valor_estornado`, `moeda`, `pix_copia_cola`, `pix_qr_code_base64`, `pix_ticket_url`, `cartao_bandeira`, `cartao_final` (4 dígitos), `parcelas`, `live_mode`, `expira_em`, `aprovado_em`, `mp_atualizado_em` (date_last_updated do MP), `versao`, `conciliado_em`.
**Nunca** guardar número de cartão, CVV, token do Brick ou documento do pagador.

### 2.9 `cobranca_chat` e `cobranca_chat_evento`
`cobranca_chat`: `id`, `id_estabelecimento`, `conversa_id`, `id_intent`, `autor_usuario_id`, `descricao` (1–140), `valor`, `tipo` (`avulsa|vinculada`; vinculada exige `pedido_id` ou `pre_pedido_id`), `pedido_id`, `pre_pedido_id`, `status` (§3.3), `versao`, `validade_minutos`, `expira_em`, `enviado_em`, `id_mensagem` (mensagem do chat que levou o link), `pago_em`, `cancelado_em/por`, `motivo`, `reemitida_de`, `idempotency_key` (único por loja).
Índice `ux_cobranca_chat_aberta_pedido`: no máximo uma cobrança aberta (`criando|aguardando|enviado`) por conversa + pedido.
`cobranca_chat_evento`: histórico (`status_anterior`, `status_novo`, `ator_tipo` `usuario|sistema|webhook|conciliador`, `usuario_id`, `detalhe jsonb`). É a fonte de `/historico`.

### 2.10 `pagamento_estorno`
`id`, `id_pagamento`, `id_estabelecimento`, `valor > 0`, `status` (§3.4), `mp_refund_id`, `motivo`, `origem` (`usuario|mercadopago`), `solicitado_por`, `idempotency_key` (único), `erro_mensagem`, `concluido_em`.
Trigger `tg_pagamento_estorno_limite`: soma dos estornos vivos (não `recusado`/`falhou`) ≤ valor do pagamento (linha do pagamento travada).

### 2.11 `pagamento_disputa`
Chargebacks, mediações e reclamações: `tipo` (`chargeback|mediacao|reclamacao`), `mp_referencia` (único por tipo; `pagamento:{amb}:{mpId}` ou `chargeback:{amb}:{id}`), `status` (`aberta|em_analise|ganha|perdida|cancelada`), `valor`, `prazo_resposta_em`, `detalhe`.

### 2.12 `mp_webhook_evento`
`id`, `ambiente`, `topico`, `acao`, `data_id`, `mp_notification_id`, `mp_request_id`, `mp_user_id`, `live_mode`, `assinatura_ts`, `chave_dedup` (único por ambiente: `topico|data_id|acao|notification_id`), `payload`, `status` (`recebido|processando|processado|ignorado|rejeitado|falhou`), `tentativas`, `proxima_tentativa_em`, `ultimo_erro`, `recebido_em`, `processado_em`.

### 2.13 `pagamento_auditoria`
Somente inserção (trigger bloqueia UPDATE/DELETE). `entidade`, `entidade_id`, `acao`, `ator_tipo`, `ator_id`, `dados jsonb` (status/valores/ids; nunca segredo). Helper: `PagamentoAuditoria.RegistrarAsync(cx, tx, ...)`. Sessão 2 registra aqui: conta conectada/desconectada, preferência alterada, aplicação/conta Zippy alteradas (sem segredo).

### 2.14 `pagamento_outbox`
`event_id`, `id_estabelecimento`, `evento` (`pagamentoAtualizado|cobrancaAtualizada`), `grupo`, `payload`, `tentativas`, `ultimo_erro`, `publicado_em`. Gravada na **mesma transação** da mudança; `PagamentoOutboxPublisher` publica depois do commit.

### 2.16 `mp_ambiente_estado` (sessão 2, migration `20261101_20_pagamentos_gestao_oauth.sql`)
PK `ambiente`; `habilitado`, `lojas_podem_conectar` (CHECK: só com `habilitado`), `id_verificacao`, `habilitado_por/em`. Linhas `sandbox` e `producao` criadas desligadas.
- Ligar `habilitado` exige a **última** `mp_verificacao` do ambiente **aprovada** e mais nova que a última troca em `mp_aplicacao`/`mp_conta_zippy` (vale para sandbox e produção). Desligar é livre.
- `POST /api/v2/integracoes/mercadopago/conectar` só funciona com `habilitado AND lojas_podem_conectar` no ambiente ativo (`422 PAG_AMBIENTE_NAO_LIBERADO`).

### 2.17 `mp_verificacao`
`id`, `ambiente`, `aprovada`, `etapas jsonb` (`[{ etapa, ok, codigo, mensagem }]`, sem segredo), `executada_por`, `created_at`. Etapas: `chave_mestra`, `aplicacao` (grant `client_credentials` no MP), `callback` (redirect_uri https e igual a `{api}/api/integracoes/mercadopago/callback`), `webhook_segredo` (assinatura secreta + `NotificationUrl` https), `recebedor_zippy` (`GET /users/me`: mesmo user_id e conta ativa), `recebedor_pix` (`GET /v1/payment_methods` tem `pix`), `webhook_recebido` (algum aviso gravado nos últimos 7 dias — só é gravado com assinatura válida; "Simular notificação" do painel serve).

### 2.18 `pagamento_politica` (linha única `id=1`)
`tolerancia_dias` (7; 0–30), `avisos_antes_dias` (`{3,1}`), `avisos_depois_dias` (`{1,3,6}`), `alcada_estorno_gerente` (200: acima, só dono estorna), `alcada_estorno_confirmacao` (1000: estorno pela Gestão acima pede confirmação digitada), `alerta_falhas_webhook` (5/24 h). A sessão 2 só grava e audita; **quem aplica** é quem consome (sessão 4: tolerância e avisos; estornos: alçadas).

### 2.15 Decisão: assinaturas ficam para a sessão 4
Não criei tabelas de assinatura, ciclo, compra de extra, solicitação de compra nem política comercial: o modelo comercial (planos em `pacotes_comerciais`, avulsos em `produtos_avulsos`) é da sessão 4 e um esqueleto aqui viraria migration para desfazer. O núcleo já entrega o que elas consomem:
- `pagamento_intent` com `origem` `assinatura|compra_extra`, conta Zippy obrigatória, `origem_ref_id` apontando para o ciclo/compra;
- `IPixChargeService` (mesmo serviço) e `ICardPaymentService` aceitam essas origens e resolvem a conta Zippy;
- `IPagamentoEfeitoHandler` (§4.5) para reagir ao pagamento dentro da transação;
- `IMercadoPagoTopicoHandler` (§5.3) para `subscription_preapproval` / `subscription_authorized_payment`.
A sessão 4 cria as suas tabelas numa migration própria `2026xxxx_NN_assinaturas*.sql` com FKs para `pagamento_intent(id)` quando fizer sentido.

---

## 3. Máquinas de estado

Toda mudança incrementa `versao` (intenção, pagamento, cobrança). O front nunca deduz estado: recebe o evento (§6) e busca o estado autorizado.

### 3.1 `pagamento_intent.status`
```
criado ──► aguardando_pagamento ──► pago ──► estornado_parcial ──► estornado
  │  │            │   ▲   │          │  ▲          │
  │  │            ▼   │   │          ▼  │          ▼
  │  └──────► processando │       contestado ◄──────┘
  │               │       │          (ganhou → pago; perdeu → fica contestado)
  ├──► recusado ◄─┘       ├──► expirado ──► pago (Pix pago depois)
  └──► cancelado ◄────────┘    cancelado ──► pago
recusado ──► aguardando_pagamento | processando (nova tentativa, ex.: outro cartão)
```
Regras (`PagamentoStatusRules.PodeIntent` / `StatusDaIntent`):
- Família paga (`pago`, `estornado_parcial`, `estornado`, `contestado`) **nunca** volta para aberto/recusado/cancelado/expirado.
- Pix antigo cancelado/expirado não derruba a intenção se não for o pagamento mais recente.
- `expirado`/`cancelado` são finais para novas tentativas: emitir de novo = **nova intenção** (nova chave). Só voltam para `pago` se o dinheiro entrar mesmo assim.

### 3.2 `pagamento.status` (tradução do MP — `PagamentoStatusRules.Traduzir`)
| MP `status` (`status_detail`) | local |
|---|---|
| `pending` | `pendente` |
| `in_process` | `em_processo` |
| `authorized` | `autorizado` |
| `approved` (sem estorno) | `aprovado` |
| `approved`/`refunded` com estorno parcial | `estornado_parcial` |
| `refunded` / estorno total | `estornado` |
| `rejected` | `recusado` |
| `cancelled` (`expired`) | `expirado` |
| `cancelled` (outro) | `cancelado` |
| `in_mediation` | `em_mediacao` (abre `pagamento_disputa` tipo mediacao) |
| `charged_back` | `contestado` (abre disputa chargeback) |

Evento fora de ordem (`DeveAplicar`): retrato com `date_last_updated` mais velho que o salvo é ignorado; dinheiro que entrou nunca volta a `pendente`; empate de data só aplica se o estado avança (peso) ou o estorno cresceu; `em_mediacao → aprovado` (mediação ganha) é aceito com data.

### 3.3 `cobranca_chat.status`
```
criando ──► aguardando ──► enviado ──► pago ──► reembolso_solicitado ──► reembolsado
   │            │             │         ▲  └───────────────────────────► reembolsado
   │            │             │         └─── (estorno recusado/parcial volta a pago)
   ├──► recusado◄┴─────────────┤
   └──► cancelado ◄────────────┤
                expirado ◄─────┘        expirado/cancelado ──► pago (pagou atrasado)
```
| Estado | Significado | Quem leva até ele |
|---|---|---|
| `criando` | Registro criado, Pix sendo gerado | sessão 5 (POST) |
| `aguardando` | Pix gerado, ainda não enviado ao cliente | núcleo (intenção `aguardando_pagamento`) |
| `enviado` | Link/copia e cola enviado no chat (`enviado_em`, `id_mensagem`) | sessão 5 (`/enviar`) |
| `pago` | Pagamento aprovado | núcleo (webhook/conciliador) |
| `expirado` | Pix venceu sem pagamento | núcleo |
| `cancelado` | Atendente cancelou (Pix cancelado no MP) | sessão 5 (`/cancelar`) via `IPixChargeService.CancelarAsync` |
| `recusado` | MP recusou/geração falhou | núcleo / sessão 5 |
| `reembolso_solicitado` | Estorno pedido, aguardando MP | núcleo (`IPagamentoEstornoService`) |
| `reembolsado` | Estorno total concluído | núcleo |

Transições válidas: `PagamentoStatusRules.PodeCobranca`. Mudança sempre por `CobrancaChatSql.MudarStatusAsync` (valida, incrementa versão, grava `cobranca_chat_evento` e outbox `cobrancaAtualizada`). O núcleo sincroniza a cobrança a partir da intenção no handler `ChatCobrancaEfeito` (`StatusDaCobranca`).

### 3.4 `pagamento_estorno.status`
`solicitado → processando → concluido | recusado | falhou`; `solicitado → concluido | recusado | falhou`. Sem resposta do MP fica `solicitado` (repetir com a mesma chave é seguro).

### 3.5 Outras
- `mp_conta_loja.estado`: `ativa ⇄ expirada` (refresh), `ativa|expirada|erro → desconectada` (loja desconectou no painel) `| revogada` (webhook `mp-connect` de desautorização).
- `mp_webhook_evento.status`: `recebido → processando → processado | ignorado | rejeitado | falhou`; `falhou → processando` (backoff 30 s·2ⁿ, até `WebhookMaxTentativas`=8).

---

## 4. Serviços compartilhados (DI scoped; registrados por `AddPagamentosMercadoPago`)

### 4.1 Credenciais — `IMercadoPagoCredencialResolver`
```csharp
string AmbienteAtivo { get; }
Task<CredencialAplicacao> AplicacaoAsync(string? ambiente = null, CancellationToken ct = default);
Task<CredencialRecebedora> RecebedorZippyAsync(string? ambiente = null, CancellationToken ct = default);
Task<CredencialRecebedora> LojaAsync(Guid idEstabelecimento, string? ambiente = null, CancellationToken ct = default);
Task<CredencialRecebedora> ParaIntentAsync(PagamentoIntent intent, CancellationToken ct = default);
Task<CredencialRecebedora?> PorMpUserIdAsync(string ambiente, long mpUserId, CancellationToken ct = default);
```
Cliente: `IMercadoPagoClient` — `CriarPagamentoAsync`, `ObterPagamentoAsync`, `BuscarPorReferenciaAsync`, `CancelarPagamentoAsync`, `EstornarAsync`, `ObterChargebackAsync`, `TrocarCodigoOAuthAsync(app, code, redirectUri, codeVerifier)`, `RenovarTokenAsync(app, refreshToken)`. Erros: `MercadoPagoApiException` (`StatusHttp`, `MpErro`, `Indisponivel`, `ParaPagamento()` → 502/503).
Sessão 2 (OAuth): troca o code com `TrocarCodigoOAuthAsync`, grava `mp_conta_loja` cifrando com `IPagamentoSegredoProtector` + `SegredoContexto.*`, nunca devolve `MpOAuthToken`.

### 4.2 Pix — `IPixChargeService` (O ÚNICO serviço Pix)
```csharp
Task<PixChargeResult> CriarAsync(PixChargeRequest request, CancellationToken ct = default);
Task<PixChargeResult> ObterAsync(Guid intentId, Guid idEstabelecimento, CancellationToken ct = default);
Task<string> GerarLinkPublicoAsync(Guid intentId, Guid idEstabelecimento, CancellationToken ct = default);
Task<PixChargeResult> CancelarAsync(Guid intentId, Guid idEstabelecimento, string ator, int? usuarioId, CancellationToken ct = default);
```
`PixChargeRequest`: `IdEstabelecimento`, `Origem`, `Valor` (**calculado no servidor**), `Descricao` (1–140), `IdempotencyKey` (≤160), `PedidoId?`, `PrePedidoId?`, `ConversaId?`, `ClienteId?`, `OrigemRefId?`, `Pagador?` (`Email`, `Nome`, `TipoDocumento`, `NumeroDocumento` — só repassado ao MP, não gravado), `CriadoPor?`, `ValidadeMinutos?` (null = preferência da loja), `GerarLinkPublico`.
`PixChargeResult`: `IntentId`, `PagamentoId`, `MpPaymentId`, `Status` (intenção), `StatusPagamento`, `Versao`, `Valor`, `Descricao`, `CopiaECola`, `QrCodeBase64`, `TicketUrl`, `ExpiraEm`, `ExternalReference`, `TokenPublico` (só na chamada que gerou), `Reaproveitado`.
Comportamento:
- Loja precisa de `pix_ativo` (senão `PAG_PIX_DESATIVADO`) e de conta conectada (`PAG_CONTA_LOJA_NAO_CONECTADA`).
- Mesma chave + mesmos dados → devolve a intenção existente (Pix pendente válido é reaproveitado, sem chamar o MP). Mesma chave + dados diferentes → `409 PAG_IDEMPOTENCIA_CONFLITO`. Chave de intenção `expirado`/`cancelado` → `409 PAG_ESTADO_INVALIDO` (emitir com nova chave).
- QR/copia e cola só aparecem enquanto o Pix está `pendente`.
- Convenção de chaves: cardápio `cardapio:{prePedidoId:N}` (Pix) e `cardapio:{prePedidoId:N}:cartao`; chat `chat:{cobrancaId:N}`; assinatura `assinatura:{cicloId:N}`; extra `compra_extra:{compraId:N}`. Reemitir no chat = nova `cobranca_chat` (nova chave) com `reemitida_de`.

### 4.3 Cartão — `ICardPaymentService` (base do Payment Brick)
```csharp
Task<CardPaymentResult> CriarAsync(CardPaymentRequest request, CancellationToken ct = default);
Task<BrickConfiguracao> ConfiguracaoAsync(Guid idEstabelecimento, CancellationToken ct = default);
```
`CardPaymentRequest` = campos de §4.2 + `Token` (do Brick), `PaymentMethodId`, `IssuerId?`, `Parcelas` (≤ `cartao_parcelas_max`), `Pagador.Email` obrigatório. `origem=chat` → `422 PAG_CARTAO_NAO_PERMITIDO_NO_CHAT`.
`CardPaymentResult`: `IntentId`, `PagamentoId`, `Status`, `StatusPagamento`, `StatusDetail` (`cc_rejected_*`), `Aprovado`, `Versao`, `Valor`.
`BrickConfiguracao`: `PixAtivo`, `CartaoAtivo`, `PresencialAtivo`, `PublicKey` (da conta da loja), `ParcelasMax`, `ContaConectada`.
Mesmo token enviado duas vezes = mesma tentativa = um pagamento só. Pix pendente na intenção bloqueia o cartão (cancele o Pix antes).

### 4.4 Estorno — `IPagamentoEstornoService`
```csharp
Task<EstornoResult> EstornarAsync(EstornoRequest request, CancellationToken ct = default);
```
`EstornoRequest`: `IntentId`, `IdEstabelecimento`, `Valor?` (null = saldo restante), `Motivo?`, `UsuarioId?`, `IdempotencyKey`. `EstornoResult`: `EstornoId`, `Status`, `Valor`, `StatusIntent`, `ValorEstornadoTotal`.
Só intenção `pago`/`estornado_parcial`. Acima do pago → `422 PAG_ESTORNO_ACIMA_DO_PAGO` (garantido também pelo trigger). Origem chat: cobrança vai a `reembolso_solicitado` e depois `reembolsado` (total) ou volta a `pago` (parcial ou recusado).

### 4.5 Ganchos
- `IPagamentoEfeitoHandler { string Origem; Task<Guid?> AplicarAsync(cx, tx, intent, statusAnterior, ator, ct); }` — roda **dentro** da transação do sincronizador sempre que a intenção muda. Núcleo registra `CardapioPagamentoEfeito` (espelha `cardapio_pedido_publico.status_pagamento`: `aguardando|pago|recusado|estornado_parcial|estornado|pendente`) e `ChatCobrancaEfeito`. Sessão 4 registra os de `assinatura`/`compra_extra` (`services.AddScoped<IPagamentoEfeitoHandler, ...>()`).
- `IMercadoPagoTopicoHandler { bool Atende(string topico); Task<(string Status, string? Motivo)> ProcessarAsync(MpWebhookEvento, ct); }` — sessão 2 (`mp-connect`) e sessão 4 (`subscription_*`). Sem handler o evento fica `ignorado` (`sem_handler`), reprocessável.
- `IPagamentoSincronizador` — `SincronizarAsync(cred, mpPaymentId, ator)`, `AplicarAsync(cred, mp, idTentativa, ator)`, `AplicarNaTransacaoAsync(...)`, `MarcarIntentAsync(cx, tx, intent, status, ator, usuarioId)`. É o único caminho que muda status de intenção/pagamento.
- `IContaLojaTokenRenovador.RenovarAsync(idContaLoja)`.

---

## 5. Webhook e conciliação

### 5.1 `POST /api/webhooks/mercadopago` (anônimo; implementado)
1. Lê corpo (≤ 64 KB), `x-signature` (`ts=...,v1=...`), `x-request-id`, query `data.id` e `type`.
2. Segredo = `mp_aplicacao.webhook_secret` do ambiente ativo. Sem segredo → **503** (o MP reenvia).
3. Manifesto `id:{data.id};request-id:{x-request-id};ts:{ts};` (data.id alfanumérico em minúsculas; partes ausentes omitidas), HMAC-SHA256 hex, comparação em tempo constante. `ts` aceito em s ou ms, tolerância `WebhookToleranciaSegundos` (600). Falha → **401** `PAG_WEBHOOK_ASSINATURA_INVALIDA`, nada gravado.
4. Grava `mp_webhook_evento` (`ON CONFLICT chave_dedup DO NOTHING`). Duplicado → **200** `{recebido:true, duplicado:true}`. Falha ao gravar → **5xx** (MP reenvia).
5. Processa na hora; erro de processamento → evento `falhou` + reprocessamento, mas responde **200** (já persistido).
6. Tópico `payment`: credencial pela intenção do pagamento local, ou pela conta dona do `user_id`. Busca `GET /v1/payments/{id}` **server-to-server** e confere: `external_reference` nosso e igual ao da intenção, conta/collector = conta da intenção (titularidade), valor, moeda, `live_mode` × ambiente. Divergência → `rejeitado` + auditoria, nada muda. Aplica de forma idempotente e transacional (linhas travadas, `DeveAplicar` contra fora de ordem). Efeitos (cardápio, chat, outbox, auditoria) só quando algo mudou.
7. `chargebacks`/`topic_chargebacks_wh`: busca o chargeback, sincroniza os pagamentos e registra `pagamento_disputa`.
8. `merchant_order` e outros sem handler: `ignorado`. `mp-connect` (sessão 2) e `subscription_preapproval`/`subscription_authorized_payment` (sessão 4): via `IMercadoPagoTopicoHandler`.

Respostas: `200 {recebido, evento, status}` | `200 {recebido, duplicado}` | `400 PAG_REQUISICAO_INVALIDA` | `401 PAG_WEBHOOK_ASSINATURA_INVALIDA` | `503 PAG_NAO_CONFIGURADO`.

### 5.2 Conciliador — `PagamentoConciliadorWorker`
A cada `ConciliadorIntervaloSegundos` (120), uma instância por vez (advisory lock `75442310`):
- reprocessa `mp_webhook_evento` `recebido|falhou` vencidos e `processando` abandonados;
- consulta no MP os pagamentos `pendente|em_processo|autorizado|em_mediacao` sem notícia há `ConciliadorIdadeMinimaSegundos` (Pix vencido vira `expirado` por aqui se o webhook não vier);
- recupera tentativas `criando` (sem resposta do MP) buscando pelo `external_reference`; sem nada em 30 min → `falhou`.

### 5.3 Para a sessão 4 (assinaturas)
Registre `IMercadoPagoTopicoHandler` para `subscription_preapproval` e `subscription_authorized_payment` usando `IMercadoPagoCredencialResolver.RecebedorZippyAsync`. Cobranças avulsas de mensalidade/extra podem usar `IPixChargeService`/`ICardPaymentService` com `origem=assinatura|compra_extra`.

---

## 6. Eventos SignalR (hub `/hubs/delivery`, grupo `chat:{estabelecimentoId:N}` = `ChatRealtimeEvents.Group`)

Publicados **depois do commit** pela outbox (`PagamentoOutboxPublisher`, ~1 s), entrega pelo menos uma vez. **Nunca** carregam token, QR, copia e cola, dado bancário ou do cliente. Ao receber, o front busca o estado autorizado e compara `versao` (ignora versão menor ou igual à que já tem).

| Evento | Payload | Buscar em seguida |
|---|---|---|
| `pagamentoAtualizado` | `{ intentId, estabelecimentoId, origem, status, versao, pedidoId, prePedidoId, conversaId, cobrancaId, ocorridoEm }` | `GET /api/v2/pagamentos/{intentId}` |
| `cobrancaAtualizada` | `{ id, conversaId, pedidoId, status, versao, ocorridoEm }` | `GET /api/v2/atendimento/cobrancas/{id}` |

Constantes: `PagamentoRealtimeEventos.PagamentoAtualizado`, `.CobrancaAtualizada`, `.Grupo(loja)`. A página Pix pública não tem SignalR: faz polling de `GET /api/public/pagamentos/pix/{token}` (sugestão: 3 s, parar em status final).

---

## 7. Formato de erro e códigos

Toda rota responde `ApiResponse`: sucesso `{ success: true, data }`; erro `{ success: false, error, code, details? }`. `error` é texto para humano; o front decide pelo `code`.

| HTTP | code | Quando |
|---|---|---|
| 400 | `PAG_REQUISICAO_INVALIDA` | Campo ausente/formato errado (`details.campo`) |
| 401 | `PAG_NAO_AUTENTICADO` / `UNAUTHENTICATED` | Sem login nas rotas autenticadas |
| 401 | `PAG_WEBHOOK_ASSINATURA_INVALIDA` | Webhook com x-signature inválida/ts fora |
| 403 | `PAG_SEM_PERMISSAO` / `FORBIDDEN` | Sem permissão ou sem loja selecionada |
| 404 | `PAG_NAO_ENCONTRADO` | Não existe **ou é de outra loja** (nunca revela qual), link/código inválido |
| 409 | `PAG_IDEMPOTENCIA_CONFLITO` | Mesma chave com dados diferentes |
| 409 | `PAG_ESTADO_INVALIDO` | Transição proibida (`details.status`) |
| 409 | `PAG_JA_PAGO` | Cancelar/pagar de novo algo pago |
| 409 | `PAG_COBRANCA_ABERTA` | Já há cobrança aberta para o pedido na conversa (sessão 5) |
| 422 | `PAG_VALOR_INVALIDO` | Valor ≤ 0, > 999.999,99 ou > 2 casas |
| 422 | `PAG_PIX_DESATIVADO` / `PAG_CARTAO_DESATIVADO` | Meio desligado na loja |
| 422 | `PAG_CARTAO_NAO_PERMITIDO_NO_CHAT` | Cartão com origem chat |
| 422 | `PAG_CARTAO_RECUSADO` | Cartão recusado (`details.statusDetail`, `details.intentId`) |
| 422 | `PAG_CONTA_LOJA_NAO_CONECTADA` | Loja sem conta MP |
| 422 | `PAG_CONTA_LOJA_REAUTORIZAR` | Token vencido/revogado: conectar de novo |
| 422 | `PAG_PEDIDO_NAO_PAGAVEL` | Pré-pedido recusado/expirado/sem valor |
| 422 | `PAG_ESTORNO_ACIMA_DO_PAGO` | Estorno passa do pago |
| 409 | `PAG_CONTA_JA_CONECTADA` | Conectar com conta ativa sem `reconectar` (sessão 2) |
| 422 | `PAG_AMBIENTE_NAO_LIBERADO` | A Gestão ainda não liberou a conexão das lojas no ambiente |
| 422 | `PAG_VERIFICACAO_PENDENTE` | Habilitar ambiente sem verificação aprovada e atual |
| 422 | `PAG_CONTA_MP_DE_OUTRA_LOJA` | Conta MP já recebe por outra loja (ou cadastrar loja como conta Zippy) |
| — | `PAG_OAUTH_STATE_INVALIDO` / `PAG_OAUTH_EXPIRADO` / `PAG_OAUTH_CANCELADO` / `PAG_OAUTH_FALHOU` / `PAG_OAUTH_AMBIENTE_DIVERGENTE` | Só no redirect do callback (`?codigo=`) |
| 429 | `RATE_LIMITED` | Política `public-payment` (30/min por IP) ou outra |
| 500 | `PAG_CREDENCIAL_INCOMPATIVEL` | Bug: token de conta errada (nunca deve acontecer) |
| 502 | `PAG_MP_ERRO` | MP recusou (`details.mpStatus`, `details.mpErro`) |
| 502 | `PAG_MP_RESPOSTA_INCONSISTENTE` | MP devolveu algo que não confere (dono, valor, QR) |
| 503 | `PAG_MP_INDISPONIVEL` | MP sem resposta/5xx/429 (repetir com a mesma chave) |
| 503 | `PAG_NAO_CONFIGURADO` | Chave mestra, aplicação ou conta Zippy não configurada |

Erros das rotas: cada rota abaixo lista os códigos que pode devolver além de 401/403/429/500 genéricos.

---

## 8. Rotas (todas as planejadas)

Legenda: ✅ implementada pelo núcleo · 🔲 a implementar pela sessão indicada (contrato fixo).

### 8.1 Gestão Zippy — `/api/gestao/pagamentos/*` (✅ sessão 2; só super admin)
Autorização no servidor por `[SomenteSuperAdmin]` em todo o controller: sem login `401 PAG_NAO_AUTENTICADO`, logado sem ser super admin `403 PAG_SEM_PERMISSAO`; `Cache-Control: no-store`. Toda escrita grava `pagamento_auditoria` (entidades `aplicacao`, `conta_zippy`, `ambiente`, `politica`, `verificacao`, `webhook`, `conciliacao`) dizendo **o que** mudou, nunca o valor de segredo.
| Método e rota | Corpo / query | Resposta `data` | Erros |
|---|---|---|---|
| `GET /api/gestao/pagamentos/resumo` | — | `{ ambiente, recebidoZippyMes, estornadoZippyMes, cobrancasZippyAbertas, valorZippyAberto, vendasLojasMes, transacoesLojasMes, lojasConectadas, lojasComProblema, webhooksComFalha, disputasAbertas, reembolsosPendentes, pronto, ambienteHabilitado }` (Zippy e lojas em campos separados, nunca somados) | — |
| `GET /api/gestao/pagamentos/configuracao?ambiente=` | — | `{ ambienteAtivo, ambiente, chaveMestraConfigurada, aplicacao: { ambiente, clientId, publicKey, redirectUri, temClientSecret, temWebhookSecret, ativo, origem: banco\|configuracao\|nenhuma, atualizadoEm }, contaZippy: { ambiente, mpUserIdMascarado, publicKey, estado, temToken, ultimoErro, validadaEm }, ambienteEstado: { habilitado, lojasPodemConectar, habilitadoEm }, urlCallback, urlWebhook, ultimaVerificacao: { id, aprovada, etapas, criadoEm, desatualizada } \| null, webhook: { ultimoRecebidoEm, recebidos24h, falhas24h, pendentes }, lojasConectadas, etapas: [{ numero, chave, titulo, status: ok\|pendente\|erro\|bloqueada, falta }] (8), pronto }` | 400 |
| `PUT /api/gestao/pagamentos/configuracao` | `{ ambiente, habilitado, lojasPodemConectar }` | igual ao GET | 400, 422 (`PAG_VERIFICACAO_PENDENTE`) |
| `POST /api/gestao/pagamentos/verificar` | `{ ambiente }` | `{ id, ambiente, aprovada, etapas, criadoEm }` (§2.17) | 400 |
| `PUT /api/gestao/pagamentos/aplicacao` | `{ ambiente, clientId, clientSecret?, publicKey?, webhookSecret?, redirectUri }` (segredo vazio = manter) | igual `aplicacao` acima | 400, 422, 503 |
| `PUT /api/gestao/pagamentos/conta-zippy` | `{ ambiente, accessToken, publicKey? }` (o servidor valida o token em `GET /users/me` e grava `mp_user_id`) | igual `contaZippy` | 400, 422, 502, 503 |
| `POST /api/gestao/pagamentos/conta-zippy/testar` | `{ ambiente }` | `{ ok, mpUserIdMascarado }` | 422, 502, 503 |
| `GET /api/gestao/pagamentos/conexoes?busca=&estado=&pagina=` (antes `lojas`) | — | `{ itens: [{ estabelecimentoId, nome, estado (nao_conectada\|ativa\|expirada\|erro\|revogada\|desconectada), mpUserIdMascarado, tokenExpiraEm, conectadoEm, ultimoErro, pixAtivo, cartaoAtivo }], total }` (ambiente ativo; 25 por página) | — |
| `GET /api/gestao/pagamentos/transacoes?conta=loja\|zippy&estabelecimentoId=&origem=&status=&de=&ate=&pagina=` | — | `{ conta, itens: [{ id, estabelecimentoId, lojaNome, origem, contaTipo, status, valor, valorPago, valorEstornado, descricao, metodo, statusPagamento, statusDetalhe, mpPaymentId, pagoEm, criadoEm }], total, somaPaga, somaEstornada }` — `conta` (padrão `loja`) é obrigatória na lógica: lista e somas são **sempre de uma conta só** | 400 |
| `GET /api/gestao/pagamentos/assinaturas?status=&pagina=` | — | igual `transacoes` com `conta=zippy` e origem `assinatura\|compra_extra` (só leitura; regras na sessão 4) | — |
| `GET /api/gestao/pagamentos/reembolsos?status=&conta=&pagina=` | — | `{ itens: [{ id, intentId, estabelecimentoId, lojaNome, contaTipo, origem, valor, status, origemEstorno, motivo, erroMensagem, criadoEm, concluidoEm }], total }` | 400 |
| `GET /api/gestao/pagamentos/webhooks?status=&topico=&pagina=` | — | `{ itens: [{ id, ambiente, topico, acao, dataId, status, tentativas, ultimoErro, recebidoEm, processadoEm }], total, saude: { ultimoRecebidoEm, recebidos24h, falhas24h, pendentes } }` (sem payload bruto) | — |
| `POST /api/gestao/pagamentos/webhooks/{id}/reprocessar` | — | `{ status }` — só `falhou\|ignorado\|recebido` (volta a `recebido` e chama `IMercadoPagoWebhookService.ProcessarAsync`); `rejeitado` nunca | 404, 409 |
| `POST /api/gestao/pagamentos/reconciliar` | — | `{ pagamentosAbertosAntes, pagamentosAbertosDepois, webhooksPendentesAntes, webhooksPendentesDepois, executadaEm }` (roda o ciclo do `PagamentoConciliadorWorker` agora) | — |
| `GET /api/gestao/pagamentos/disputas?status=` | — | `{ itens: [{ id, tipo, status, valor, prazoRespostaEm, intentId, estabelecimentoId, lojaNome, criadoEm }] }` | — |
| `GET /api/gestao/pagamentos/auditoria?escopo=gestao\|&entidade=&entidadeId=&pagina=` | — | `{ itens: [{ id, estabelecimentoId, entidade, entidadeId, acao, atorTipo, atorId, atorNome, dados, criadoEm }], total }` (`escopo=gestao` = histórico de alterações da Gestão) | 400 |
| `GET` / `PUT /api/gestao/pagamentos/politicas` | `{ toleranciaDias, avisosAntesDias[], avisosDepoisDias[], alcadaEstornoGerente, alcadaEstornoConfirmacao, alertaFalhasWebhook }` | as mesmas + `atualizadoEm` (§2.18) | 400 |

### 8.2 OAuth da loja (✅ sessão 2)
Quem pode: **gerenciar** (conectar, reconectar, desconectar, verificar, preferências) = super admin, `dono`/`gerente_empresa` da empresa ou `gerente_estabelecimento` da loja (`ConexaoMercadoPagoAcesso.PodeGerenciar`); **ver** (GET estado, preferências, transações) = quem gerencia ou `Financeiro:visualizar`. Não depende de módulo premium (Integrações vale antes de contratar módulos). Loja = a selecionada no token; sem loja `403`.
| Método e rota | Corpo | Resposta `data` | Erros |
|---|---|---|---|
| `GET /api/v2/integracoes/mercadopago` | — | `{ conectada, estado (nao_conectada\|ativa\|expirada\|erro\|revogada\|desconectada), mpUserIdMascarado, liveMode, tokenExpiraEm, conectadoEm, ultimaVerificacaoEm, ultimoErro (token_expirado\|token_invalido\|conta_suspensa\|conta_divergente\|meios_indisponiveis\|substituida\|desautorizada_no_mp), ambiente, conexaoDisponivel, preferencias: { pixAtivo, cartaoAtivo, presencialAtivo, pixValidadeMinutos, cartaoParcelasMax } }` | 403 |
| `POST /api/v2/integracoes/mercadopago/conectar` | `{ reconectar? }` | `{ urlAutorizacao, tentativaId, expiraEm }` — URL `https://auth.mercadopago.com.br/authorization?client_id=..&response_type=code&platform_id=mp&state={state}&redirect_uri=..&code_challenge=..&code_challenge_method=S256`. State = 32 bytes base64url (só o SHA-256 no banco); verifier PKCE cifrado (`SegredoContexto.OAuthCodeVerifier`); nova tentativa cancela a anterior da loja | 409 `PAG_CONTA_JA_CONECTADA` (sem `reconectar`), 422 `PAG_AMBIENTE_NAO_LIBERADO`, 503 |
| `GET /api/integracoes/mercadopago/callback?code=&state=&error=` (anônimo) | — | **302** para `{PainelUrl}/apps/configuracoes/integracoes?mercadopago=ok\|erro&codigo=...` (`Payments:MercadoPago:PainelUrl`, senão `Rastreio:PublicBaseUrl`). Consome a tentativa uma vez só (`UPDATE ... WHERE estado='iniciada'`), confere expiração (10 min) e ambiente, troca o code com o verifier, recusa conta MP já viva em **outra** loja (`PAG_CONTA_MP_DE_OUTRA_LOJA`), mesma conta = só troca tokens, outra conta = a anterior vira `desconectada`; audita. Códigos: `PAG_OAUTH_STATE_INVALIDO`, `PAG_OAUTH_EXPIRADO`, `PAG_OAUTH_CANCELADO`, `PAG_OAUTH_FALHOU`, `PAG_OAUTH_AMBIENTE_DIVERGENTE`, `PAG_CONTA_MP_DE_OUTRA_LOJA`, `PAG_MP_INDISPONIVEL`, `PAG_MP_RESPOSTA_INCONSISTENTE` | (via redirect) |
| `GET /api/v2/integracoes/mercadopago/tentativas/{id}` | — | `{ estado, erroCodigo }` (só da própria loja) | 404 |
| `POST /api/v2/integracoes/mercadopago/verificar` | — | `{ ok, estado, pixDisponivel, cartaoDisponivel, etapas: [{ etapa: acesso\|conta\|pix\|cartao, ok, codigo, mensagem }] }` — renova o token se preciso, `GET /users/me` (mesmo titular, conta ativa) e `GET /v1/payment_methods`; 401/403 → `erro`/`token_invalido`, refresh recusado → `expirada`, MP fora → não muda estado | 403, 422 (`PAG_CONTA_LOJA_NAO_CONECTADA`) |
| `DELETE /api/v2/integracoes/mercadopago?confirmar=` | — | `{ estado: "desconectada" }` (apaga tokens, mantém a linha e **todas** as transações) | 404, 409 `PAG_COBRANCA_ABERTA` (`details.abertas` = intenções abertas na conta; repetir com `confirmar=true`) |
| `GET` / `PUT /api/v2/integracoes/mercadopago/preferencias` | `{ pixAtivo, cartaoAtivo, presencialAtivo, pixValidadeMinutos, cartaoParcelasMax }` | preferências (auditado `preferencia_alterada` com antes/depois) | 400, 422 (`PAG_CONTA_LOJA_NAO_CONECTADA` ao ligar Pix/cartão sem conta ativa) |
| `GET /api/v2/integracoes/mercadopago/transacoes?pagina=` | — | `{ itens: [{ id, origem, status, valor, valorPago, valorEstornado, descricao, metodo, pedidoId, pagoEm, criadoEm }] }` (conta da loja, 20 por página) | 403 |
| Webhook `mp-connect` | — | `MpConnectTopicoHandler`: `application.deauthorized` → toda conta viva com aquele `user_id` vira `revogada` (tokens apagados), auditado | — |
| Renovação | — | `MercadoPagoContasLojaWorker` (6 h): renova contas `ativa` que vencem em < 7 dias via `IContaLojaTokenRenovador` e expira tentativas abandonadas | — |

### 8.3 Cardápio web (✅ núcleo; anônimo; política `public-payment`)
O valor **nunca** vem do corpo: é `cardapio_pedido_publico.total`. `codigo` = código do pedido mostrado ao cliente (segundo fator junto com o id; errado → 404).
| Método e rota | Corpo / query | Resposta `data` | Erros |
|---|---|---|---|
| `GET /api/public/cardapio/{estabelecimentoId}/pagamentos/config` | — | `BrickConfiguracao` `{ pixAtivo, cartaoAtivo, presencialAtivo, publicKey, parcelasMax, contaConectada }` | — |
| `POST /api/public/cardapio/pedidos/{prePedidoId}/pagamentos/pix` | `{ codigo }` | `PublicPixDto` `{ status, versao, valor, descricao, copiaECola, qrCodeBase64, expiraEm, lojaNome:null }` | 404, 409, 422 (`PAG_PIX_DESATIVADO`, `PAG_CONTA_LOJA_NAO_CONECTADA`, `PAG_PEDIDO_NAO_PAGAVEL`), 429, 502, 503 |
| `POST /api/public/cardapio/pedidos/{prePedidoId}/pagamentos/cartao` | `{ codigo, token, paymentMethodId, issuerId?, parcelas, pagador: { email, tipoDocumento, numeroDocumento } }` (saída do Payment Brick) | `{ intentId, status, statusPagamento, statusDetail, aprovado, versao, valor }` | 400, 404, 409 (`PAG_JA_PAGO`, Pix aberto), 422 (`PAG_CARTAO_RECUSADO` com `details.statusDetail`, `PAG_CARTAO_DESATIVADO`), 429, 502, 503 |
| `GET /api/public/cardapio/pedidos/{prePedidoId}/pagamento?codigo=` | — | `PublicPixDto` ou `{ status: "sem_pagamento", statusPagamentoPedido, versao: 0 }` | 404, 429 |
A sessão 3 decide em que passo do fluxo do pedido o pagamento aparece; o espelho `cardapio_pedido_publico.status_pagamento` já é mantido pelo núcleo.

### 8.4 Assinaturas Zippy — `/api/v2/comercial/*` (🔲 sessão 4; recebedor = conta Zippy)
| Método e rota | Corpo | Resposta `data` | Erros |
|---|---|---|---|
| `GET /api/v2/comercial/assinatura` | — | `{ plano, status, cicloAtual: { id, inicio, fim, valor, status, intentId }, proximaCobrancaEm, meio }` | 404 |
| `POST /api/v2/comercial/assinatura/pagar-ciclo` | `{ cicloId, metodo: "pix" \| "cartao", cartao?: { token, paymentMethodId, issuerId, parcelas, pagador } }` (valor = ciclo no servidor) | Pix: `PixChargeResult` público; cartão: `CardPaymentResult` | 404, 409, 422, 502, 503 |
| `GET /api/v2/comercial/extras` | — | `{ itens: [{ id, nome, preco }] }` (de `produtos_avulsos`) | — |
| `POST /api/v2/comercial/solicitacoes` | `{ produtoAvulsoId, quantidade }` | `{ solicitacaoId, status, compraId?, pagamento? }` | 400, 404, 409, 422 |
| `GET /api/v2/comercial/solicitacoes` / `GET /api/v2/comercial/compras/{id}` | — | lista / detalhe com `intentId`, `status` | 404 |
| Webhooks `subscription_*` | — | `IMercadoPagoTopicoHandler` | — |

### 8.5 Chat — cobrança Pix do atendente (🔲 sessão 5 backend; 🔲 sessão 6 UI)
Permissão sugerida: `WhatsApp:cobrar` (nova ação; acrescentar no catálogo de `EstablishmentPermissions`). Estorno: `Financeiro:cancelar`.
DTO `CobrancaChatDto`:
```json
{ "id": "uuid", "conversaId": "uuid", "intentId": "uuid|null", "status": "criando|aguardando|enviado|pago|expirado|cancelado|reembolso_solicitado|reembolsado|recusado",
  "versao": 3, "descricao": "Taxa de entrega", "valor": 12.50, "tipo": "avulsa|vinculada", "pedidoId": 123, "prePedidoId": null,
  "expiraEm": "2026-10-25T15:30:00Z", "enviadoEm": null, "pagoEm": null, "canceladoEm": null, "reemitidaDe": null,
  "autor": { "id": 7, "nome": "Ana" }, "pix": { "copiaECola": "...", "qrCodeBase64": "...", "linkPublico": "https://.../pix/{token}" | null },
  "valorEstornado": 0, "podeEnviar": true, "podeCancelar": true, "podeReemitir": false, "podeReembolsar": false, "criadoEm": "..." }
```
`pix` só vem enquanto `aguardando|enviado`. `linkPublico` só na resposta do POST/`/reemitir`/`/enviar` que gerou o token (depois: `null`; gerar outro via `/enviar`).

| Método e rota | Corpo | Resposta `data` | Erros |
|---|---|---|---|
| `POST /api/v2/atendimento/conversas/{id}/cobrancas` | `{ descricao, valor, tipo, pedidoId?, prePedidoId?, validadeMinutos?, idempotencyKey }` — valor digitado pelo **atendente autenticado** (não pelo cliente); vinculada: valor ≤ saldo do pedido | `201 CobrancaChatDto` (status `aguardando`) | 400, 404 (conversa de outra loja), 409 (`PAG_COBRANCA_ABERTA`, `PAG_IDEMPOTENCIA_CONFLITO`), 422 (`PAG_PIX_DESATIVADO`, `PAG_CONTA_LOJA_NAO_CONECTADA`, `PAG_VALOR_INVALIDO`), 502, 503 |
| `GET /api/v2/atendimento/conversas/{id}/cobrancas` | `?status=` | `{ itens: CobrancaChatDto[] }` | 404 |
| `GET /api/v2/atendimento/cobrancas/{id}` | — | `CobrancaChatDto` (estado autorizado após `cobrancaAtualizada`) | 404 |
| `POST /api/v2/atendimento/cobrancas/{id}/enviar` | `{ formato: "link" \| "copia_e_cola" \| "ambos" }` | `CobrancaChatDto` (`enviado`, `id_mensagem`) — envia pelo chat (janela 24 h; fora dela, template) | 404, 409 (`PAG_ESTADO_INVALIDO`), 422 (janela fechada sem template), 502 (WhatsApp) |
| `POST /api/v2/atendimento/cobrancas/{id}/cancelar` | `{ motivo? }` | `CobrancaChatDto` (`cancelado`) — `IPixChargeService.CancelarAsync` | 404, 409 (`PAG_JA_PAGO`, estado), 503 |
| `POST /api/v2/atendimento/cobrancas/{id}/reemitir` | `{ validadeMinutos?, idempotencyKey }` | `201 CobrancaChatDto` nova (`reemitidaDe` = anterior; anterior cancelada se aberta) | 404, 409, 422, 502, 503 |
| `POST /api/v2/atendimento/cobrancas/{id}/reembolsar` | `{ valor?, motivo?, idempotencyKey }` | `CobrancaChatDto` (`reembolso_solicitado`/`reembolsado`) — `IPagamentoEstornoService` | 404, 409, 422 (`PAG_ESTORNO_ACIMA_DO_PAGO`), 502, 503 |
| `GET /api/v2/atendimento/cobrancas/{id}/historico` | — | `{ itens: [{ statusAnterior, statusNovo, atorTipo, usuario: { id, nome } \| null, detalhe, criadoEm }] }` (de `cobranca_chat_evento`) | 404 |

Fluxo da sessão 5 no POST: inserir `cobranca_chat` `criando` (idempotente) → `IPixChargeService.CriarAsync` (`Origem=chat`, `ConversaId`, `PedidoId`, `IdempotencyKey=chat:{cobrancaId:N}`, `GerarLinkPublico=true`) → gravar `id_intent` + `expira_em` na mesma transação em que a cobrança vai a `aguardando` (ou deixar o `ChatCobrancaEfeito` fazer isso quando o `id_intent` já estiver gravado antes do Pix). Falha 4xx do MP → `recusado`.

### 8.6 Página Pix pública (✅ núcleo; anônimo; sem login, sem dados sensíveis)
| Método e rota | Resposta `data` | Erros |
|---|---|---|
| `GET /api/public/pagamentos/pix/{token}` | `PublicPixDto` `{ status, versao, valor, descricao, copiaECola, qrCodeBase64, expiraEm, lojaNome }` — QR só enquanto pendente | 404 (`PAG_NAO_ENCONTRADO`, token inválido/inexistente), 429 |
Token: 32 bytes aleatórios em base64url (43 caracteres), só o SHA-256 fica no banco (`pagamento_intent.token_publico_hash`). Nada de nome/telefone/CPF do cliente, conversa ou dado bancário. Rota sugerida no front: `/pix/{token}`.

### 8.7 Loja autenticada (✅ núcleo)
| Método e rota | Resposta `data` | Erros |
|---|---|---|
| `GET /api/v2/pagamentos/{intentId}` (`Financeiro:visualizar`) | `PagamentoResumoDto` `{ id, origem, status, versao, valor, valorPago, valorEstornado, descricao, pedidoId, prePedidoId, conversaId, metodo, statusPagamento, statusDetalhe, cartaoBandeira, cartaoFinal, expiraEm, pagoEm, criadoEm, atualizadoEm }` | 403, 404 |
| `GET /api/v2/pagamentos?pedidoId=&prePedidoId=&conversaId=` (`Financeiro:visualizar`) | `PagamentoResumoDto[]` (máx. 50) | 400, 403 |
| `POST /api/v2/pagamentos/{intentId}/estornos` (`Financeiro:cancelar`) | corpo `{ valor?, motivo?, idempotencyKey }` (ou header `Idempotency-Key`) → `EstornoResult` | 400, 403, 404, 409, 422, 502, 503 |

### 8.8 Removidas
`POST /api/checkout/pix`, `POST /api/checkout/cartao`, `GET /api/checkout/{id}`, `POST /api/checkout/public/pix|cartao` e `POST /api/webhooks/asaas` deixaram de existir (Asaas fora do caminho ativo; aceitavam valor do cliente).

---

## 9. Configuração

| Chave (env com `__`) | Obrigatória | Descrição |
|---|---|---|
| `Payments__ChaveMestra` ou `PAGAMENTOS_CHAVE_MESTRA` | sim | base64 de 32 bytes. Perder = reconectar todas as lojas e recadastrar segredos |
| `Payments__ChaveMestraAnterior` | rotação | chave antiga (só leitura) |
| `Payments__MercadoPago__Ambiente` | sim | `sandbox` \| `producao` |
| `Payments__MercadoPago__NotificationUrl` | sim | `https://{api}/api/webhooks/mercadopago` (vai em cada pagamento) |
| `Payments__MercadoPago__WebhookToleranciaSegundos` | não | 600 |
| `Payments__MercadoPago__ExigirLiveModeDoAmbiente` | não | `true` |
| `Payments__MercadoPago__PixValidadePadraoMinutos` | não | 30 |
| `Payments__MercadoPago__EmailPagadorPadrao` | não | e-mail usado quando o cliente não informou (o MP exige no Pix) |
| `Payments__MercadoPago__StatementDescriptor` | não | `ZIPPY` (fatura do cartão) |
| `Payments__MercadoPago__SplitHabilitado` / `ComissaoPercentual` | não | `false` / `0` — não ligar |
| `Payments__MercadoPago__Conciliador*`, `WebhookMaxTentativas`, `OutboxIntervaloMilissegundos` | não | ajustes finos |
| `Payments__MercadoPago__Bootstrap*` | não | só sandbox antes da tela da Gestão |
| `Payments__MercadoPago__PainelUrl` | sim (OAuth) | `https://{painel}`: destino do callback OAuth (sem valor: `Rastreio__PublicBaseUrl`) |
| `Payments__MercadoPago__ApiPublicaUrl` | não | `https://{api}` mostrado na Gestão para callback e webhook (sem valor: origem da `NotificationUrl`) |
As variáveis `Payments__Asaas__*` podem ser apagadas do Render.

---

## 10. O que habilitar/configurar manualmente no Mercado Pago antes da produção

1. **Aplicação** no [Painel do desenvolvedor](https://www.mercadopago.com.br/developers/panel/app) da conta Zippy: produto "Pagamentos online" / **CheckoutAPI** (Payment Brick + API de pagamentos), modelo **marketplace** (para OAuth). Ativar as **credenciais de produção** (o MP pede dados do negócio e site).
2. **OAuth**: cadastrar a **Redirect URL** exata `https://{api}/api/integracoes/mercadopago/callback` (sandbox e produção); habilitar **PKCE** se a aplicação exigir; conferir os escopos `offline_access read write`.
3. **Webhooks** (Suas integrações → Webhooks) para cada modo (teste e produção): URL `https://{api}/api/webhooks/mercadopago`, eventos **Pagamentos** (`payment`), **Contestações/Chargebacks**, **Vinculação de aplicações** (`mp-connect`), **Planos e assinaturas** (`subscription_preapproval`, `subscription_authorized_payment`). Copiar a **assinatura secreta** de cada modo para `mp_aplicacao.webhook_secret` (Gestão).
4. **Conta recebedora Zippy** e **contas das lojas**: cada uma precisa de **chave Pix cadastrada** no Mercado Pago (sem chave Pix o MP recusa `payment_method_id=pix`), conta verificada (identidade/CNPJ) e, para cartão, aceite dos termos de recebimento.
5. Usuários de teste (vendedor e comprador) para homologar o OAuth e o Pix no sandbox; Pix de teste é aprovado pelo próprio painel/API de teste.
6. **Qualidade da integração** (medidor do painel): mandar `payer.email`, `description`, `external_reference`, `notification_url`, `statement_descriptor`, device id do Brick no cartão e `X-Idempotency-Key` (já feito pelo núcleo). Pedir a homologação/selo se o MP exigir para liberar produção.
7. Pix: confirmar a **validade máxima** permitida (`date_of_expiration`, até 30 dias) e que o prazo padrão (30 min) cabe no fluxo.
8. Cartão: definir parcelamento e se há **juros** repassados (`cartao_parcelas_max` por loja); revisar **antifraude** e `binary_mode` (hoje desligado: aceita `in_process`).
9. Comissão de marketplace: manter **zero** (não enviar `application_fee`).
10. Gerar e guardar a **chave mestra** (`PAGAMENTOS_CHAVE_MESTRA`) no Render (secret) com backup em cofre; testar rotação em sandbox.
11. Produção: `Payments__MercadoPago__Ambiente=producao`, `NotificationUrl` apontando para a API de produção, CORS do painel liberado para o callback.
