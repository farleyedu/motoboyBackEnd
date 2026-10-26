# Mercado Pago — status (handoff curto)

Atualizado em 2026-10-26 (Orders API). Contrato completo: `docs/pagamentos/CONTRATOS.md` (fonte de verdade — ler só a seção da parte em que for trabalhar).
Repositórios: backend `motoboyBackEnd` (master), frontend `../zippy-admin` (main).

## Feito
| Parte | Onde | Estado |
|---|---|---|
| MP 1 — Núcleo (Pix, cartão, estorno, webhook, conciliador, página Pix pública, rotas da loja §8.6/8.7) | backend | ✅ commit `dcd710b` |
| MP 2 — Gestão Pagamentos + OAuth das lojas (§8.1/8.2) | backend | ✅ commit `f08ecd9` |
| MP 3 — Cardápio web: backend (pagamento antes do aceite, estorno, worker) | backend | ✅ commit `f566809` (build ok, testes unitários ok) |
| MP 3 — Cardápio web: tipos + cliente `pagamentoOnlineApi.ts` | zippy-admin | ✅ commit `f796f2a` (tsc ok) |
| MP 5 — Cobrança Pix no chat (backend) | backend | ✅ commit `f566809` (`Payments/Chat/*`, build ok, testes unitários ok; testes de banco não rodados) |
| Migração `v1/payments` → Orders API (`v1/orders`) | backend | ✅ 2026-10-26 — ver "Orders API" abaixo. Build ok, 2143 testes unitários ok; testes de banco ajustados (MP falso em `Tests/Integration/MpOrdersFalso.cs`) mas **não rodados** (sem `TEST_DELIVERY_DATABASE`). |

## Orders API (Checkout Transparente)
- **Aplicar no banco:** `Migrations/Delivery/20261103_70_mercadopago_ambiente_ativo.sql` (tabela `mp_ambiente_ativo`; verify `_71`, rollback em `rollback/`) e `Migrations/Delivery/20261102_60_mercadopago_orders.sql` (coluna `pagamento.mp_order_id`; verify `_61`, rollback em `rollback/`).
- **Painel do MP (aplicação ZippyGo):** habilitar o tópico **`order`** em Webhooks (além de `payment`, que as assinaturas ainda usam) e usar a mesma URL/segredo.
- Criação: `POST v1/orders` (`type=online`, `processing_mode=automatic`, valores em string `"0.00"`, 1 pagamento por order, `X-Idempotency-Key` = chave da tentativa). Pix: `payment_method {id:pix,type:bank_transfer}` + `expiration_time` ISO 8601 (mínimo 30 min no MP; validade menor vale só localmente). Cartão: `{id:<bandeira>,type:credit_card|debit_card,token,installments,statement_descriptor}`; `issuer_id`, `notification_url` e `metadata` não existem na Orders (o webhook vem da aplicação; a intenção é achada pelo `external_reference`). `paymentTypeId` do Brick é opcional na rota pública do cartão (padrão `credit_card`).
- Consulta `GET v1/orders/{id}`; cancelar Pix `POST v1/orders/{id}/cancel`; estorno `POST v1/orders/{id}/refund` (total: corpo vazio; parcial: `{transactions:[{id:"PAY...",amount:"x.xx"}]}`, o `PAY...` vem de um GET da order).
- Modelo interno: o cliente traduz a order para o `MpPagamento` de antes (`MpOrder.ParaPagamento`). `mp_payment_id` = `reference_id` do pagamento da order (numérico; sem ele, hash estável do `PAY...`), `mp_order_id` = `ORD...`. Status da Orders entram em `PagamentoStatusRules.Traduzir`; recusas de cartão voltam como `cc_rejected_*` (`DetalheCompativel`) para o front não mudar. Contratos públicos (`CONTRATOS.md`) sem mudança.
- Mantido na API v1: `GET v1/payments/{id}` e `.../refunds` só para pagamentos sem `mp_order_id` (cobranças recorrentes das assinaturas), chargebacks, OAuth, `users/me`, `payment_methods`, preapproval.
- Sem busca por `external_reference` na Orders: o conciliador não recupera mais tentativa `criando` por busca; o aviso `order` cria o pagamento e o conciliador só vincula a tentativa (ou a marca `falhou` após 30 min sem order).
- **Não validado contra o sandbox do MP** (doc consultada: criação, status, webhook, refund total; confirmar no sandbox: corpo do refund parcial, `reference_id` presente em Pix e cartão, `live_mode`/moeda na resposta, resposta do `cancel`, expiração do Pix).

