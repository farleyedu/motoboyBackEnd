# Zippy Gestão (backend)

- `CONTRATOS.md`: cópia igual à do Painel. Mudou o contrato? Atualize as duas.
- Andamento e pendências das frentes ficam só no Painel: `zippy-admin/docs/zippy-gestao/ANDAMENTO.md` e `PENDENCIAS.md`.
- Fundação (livro de acontecimentos): `Acontecimentos/`, `Security/GestaoPapeis.cs`, `Controllers/GestaoAcontecimentosController.cs`,
  `Migrations/Delivery/20261105_01_gestao_fundacao.sql`.
- Lógica (CONTRATOS §9), em `GestaoLogica/`:

  | Pasta | Etapa | Controller | Migração |
  | --- | --- | --- | --- |
  | `Documentos/` | E1 documentos de compra | `GestaoDocumentosController.cs` | `20261106_01_gestao_documentos.sql` |
  | `Vinculos/` | E2 vínculos e curadoria | `GestaoVinculosController.cs` | `20261107_01_gestao_vinculos.sql` |
  | `Compras/`, `Estoque/` | E3 compra, conferência, perda, inventário, baixa | `GestaoComprasEstoqueController.cs` | `20261108_01_gestao_estoque.sql` |
  | `Equipe/` | E4 equipe e remuneração | `GestaoEquipeController.cs` | `20261109_01_gestao_equipe.sql` |
  | `Caixa/` | E5 caixa do dia | `GestaoCaixaController.cs` | `20261110_01_gestao_caixa.sql` |
  | `Despesas/` | E6 contas fixas e finalidades | `GestaoDespesasController.cs` | `20261111_01_gestao_recorrencias.sql` |
- Testes de banco: defina `TEST_DELIVERY_DATABASE` com um PostgreSQL local e rode `dotnet test --filter Gestao`.
