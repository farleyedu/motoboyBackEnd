-- Desfaz 20261019_01_ingredientes.sql. Destrutivo: apaga os ingredientes e o historico de custo.
-- So rode se nenhuma receita, compra ou fechamento depender deles.
BEGIN;
DROP TABLE IF EXISTS insumo_custo;
DROP TABLE IF EXISTS insumo;
COMMIT;
