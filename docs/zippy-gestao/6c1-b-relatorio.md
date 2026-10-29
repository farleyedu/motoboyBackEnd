# 6C1-B: recebíveis e repasse do iFood (29/10/2026)

Clone `motoboyBackEnd-6c1b`, branch `master`, sem push. Base `9ba4276`.

## Fonte oficial de repasse: não existe

A integração iFood só usa Merchant API de autenticação, eventos, pedidos, merchants e disputas
(`Integrations/Ifood/*`). Não há cliente da API Financeira (vendas, repasses ou conciliação). O snapshot do pedido traz
`order_amount`, `prepaid_amount` e `pending_amount`: o que o cliente pagou online e o que paga na entrega, **não** o repasse
bancário nem a comissão. Por isso a liquidação é manual. Automação = pendência da Integração (abaixo).

## Contrato (para consolidar em CONTRATOS §9, número a definir na consolidação)

Rotas em `/api/gestao/ifood` (`Controllers/GestaoRepasseIfoodController.cs`). Só gerente com `PodeLancar` (`SEM_PERMISSAO`, 403).
O serviço (`GestaoLogica/RepasseIfood/RepasseIfoodService.cs`) é criado pelo controller com `NpgsqlDataSource` e
`IAcontecimentosService` do DI: `Program.cs` não mudou.

| Rota | Entrada | Saída |
| --- | --- | --- |
| `GET recebiveis` | — | `[{ pedidoId, vendaId, lancamentoId, descricao, dataFato, valorAReceber, divergencias[{ id, valorRecebido, dataPagamento, referencia, registradoEm }] }]`: linhas a receber no recebível iFood de vendas não estornadas |
| `POST liquidacoes` | `{ chaveIdempotencia, pedidoId, contaDestinoId, dataPagamento, valorRecebido, referencia?, confirmacaoHumana }` | `{ id, pedidoId, vendaId, lancamentoId, valorAReceber, valorRecebido, valorLiquidado, situacao, situacaoFinanceira, pagamentoId?, contaDestinoId, dataPagamento, referencia, repetido }` |

- Acha a venda por `tipo = venda`, origem `pedido:{pedidoId}` (contrato da 6C1-A) e, nela, **só** a linha de receita
  `a_receber` numa conta `recebivel` com subtipo `ifood`. O resto da composição (dinheiro na gaveta, maquininha) não é tocado.
  Não depende de `dados.forma` nem de `prepaid_amount`.
- `valorRecebido = valor a receber` (original + ajustes) → `Liquidar` com chave `repasse_ifood:{chave}`; acontecimento
  `pagamento` "Repasse iFood do pedido #N (referência)"; `situacao = liquidado`, `situacaoFinanceira = recebido`. Nenhuma receita nova.
- `valorRecebido ≠ a receber` (taxa descontada, parcial, a mais) → nada é quitado nem vira despesa: linha `divergente` em
  `gestao_repasses_ifood`, devolvida com `situacaoFinanceira = a_receber` e listada em `recebiveis`.
- Erros: `CAMPO_INVALIDO` (sem chave, pedido, data, valor com até 2 casas ou `confirmacaoHumana`), `CONTA_INVALIDA` (não é banco
  ativo da loja: outra loja, recebível, caixa), `SEM_VENDA` 404, `SEM_RECEBIVEL` 404 (venda sem parte iFood a receber),
  `CONCILIAR` 409 (mais de uma linha iFood), `JA_ESTORNADO` 409, `JA_LIQUIDADO` 409 (já recebido por outra chave),
  `IDEMPOTENCIA_CONFLITO` 409, `MES_FECHADO` e data futura vêm do livro.
- Idempotência: a mesma chave com o mesmo conteúdo devolve o mesmo resultado (`repetido = true`); concorrência com chaves
  diferentes: o livro trava o lançamento e só uma entra. Se cair entre o `Liquidar` e o registro, repetir a mesma chave completa.
- Migração `Migrations/Delivery/20261118_01_gestao_repasse_ifood.sql` (aditiva, idempotente, imutável por gatilho): quem confirmou,
  comprovante, valor que caiu, situação, ligação venda/lançamento/pagamento. Um `liquidado` por lançamento (índice único).

## Testes

`Tests/Integration/GestaoRepasseIfoodDatabaseTests.cs` (PostgreSQL 18 local, porta 55432, venda publicada direto no livro):
venda R$ 100 → a receber; repasse → banco R$ 100 e receita continua R$ 100; replay; conflito de chave; outra chave recusada;
6 repasses concorrentes (1 entra) e 4 com a mesma chave (1 pagamento); conta de outra loja e recebível recusados; pedido sem
venda; venda estornada; parcial R$ 60 de R$ 80 fica divergente e depois R$ 80 quita; offline R$ 30 gaveta + R$ 70 iFood (R$ 100
diverge, R$ 70 quita, gaveta intacta); venda só em dinheiro; DELETE recusado.
`dotnet build` ok; `dotnet test --filter Gestao`: **230 de 230**, 0 ignorados.

Não testado: as rotas por HTTP (permissão de gerente só no controller, sem teste HTTP); o Painel não tem tela para isso.

## Decisões e o que falta (para ANDAMENTO/PENDENCIAS)

- **Decisão do dono:** o iFood repassa líquido de comissão e por período, não por pedido. Hoje todo repasse com taxa fica
  `divergente`. Falta decidir como a taxa vira despesa (`taxas`) com evidência (extrato/relatório do iFood) e se haverá baixa em
  lote por período. Quem: **dono** + **Lógica**.
- **Integração:** automação do repasse pela API Financeira do iFood (escopo e homologação novos). Quem: **Integração**.
- **Painel:** tela de recebíveis iFood e confirmação (não feita; Caixa não mudou). Quem: **Painel**.
- **Consolidação:** aplicar a migração `20261118_01`; mover o contrato acima para CONTRATOS; conferir que a 6C1-A publica a venda
  iFood com a parte online no recebível `ifood` e a parte paga na entrega fora dele.
