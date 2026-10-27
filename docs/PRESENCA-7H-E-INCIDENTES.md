# Presença de 7 horas e incidentes de desconexão — 27/10/2026

O turno mobile (PWA e Android nativo) fica aberto até o motoboy encerrar ou até **7 horas sem atividade humana**. Heartbeat, GPS, abertura de conexão e entrega de push **não** adiam esse prazo. O simulador conserva a política própria (30 dias).

## Prazos independentes

| Sinal | Fonte | Efeito |
| --- | --- | --- |
| Conexão | `last_heartbeat_at_utc` / `last_seen_at` + 12 min (`PresenceTtlSeconds`) | só `presenceStatus` (`connected`/`disconnected`); o turno continua ativo, elegível a ofertas e push |
| Fim do turno | `last_human_activity_at_utc` + 7 h (`HumanInactivitySeconds`) | `expires_at_utc` em turnos mobile; vencido, o worker encerra **uma vez** com `end_reason = inactivity_7h` |

`expires_at_utc` passa a ser, para turnos mobile, o fim por inatividade. Por isso todos os filtros já existentes (`expires_at_utc > NOW()`: push, ofertas, snapshot, middleware, conclusão) valem a janela de 7 h sem mudança. Heartbeat e GPS não tocam nele; a migração dá 7 h cheias aos turnos abertos na publicação e não estende nada ao ser reaplicada.

Contam como atividade humana: início do turno, qualquer ação do motoboy na fila (aceite/recusa, retirada, chegada, conclusão, falha, transferência, pausa/retomada, mensagens, preferências) e `POST /api/v2/motoboys/me/session/activity`. Consultas (GET) e retomada automática não contam. A gravação é limitada por frequência (`ActivityMinIntervalSeconds`, 30 s): dentro do intervalo a resposta traz `recorded=false` e o prazo atual.

## Contratos do app (`/api/v2/motoboys/me/session`)

- `POST activity` → `{ recorded, serverTimeUtc, lastHumanActivityAtUtc, inactivityExpiresAtUtc, inactivityRemainingSeconds }`.
- `OperationalSessionDto` ganhou `lastContactAtUtc`, `lastHumanActivityAtUtc`, `inactivityExpiresAtUtc`, `inactivityRemainingSeconds`; `presenceExpiresAtUtc` agora é o fim da janela de **conexão** (contato + 12 min); `endReason` informa `inactivity_7h`, `client_end`, `explicit_switch`, `login_replaced`, `logout`, `link_revoked`...
- `DELETE ?confirmPendingWork=true`: com pedidos ativos, sem a confirmação segue `409 MOTOBOY_HAS_PENDING_WORK` (com `details.canConfirm`); com ela o turno encerra e a loja assume os pedidos como incidente.
- `start`, `queue`, `heartbeat`, `location` e `end` mantêm seus contratos.

## Snapshot da loja — `GET /api/v2/delivery/tracking-snapshot`

Por motoboy, campos novos: `clientPlatform` (`native`/`web`/`simulator`), `shiftStartedAtUtc`, `lastHeartbeatAtUtc`, `lastContactAtUtc`, `lastHumanActivityAtUtc`, `presenceStatus`, `pushCapability` (`enabled` com inscrição de push na sessão, `unavailable`, `unknown` sem a tabela), `inactivityDeadlineAtUtc`, `inactivityRemainingSeconds`, `hasRecentLocation`. `status` continua `online`/`delivering`/`returning` enquanto o turno existe; `presence.offline` só sai no encerramento. Os eventos `presence.online` / `presence.heartbeat` carregam os mesmos campos de contato/atividade.

## Incidentes de desconexão

Encerrar de verdade (saída manual confirmada, inatividade, troca de aparelho/estabelecimento, logout, revogação do vínculo) cria, **na mesma transação** do encerramento e uma única vez por turno (`UNIQUE(session_id)`), um incidente quando há pedidos nas filas `assigned`/`en_route`:

- **Não retirado** (`picked_up_at_utc` nulo): a parada sai da fila (`removed`), o pedido volta a `Pendente` sem responsável — sob gestão do restaurante.
- **Retirado/em rota**: nada muda (parada, responsável, status e retirada continuam; não se falsifica retorno à loja). Fica `collected = true` até a loja agir.
- Pendências de transferência desses pedidos são canceladas. Concluídos/cancelados/encerrados não entram.
- Guarda motivo, horários, último contato, última localização, plataforma, etapa e foto do pagamento de cada pedido. O que já existe (pedido, código, comprovante, recebimentos, SLA, eventos iFood) não é tocado.
- A trava da fila do motoboy é tomada **antes** da sessão (mesma ordem dos comandos de fila) para evitar deadlock entre encerrar, aceitar, transferir e concluir. Itens cujo pedido seguiu por outro caminho são reconhecidos sozinhos (`reatribuido`, `transferido`, `pedido_encerrado`).