## Pendente (todas as sessões 2–6 pararam por limite de uso)
| Parte | O que falta | Contrato |
|---|---|---|
| ~~MP 2 — UI~~ | ✅ feito, commit `31931ff` (zippy-admin): `/apps/gestao/pagamentos` (superadmin) e card Mercado Pago em Configurações > Integrações. tsc ok; **não testado no navegador**. | §8.1, §8.2 |
| ~~MP 3 — UI cardápio~~ | ✅ feito, commit `31931ff`: opções "Pix online"/"Cartão online" no checkout (só com conta MP conectada), `PagamentoOnlinePanel` (QR/copia e cola e Payment Brick) quando o pedido está `aguardando_pagamento`. tsc + 66 testes ok; **não testado no navegador**. | §8.3 |
| Resto não commitado no zippy-admin | `PlataformaScreen.tsx` (mistura card de resumo MP com `MetaAppCard`), `MetaAppCard.tsx`, `conexaoMetaApi.ts` — são da sessão do WhatsApp Meta; não mexer aqui. | — |
| MP 4 — Assinaturas e compras Zippy | Backend em `f566809` (`Payments/Assinaturas`, migration 40). UI feita no admin (commits `f299dc9` + `de2e56a`, detalhes no padrão OrderFocus; conferido em 2026-10-26: o cliente cobre `assinatura`, `assinaturas`/`alterar`/`cancelar`/`retomar`, `cobrancas`/`{id}/pagar`, `compras`, `solicitacoes-compra`/`{id}/decisao`; tsc ok, 1124 testes ok — os 2 timeouts do delivery passam isolados): Configurações > Assinatura (plano, cobranças, Pix/cartão com Card Brick, troca com previsão, cancelar/retomar, extras, decisão de solicitações) e ficha da loja na Gestão (assinatura, cobranças, baixa manual). Falta: validar no sandbox do MP (`/api/v2/comercial/*` + webhooks `subscription_*`), ver as telas no navegador com login, e a visão geral de cobranças da Gestão (só há a ficha por loja). | §8.4, §5.3, §2.15 |
| ~~MP 6 — Chat UI~~ | ✅ feito, commit `d95313e` (zippy-admin): botão "Cobrar Pix", balão com SignalR (`cobrancaAtualizada`), página `/pix/{token}` com polling de 3 s, botão "Devolver valor" (reembolso direto ou pedido ao gerente em `PAG_EXIGE_APROVACAO`); cliente já tem aprovações e config, **sem tela** de fila do gerente nem config. tsc ok, 188 testes de chat/pix ok (reconferido em 2026-10-26); **não testado no navegador**. | §8.5, §8.6, §6 |

## Ordem sugerida
1. ~~MP 2 UI + MP 3 UI~~ feitos.
2. MP 6 UI → 3. MP 4. Antes: teste ponta a ponta no sandbox do MP (migrations 30/40/50 no banco, webhook).

## Regras para economizar tokens
- Uma sessão nova por parte, iniciada só com este arquivo + a seção do contrato.
- Não reler o CONTRATOS.md inteiro; não varrer o projeto.
- Ao terminar a parte: commitar, atualizar esta tabela, encerrar a sessão.
