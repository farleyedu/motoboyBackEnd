# Zippy Gestão (backend)

- `CONTRATOS.md`: cópia igual à do Painel. Mudou o contrato? Atualize as duas.
- Andamento e pendências das frentes ficam só no Painel: `zippy-admin/docs/zippy-gestao/ANDAMENTO.md` e `PENDENCIAS.md`.
- Código: `Acontecimentos/`, `Security/GestaoPapeis.cs`, `Controllers/GestaoAcontecimentosController.cs`,
  `Migrations/Delivery/20261105_01_gestao_fundacao.sql`.
- Lógica: `GestaoLogica/Documentos/` (E1) e `GestaoLogica/Vinculos/` (E2), rotas em `Controllers/GestaoDocumentosController.cs` e
  `Controllers/GestaoVinculosController.cs`, migrações `20261106_01_gestao_documentos.sql` e `20261107_01_gestao_vinculos.sql`.
- Testes de banco: defina `TEST_DELIVERY_DATABASE` com um PostgreSQL local e rode `dotnet test --filter Gestao`.