### `GET /api/v2/delivery/atencao-motoboy?status=abertos|todos&desde=<utc>` (Delivery/visualizar)

`{ serverTimeUtc, incidentes[] }`; cada incidente traz `id`, `version`, `status` (`aberto`/`aguardando_retorno`/`resolvido`), `motivo`, `motivoTexto`, `abertoEmUtc`, `turnoEncerradoEmUtc`, `resolvidoEmUtc`, `resolvidoPorNome`, `motoboy{ id, nome, avatar, reconectado, ultimoContatoUtc, ultimaAtividadeUtc }`, `ultimaLocalizacao`, `pedidos[]` (`estagio` `na_loja`/`coletado`/`em_rota`, `coletado`, `devolucaoConfirmada`, `situacao`, `resolucao`, `acoes[]` com `permitida`, `motivoBloqueio`, `exigeJustificativa`, `exigeConfirmacao`), `acoesIncidente[]`, `historico[]`. `GET .../{id}` devolve um incidente.

### `POST /api/v2/delivery/atencao-motoboy/{incidenteId}/acoes` (Delivery/atribuir_motoboy)

Corpo: `{ tipoAcao, pedidoIds, motoboyDestinoId, motivo, versaoEsperada, idempotencyKey }` (o header `Idempotency-Key` vale se a chave não vier no corpo). Resposta: `{ incidente, resultados[{ pedidoId, ok, code, mensagem }] }` (lote parcial permitido). Erros: 403 sem permissão, 404 incidente de outra loja/inexistente, 409 `VERSAO_DESATUALIZADA` (`data` = incidente atual) ou `ACAO_EM_ANDAMENTO`, 422 `ACAO_NAO_PERMITIDA`, `JUSTIFICATIVA_OBRIGATORIA`, `DESTINO_OBRIGATORIO`, `DESTINO_INVALIDO`, `ACAO_DESCONHECIDA`, `IDEMPOTENCY_KEY_REQUIRED`, `VERSAO_OBRIGATORIA`.

| `tipoAcao` | Efeito |
| --- | --- |
| `aguardar_contato` | incidente → `aguardando_retorno` |
| `registrar_contato` | registra a tentativa no histórico (justificativa obrigatória) |
| `reofertar_mesmo_motoboy` | só com o motoboy reconectado: pedido na loja é oferecido a ele (atribuição/oferta existente); coletado confere que segue na fila dele |
| `redistribuir` | pedido na loja → `AssignAsync`/`AssignRouteAsync` para `motoboyDestinoId`; **coletado só após `confirmar_devolucao_fisica`** |
| `confirmar_devolucao_fisica` | coletado: libera a parada do motoboy anterior e devolve o pedido a pendente (sem apagar o responsável histórico, que fica no incidente) |
| `registrar_falha` | coletado (devolvido ou com autorização excepcional): registra a falha e devolve o pedido à loja |
| `concluir_manual` | conclusão manual existente (`CompleteStopAsync` ou entrega manual), justificativa obrigatória |
| `cancelar` | cancelamento existente; coletado ainda com o motoboy exige autorização excepcional (`Delivery/configurar`) |

Cada ação revalida o estado sob as travas das operações de fila existentes, por pedido. O incidente reserva a versão antes de operar (uma segunda ação com a mesma versão toma 409) e a chave de idempotência guarda o resultado: repetir devolve o mesmo resultado sem repetir a operação. Quando não resta pedido pendente o incidente vira `resolvido` e continua em `status=todos`.

Realtime (mesmo hub/outbox): `delivery.attention.opened|updated|resolved` com `{ eventId, estabelecimentoId, incidenteId, version, occurredAtUtc, incidente }`. O incidente do evento calcula as ações **sem** a autorização excepcional; o GET traz o cálculo do usuário logado.

## Publicação

1. Publicar a API; com `DeliveryTracking:ApplyMigrationsOnStartup` o runner aplica `20261104_01_presenca_7h_incidentes.sql` (conferência em `20261104_02_..._verify.sql`, rollback em `rollback/20261104_01_...down.sql`). Sem a migração a API segue funcionando como antes, só que sem incidentes.
2. Depois publicar os frontends. A migração dá 7 h cheias aos turnos abertos naquele momento.

Fora do escopo: fluxo nativo de GPS, regras de cancelamento/reembolso do iFood, reatribuição automática de pedido já coletado.
