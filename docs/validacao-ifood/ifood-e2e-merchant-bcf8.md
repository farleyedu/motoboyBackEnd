# Validação ponta a ponta — merchant `7c0ef690-b675-4cef-9819-7085fcbbbcf8` (loja `a06a0737-…`)

Roteiro para rodar **com autorização**, depois do deploy da migration `20261027_01_ifood_connection_lifecycle`.
Nada aqui foi executado contra o iFood real pelo desenvolvimento; cada passo diz o que observar.

## Antes
1. Deploy do backend (migration roda no boot). Conferir `delivery_tracking_schema_versions` com `20261027_01_ifood_connection_lifecycle`.
2. Gestão › Configuração iFood › **Merchants e diagnóstico**. A conexão `…bcf7` (loja `97c5d396-…`) deve aparecer como
   *Não autorizada no iFood* ou *iFood recusa a leitura (403)* — não mais como "Autorizado". Ela fica isolada e não bloqueia a `…bcf8`.
   Ações disponíveis nela sem SQL: **Conferir agora**, **Desconectar**, **Remover** (se nunca usada) ou **Transferir**.

## Pedido real
| # | Ação | O que deve acontecer | Onde ver |
|---|------|----------------------|----------|
| 1 | `…bcf8` → **Conferir agora** | Mensagem "O iFood confirmou a conexão desta loja"; chip passa a *Sem confirmação* → *Operacional* após o 1º polling | Painel de diagnóstico |
| 2 | Fazer um pedido de teste no iFood | Em até ~30 s: evento `PLC` recebido (polling) | `ifood_events_inbox` (state `queued`→`processed`), card "eventos na fila" |
| 3 | ACK | `acknowledged_at_utc` preenchido, `ack_last_error_code` nulo | `ifood_events_inbox`, card "aguardando ACK = 0" |
| 4 | Importação | Linha em `ifood_order_links`/`ifood_order_snapshots` e `pedido` criado | Pedidos › origem iFood |
| 5 | Exibição | Pedido aparece no Modo Comando / Pedidos da loja `a06a0737-…` | Zippy Admin |

## Prova do isolamento (mesmo ambiente)
- Com `…bcf7` ainda cadastrada: nos logs, `Polling iFood interrompido` **não** deve se repetir; `ifood_connections.poll_state='forbidden'`
  e `poll_blocked_until_utc` no futuro só em `…bcf7`; `…bcf8` com `poll_state='ok'` e `last_poll_ok_at_utc` recente.
- Corrigir `…bcf7` sem SQL: Gestão › **Desconectar** → **Remover** (ou **Transferir** para a loja certa). Histórico fica em `ifood_connection_audit`.

## Critérios de falha (parar e investigar)
- `…bcf8` com `poll_state='forbidden'`: a autorização do app na loja ainda não propagou no iFood — aguardar (o backoff vai de 1 a 60 min) ou usar **Conferir agora**.
- Evento sem ACK por mais de 5 min com `ack_last_error_code` preenchido.
