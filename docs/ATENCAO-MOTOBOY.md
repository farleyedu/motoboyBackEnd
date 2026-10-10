# Atenção do motoboy — 10/10/2026

A seleção é feita no painel em **Cardápio → Gestão de produtos → Atenção do motoboy**. O atendente escolhe produtos e adicionais que precisam de cuidado na retirada. A atualização salva somente essa escolha, sem sobrescrever preço, descrição, composição ou disponibilidade.

## Contrato e publicação

Aplicar `Migrations/Delivery/20261010_02_cardapio_atencao_motoboy.sql` antes de disponibilizar os novos frontends. O runner existente inclui esse arquivo quando `DeliveryTracking:ApplyMigrationsOnStartup` está habilitado. A migration é aditiva e reaplicável; produtos e grupos de adicionais começam com `atencao_motoboy=false`, sem classificação automática pelo nome.

Os endpoints retornam o envelope `ApiResponse`:

| Operação | Endpoint | Permissão |
| --- | --- | --- |
| Consultar IDs selecionados | `GET /api/cardapio/{estabelecimentoId}/atencao-motoboy` | Cardapio/visualizar |
| Selecionar/desmarcar item | `PATCH /api/cardapio/{estabelecimentoId}/atencao-motoboy/{tipo}/{itemId}` com `{ "atencao": true/false }` | Cardapio/editar |

`tipo` aceita `produtos` ou `adicionais`. A consulta retorna `{ produtos: [], adicionais: [] }`. Loja é revalidada contra a sessão e os UPDATEs exigem o mesmo estabelecimento e item não excluído. Tipo inválido ou escolha ausente retorna 400; loja fora da sessão, 403; item ausente nessa loja, 404.

O painel trata os adicionais globais como grupos. A seleção do grupo vale para os IDs do próprio grupo e dos seus itens, sempre dentro da loja. Produtos usam `ProdutoId`. O JSON legado preserva `produtoId/produto_id/productId` e IDs de adicionais, quando presentes. Pedidos externos sem vínculo por ID continuam com a conferência normal; não recebem atenção por semelhança de nome. Flags recebidas do cliente são ignoradas.

## Conferência e fluxo

Detalhe do pedido e validação transacional de retirada/entrega consultam a mesma seleção. `checklist.items[].extra` indica atenção explícita, não significa que qualquer ingrediente seja um volume extra. Quantidades de adicionais seguem a quantidade do produto.

O motoboy vê os itens destacados no pedido. Ao sair da loja, um resumo agrupado por pedido reúne somente esses itens para uma única confirmação. Sem itens destacados, não há essa segunda interação. Pedidos sem detalhes usam uma confirmação manual de conteúdo/volumes, sem extras inventados. A entrega também dispensa a segunda conferência quando não há destaques e mantém código, recebimento, foto obrigatória e arraste final conforme a política do pedido.

Alterar atenção, quantidade ou conteúdo invalida a versão anterior da conferência; trocar apenas a foto não invalida. A API rejeita omissões, chaves extras e versões antigas. A nova seleção não pode ser contornada por nome, flag enviada pelo cliente ou ID de outra loja.

## Validação e limites

Os testes novos exercitam seleção/desmarcação persistida em PostgreSQL local isolado, isolamento entre lojas, IDs de grupo/opção, quantidades, exclusão de flags do cliente, versões e pedidos sem destaques. O teste transacional existente foi atualizado para usar um adicional explicitamente selecionado e conservar rollback e pagamento somente no gesto final.

**49 testes relacionados passaram, zero ignorados**, com relatório local em `obj/attention-validation/results/attention-flow.trx`. A execução ampliada encontrou as duas falhas financeiras de offset `-03:00` em `MotoboyWorkService.AddPeriod` já documentadas em WEB-IOS.md; elas foram excluídas do filtro direcionado, sem alteração financeira nesta tarefa. As falhas de conexão iniciais do ambiente de QA foram corrigidas usando a role existente do cluster local, sem acessar produção.

Os testes de navegador do app usam o bundle de produção e API/GPS controlados. O teclado é simulado pelo `visualViewport`; esse roteiro não comprova comportamento do teclado no iPhone físico. Sem deploy ou alteração em banco de produção nesta implementação.
