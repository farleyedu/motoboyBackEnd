# InfinitePay — auditoria e plano de liberação (prompt 07)

Estado: **NÃO pronto para produção.** Nenhum fluxo financeiro real foi validado. Trabalho dos prompts 01–06 está não commitado na working tree (master / main).

## Matriz (executado em 2026-11-09)

| Cenário | Teste executado | Resultado | Bloqueio |
|---|---|---|---|
| Backend: criação de link, HTTPS, 403/429/503, timeout, centavos, sem PII no order_nsu | `InfinitePayCheckoutTests` (unit) | PASSOU | — |
| Webhook sem assinatura, /payment_check, amount original x paid_amount, divergência, hash/idempotência, backoff, limite de corpo | `InfinitePayWebhookTests` (unit) | PASSOU | — |
| Checkout hospedado do cardápio, chat link x Pix, regras de recebimento | `CardapioCheckoutHospedadoTests`, `CobrancaChatLinkTests`, `CobrancaChatRegrasTests`, `RecebimentosRegrasTests` | PASSOU | — |
| Preservação Mercado Pago | `MercadoPagoNucleoTests`, `MercadoPagoAmbienteAtivoTests` | PASSOU | — |
| Loja errada, double-submit, concorrência, recálculo de totais, eventos antigos, migrations/rollback, caixa (banco real) | `*DatabaseTests` (13 testes) | **NÃO RODOU** (ignorados: `TEST_DELIVERY_DATABASE` ausente) | Precisa de banco de teste |
| Frontend: cartão InfinitePay, regras, recebimentos, checkout, cobranças, caixa | vitest (31 arquivos, 310 testes) | PASSOU | — |
| Chave nunca no front | grep + `InfinitePayView.test.tsx`: UI só recebe `temApiKey` (bool), input `type=password`, limpa após salvar | PASSOU (leitura + teste de UI) | — |
| Chave/corpo em logs do backend | grep de chamadas de log em `Payments/InfinitePay` | Nenhuma ocorrência | Revisão manual de logs em runtime pendente |
| DI/worker | `Program.cs:416` → `AddPagamentosInfinitePay`; worker registrado | Confirmado por leitura | Worker sem migrations 20261129_* só registra aviso |
| Switch de provedor, permissões, chat Pix-only não enviando link multi-método, desconto Pix | testes de UI/regra existentes não cobrem de ponta a ponta | **NÃO RODOU** | Exige ambiente integrado |
| Pix-only com InfinitePay | Não suportado: não há como impedir cartão | Deve ficar rotulado "Pix ou cartão" | Aprovação de produto |
| Pagamento real R$ 1,00 | — | **NÃO RODOU** | Autorização do proprietário |

## Pontos de atenção para as sessões donas
- Checar que migrations `20261129_*` e `20261201_*` aplicam e revertem (`Migrations/Delivery/rollback/`) em banco de teste antes de qualquer piloto. Não há rollback para os `_verify` (esperado).
- `20261201_01_pagamento_estorno_informado` mexe em estorno; confirmar que é só registro informado (InfinitePay não tem estorno por API) e que não aparece botão de estorno automático para InfinitePay.
- Diff de Mercado Pago (`PagamentoCriacao.cs`, `PagamentoEstornoService.cs`, `PagamentosControllers.cs`) foi tocado; os testes MP passam, mas revisar o diff antes do merge.

## Plano de ensaio real (exige autorização explícita do proprietário)
Sem sandbox. Conta InfinitePay da loja-teste, pagamento real de R$ 1,00, executado pelo proprietário ou com ele presente.
1. Pré-condições: migrations aplicadas em banco de teste, flag da loja-teste ligada, handle cadastrado, HTTPS público para webhook.
2. Criar link pelo cardápio → conferir URL HTTPS, `order_nsu` sem PII, valor em centavos.
3. Pagar no navegador (Pix ou cartão) → voltar pelo redirect: **a tela não pode marcar pago sozinha**.
4. Confirmar webhook recebido, `/payment_check` com `order_nsu`, `transaction_nsu`, `slug`, conta e `amount` original; pedido muda para pago; caixa recebe o valor; comprovante/retorno.
5. Repetir o mesmo webhook (idempotência) e um webhook falsificado (deve ser descartado sem efeito).
6. Registrar evidência: IDs, status, horário, valor, resultado. Nunca chave ou handle completo.
7. Se webhook não chegar ou `/payment_check` falhar: marcar **não validado** e bloquear a liberação. Estorno/cancelamento só pelo App/Conta Web, com o titular.

## Rollout
- Flag por loja (`habilitada` na conta InfinitePay); piloto numa única loja.
- Monitor: eventos de webhook não reconciliados, cobranças pendentes além da validade, divergência de valor, falhas de `/payment_check` (contagem e tempo, sem PII).
- Suporte manual: pedido pendente com pagamento confirmado na InfinitePay → conferir no App/Conta Web, reprocessar o aviso, nunca marcar pago à mão sem registrar auditoria.
- Kill switch: desligar `habilitada` bloqueia novos links; cobranças já emitidas continuam sendo reconciliadas pelo worker.
- Fallback para Mercado Pago apenas se a loja tiver conta MP ativa; intenções antigas não são alteradas.

## O que NÃO existe
Checkout transparente, Pix QR/copia-e-cola por API, checkout Pix-only, estorno/cancelamento por API, split, recorrência, sandbox, tokenização. O InfiniteTap (deeplink de maquininha) é outra integração, futura, e não faz parte do checkout online.
