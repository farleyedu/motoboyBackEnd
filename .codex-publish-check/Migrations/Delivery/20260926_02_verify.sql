-- Verificacao da Fase 3 (ficha de atendimento). Somente leitura; nao e aplicada automaticamente.

-- 1) A tabela existe (deve listar 1 linha).
SELECT to_regclass('cardapio_produto_atendimento') AS tabela;

-- 2) Atendimento sem produto (a FK com CASCADE deveria ter impedido).
SELECT a.produto_id
  FROM cardapio_produto_atendimento a
  LEFT JOIN cardapio_produto p ON p.id = a.produto_id
 WHERE p.id IS NULL;

-- 3) Atendimento de um estabelecimento diferente do produto (nunca deveria acontecer).
SELECT a.produto_id, a.id_estabelecimento AS atendimento_estab, p.id_estabelecimento AS produto_estab
  FROM cardapio_produto_atendimento a
  JOIN cardapio_produto p ON p.id = a.produto_id
 WHERE a.id_estabelecimento IS DISTINCT FROM p.id_estabelecimento;

SELECT version, applied_at_utc
  FROM delivery_tracking_schema_versions
 WHERE version LIKE '20260926%'
 ORDER BY applied_at_utc, version;
