# Mercado Pago — status (handoff curto)

Atualizado em 2026-10-26. Contrato completo: `docs/pagamentos/CONTRATOS.md` (fonte de verdade — ler só a seção da parte em que for trabalhar).
Repositórios: backend `motoboyBackEnd` (master), frontend `../zippy-admin` (main).

## Feito
| Parte | Onde | Estado |
|---|---|---|
| MP 1 — Núcleo (Pix, cartão, estorno, webhook, conciliador, página Pix pública, rotas da loja §8.6/8.7) | backend | ✅ commit `dcd710b` |
| MP 2 — Gestão Pagamentos + OAuth das lojas (§8.1/8.2) | backend | ✅ commit `f08ecd9` |
| MP 3 — Cardápio web: backend (pagamento antes do aceite, estorno, worker) | backend | ✅ commit `f566809` (build ok, testes unitários ok) |
| MP 3 — Cardápio web: tipos + cliente `pagamentoOnlineApi.ts` | zippy-admin | ✅ commit `f796f2a` (tsc ok) |
| MP 5 — Cobrança Pix no chat (backend) | backend | ✅ commit `f566809` (`Payments/Chat/*`, build ok, testes unitários ok; testes de banco não rodados) |

## Pendente (todas as sessões 2–6 pararam por limite de uso)
| Parte | O que falta | Contrato |
|---|---|---|
| ~~MP 2 — UI~~ | ✅ feito, commit `31931ff` (zippy-admin): `/apps/gestao/pagamentos` (superadmin) e card Mercado Pago em Configurações > Integrações. tsc ok; **não testado no navegador**. | §8.1, §8.2 |
| ~~MP 3 — UI cardápio~~ | ✅ feito, commit `31931ff`: opções "Pix online"/"Cartão online" no checkout (só com conta MP conectada), `PagamentoOnlinePanel` (QR/copia e cola e Payment Brick) quando o pedido está `aguardando_pagamento`. tsc + 66 testes ok; **não testado no navegador**. | §8.3 |
| Resto não commitado no zippy-admin | `PlataformaScreen.tsx` (mistura card de resumo MP com `MetaAppCard`), `MetaAppCard.tsx`, `conexaoMetaApi.ts` — são da sessão do WhatsApp Meta; não mexer aqui. | — |
| MP 4 — Assinaturas e compras Zippy | Backend em `f566809` (`Payments/Assinaturas`, migration 40). UI feita no admin (commit `f299dc9`): Configurações > Assinatura (plano, cobranças, Pix/cartão com Card Brick, troca com previsão, cancelar/retomar, extras, decisão de solicitações) e ficha da loja na Gestão (assinatura, cobranças, baixa manual). Falta: validar no sandbox do MP (`/api/v2/comercial/*` + webhooks `subscription_*`), ver as telas no navegador com login, e a visão geral de cobranças da Gestão (só há a ficha por loja). | §8.4, §5.3, §2.15 |
| ~~MP 6 — Chat UI~~ | ✅ feito (zippy-admin): botão "Cobrar Pix", balão com SignalR (`cobrancaAtualizada`), página `/pix/{token}` com polling de 3 s, botão "Devolver valor" (reembolso direto ou pedido ao gerente em `PAG_EXIGE_APROVACAO`); cliente já tem aprovações e config, **sem tela** de fila do gerente nem config. tsc + vitest ok; **não testado no navegador**. | §8.5, §8.6, §6 |

## Ordem sugerida
1. ~~MP 2 UI + MP 3 UI~~ feitos.
2. MP 6 UI → 3. MP 4. Antes: teste ponta a ponta no sandbox do MP (migrations 30/40/50 no banco, webhook).

## Regras para economizar tokens
- Uma sessão nova por parte, iniciada só com este arquivo + a seção do contrato.
- Não reler o CONTRATOS.md inteiro; não varrer o projeto.
- Ao terminar a parte: commitar, atualizar esta tabela, encerrar a sessão.
