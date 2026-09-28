-- Verificacao de cardapio_produto_atendimento. Somente leitura; nao e aplicada automaticamente.

SELECT to_regclass('cardapio_produto_atendimento') AS tabela_atendimento;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version IN ('20260926_01_produto_atendimento', '20261001_03_produto_atendimento')
 ORDER BY version;
