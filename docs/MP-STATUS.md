# Mercado Pago — status (handoff curto)

Atualizado em 2026-10-26. Contrato completo: `docs/pagamentos/CONTRATOS.md` (fonte de verdade — ler só a seção da parte em que for trabalhar).
Repositórios: backend `motoboyBackEnd` (master), frontend `../zippy-admin` (main).

## Feito
| Parte | Onde | Estado |
|---|---|---|
| MP 1 — Núcleo (Pix, cartão, estorno, webhook, conciliador, página Pix pública, rotas da loja §8.6/8.7) | backend | ✅ commit `dcd710b` |
| MP 2 — Gestão Pagamentos + OAuth das lojas (§8.1/8.2) | backend | ✅ commit `f08ecd9` |
| MP 3 — Cardápio web: rotas públicas (§8.3) | backend | ✅ (veio no núcleo) |

## Pendente (todas as sessões 2–6 pararam por limite de uso)
| Parte | O que falta | Contrato |
|---|---|---|
| MP 2 — UI | Frontend **não commitado** em zippy-admin: `features/gestao/pagamentos/`, `app/(DashboardLayout)/apps/gestao/(central)/pagamentos/`, `features/pagamentos/MercadoPagoCard.tsx`, `integracoes/IntegracoesScreen.tsx`, `gestao/areas.ts`. Revisar, compilar e commitar. | §8.1, §8.2 |
| MP 3 — UI cardápio | Frontend **não commitado**: `cardapio/checkout/pagamentoOnlineApi.ts`, `checkout/types.ts`. Falta passo de pagamento no checkout (Pix + Payment Brick) e polling de status. | §8.3 |
| MP 4 — Assinaturas e compras Zippy | Nada no código ainda. Backend `/api/v2/comercial/*` + webhooks `subscription_*`, depois UI. | §8.4, §5.3, §2.15 |
| MP 5 — Cobrança Pix no chat (backend) | Nada no código ainda. Permissão `WhatsApp:cobrar`, rotas `/api/v2/atendimento/.../cobrancas`, fluxo do POST descrito no fim da §8.5. | §8.5, §3.3, §6 |
| MP 6 — Chat UI | Botão "Cobrar Pix", balão de cobrança com status em tempo real (SignalR), página `/pix/{token}`. Depende do MP 5. | §8.5, §8.6, §6 |

## Ordem sugerida
1. MP 2 UI + MP 3 UI (só revisar/fechar o que já está no zippy-admin e commitar).
2. MP 5 backend → 3. MP 6 UI → 4. MP 4.

## Regras para economizar tokens
- Uma sessão nova por parte, iniciada só com este arquivo + a seção do contrato.
- Não reler o CONTRATOS.md inteiro; não varrer o projeto.
- Ao terminar a parte: commitar, atualizar esta tabela, encerrar a sessão.
