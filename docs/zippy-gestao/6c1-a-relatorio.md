# 6C1-A — Vendas dos pedidos no livro

Sessão A da 6C1, feita no clone `motoboyBackEnd-6c1a` (`master`, sem push). Para consolidar em ANDAMENTO/PENDENCIAS.

## O que mudou

- **Ponto central**: `GestaoLogica/Vendas/VendaDoPedido.cs`. `PublicarAsync` grava a venda dentro da transação da conclusão
  (`IAcontecimentosService.Publicar(conexao, transacao, ...)`); `EstornarAsync` estorna dentro da transação do cancelamento.
  `VendaRegras` (pura) traduz a forma de pagamento e monta a composição.
- **Livro**: nova sobrecarga `Estornar(NpgsqlConnection, NpgsqlTransaction, ...)` em `Acontecimentos/AcontecimentoService.cs`
  (aditiva; o `Estornar` antigo passa a usar o mesmo planejamento).
- **Conclusões ligadas** (todas na mesma transação que muda o pedido para 3):
  | Caminho | Modalidade | Pagamento usado |
  | --- | --- | --- |
  | Fila: operador (`CompleteCurrentAsync`, `CompleteStopAsync`), motoboy legado (`DeliverCurrentAsync`) | delivery | texto do pedido |
  | App do motoboy (`CompleteDeliveryAsync`) | delivery | pagamentos conferidos no app |
  | Manual (`MarcarEntregueManualAsync`): pendente → balcão / em rota → entrega | retirada / delivery | texto do pedido |
  | iFood entrega oficial (`FinalizeOfficialCompletion`, worker e reconciliação pelo evento CONCLUDED) | delivery | pré-pago iFood + conferidos |
  | iFood retirada oficial (`FinalizeOfficialPickup`) | retirada | pré-pago iFood + conferidos |
  `CompleteCurrentInternalAsync` agora exige o livro como parâmetro: um caminho novo de conclusão não compila sem decidir a venda.
  A projeção iFood (singleton) e o repositório criam um livro só de transação quando o DI não injeta (`LivroDaTransacao`).
- **Cancelamento autorizado de pedido entregue**: `POST /api/v2/delivery/pedidos/{id}/cancelar-concluido` `{ motivo }`
  (permissões `Delivery:atribuir_motoboy` **e** `Financeiro:cancelar`). Exige motivo, recusa pedido de outra loja e do iFood
  (segue o fluxo oficial), status 3→4 e estorno na mesma transação. O cancelamento comum continua recusando pedido concluído.

## Contrato da venda

- Tipo `venda`, módulo `pedidos`, origem `pedido:{id}`, chave `venda:pedido:{id}`, responsável = quem concluiu (operador, usuário do
  motoboy, quem confirmou no iFood; `null` no `DeliverCurrentAsync` legado), data do fato = hora da transação.
- Valor: iFood usa `order_amount` do snapshot oficial; demais, `pedido.value` (já com desconto e taxa). Valor ilegível → venda sem
  lançamento e `pendencias: ["valor_desconhecido"]`.
- `dados`: `forma` (a maior parte, só se conhecida), `modalidade`, `origemPedido`, `total`, `pagamentos: [{forma, valor, situacao,
  formaConfirmada}]`, `ifoodTipo` (iFood), `pendencias` (quando houver).
- Um lançamento de receita por forma:
  | Forma | Conta | Situação |
  | --- | --- | --- |
  | din | gaveta (`gestao_caixas.conta_gaveta_id`) **com o caixa de hoje aberto** | recebido hoje |
  | din sem caixa configurado / caixa fechado | nenhuma | a receber, `sem_conta_caixa` / `caixa_fechado` |
  | pix | recebível maquininha | recebido hoje (sem conta: a receber, `sem_conta_maquininha`) |
  | deb, cre | recebível maquininha | a receber |
  | ifood (pré-pago) | recebível iFood | a receber (o repasse é da sessão B) |
  | desconhecida | nenhuma | a receber, `forma_desconhecida` |
- Idempotência: trava pela chave + consulta da venda da origem antes de publicar; venda existente (ou estornada) não publica de
  novo. O índice de origem do livro segura o resto. Pedido do simulador (`origem = 'simulador'`) não gera venda.
- Estorno: chave `estorno:venda:pedido:{id}`, motivo e responsável humano obrigatórios, ligado à venda; a venda original fica.

## Testes (`Tests/Integration/GestaoVendaPedidoDatabaseTests.cs`, PostgreSQL 18 local, porta 55432)

Passaram 7 de 8: venda mista do app (din na gaveta, pix na maquininha, replay = 1 venda, saldo da gaveta), operador em
dinheiro sem caixa (a receber, nada inventado) e pedido sem forma, balcão com modalidade retirada e cartão a conferir, erro do livro
desfaz a conclusão, cancelar antes = nenhuma venda / cancelar depois = um estorno ligado e idempotente, 4 publicações concorrentes +
replay = 1 venda e loja B não alcança o pedido, iFood com pré-pago a receber + dinheiro recebido.

Não rodou neste ambiente: `Venda6C1A_retirada_ifood_oficial_publica_uma_venda` — a fixture iFood falha na importação
(`IfoodIntegrationException` de acesso), **igual nos testes antigos de retirada no commit base `9ba4276`**. Precisa rodar onde a
suíte iFood de banco passa.

Validação: `dotnet build` sem erro; `dotnet test --filter Gestao` 229/229; unitários + delivery 2352 ok e 13 falhas em
`DeliverySyncDatabaseTests` que falham igual no commit base (colunas/tabelas de migrações ausentes na fixture).

## Para as outras sessões e a consolidação

- **Sessão C (Caixa)**: a fita usa um único `dados.forma` para todas as receitas da venda. Em venda mista, ler a forma de cada linha
  em `dados.pagamentos` (mesma ordem dos lançamentos). Dinheiro com caixa fechado fica a receber sem conta: dá para liquidar na
  gaveta quando abrir.
- **Sessão B (repasse)**: o pré-pago iFood entra como receita a receber no recebível iFood, valor bruto (`order_amount` −
  cobrado na entrega); comissão e taxa ficam para a liquidação.

## Decisões que o dono precisa confirmar

1. **Dinheiro sem caixa aberto**: hoje fica a receber sem conta (não muda saldo nem abre caixa). Alternativa: aceitar na gaveta mesmo
   com o dia fechado.
2. **Cartão sem dizer débito ou crédito** (`cartao_entrega`, texto antigo): registrado como `cre` com `formaConfirmada: false`.
3. **Forma desconhecida** (`link`, `outro`, `pagoApp`, vazio): venda a receber sem conta e sem `forma`.
4. **Pix recebido na maquininha**, inclusive Pix online do cardápio (que cai no Mercado Pago). Precisa de conta própria do MP?
5. **iFood cancelado depois de concluído**: a projeção põe o pedido em 4, mas o estorno do livro exige pessoa responsável, então
   a venda **não é estornada sozinha**. Decidir: estorno pelo gerente numa fila de pendências, ou responsável "sistema".
6. **iFood concluído sem conclusão local** (entrega pela logística do iFood): a projeção põe status 6 e **não há venda**. Decidir se
   vira venda (100% recebível iFood).
7. **Salão**: o pedido não guarda salão/mesa; nenhuma conclusão gera `salao` hoje.
8. **Mais de uma maquininha ou recebível iFood**: vale a primeira conta ativa criada.
9. **Simulador**: reabrir pedido concluído no simulador não estorna (pedido do simulador não gera venda).
10. Remuneração do motoboy continua registrada quando o pedido entregue é cancelado.
