BEGIN;

-- Desfaz 20260926_01_produto_atendimento.sql. DESTRUTIVO: apaga apelidos, instrucoes, restricoes
-- e tempos extras cadastrados. O cardapio em si (produtos, precos) nao e tocado.
DROP TABLE IF EXISTS cardapio_produto_atendimento;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260926_01_produto_atendimento';

COMMIT;
